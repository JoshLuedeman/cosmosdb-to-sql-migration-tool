# Production Hardening Guide

This guide describes how to deploy and operate the **Cosmos DB to SQL Migration Assessment Tool** in production-style Azure environments without static secrets, while meeting common enterprise security baselines.

It is the umbrella document for parent issue [#128](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/128). Sections are landed incrementally as the parent's sub-issues close:

| Sub-issue | Section | Status |
|---|---|---|
| [#199](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/199) | Managed identity setup (this section) | ✅ |
| [#200](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/200) | Azure Key Vault integration for non-Microsoft Entra secrets | ✅ — see [Secrets Management](secrets-management.md) |
| [#201](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/201) | Network isolation (Private Endpoints, VNet integration) | ✅ |
| [#202](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/202) | Least-privilege custom RBAC role definitions (JSON) | ✅ — see [`security/rbac/`](security/rbac/README.md) |
| [#203](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/203) | Secret rotation procedures and audit logging | ✅ — see [Secret Rotation and Audit Logging](secret-rotation-and-audit.md) |
| [#204](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/204) | Production-readiness checklist (security review gate) | ✅ — see [Production-readiness checklist](production-readiness-checklist.md) |

---

> 🛑 **Before going to production: walk the [production-readiness checklist](production-readiness-checklist.md).** It's the security-review gate that ties together every recommendation in this guide and its companions.

---

## What identity the tool needs at runtime

Both `Services/CosmosDbAnalysisService.cs` and `Services/DataQualityAnalysisService.cs` construct the Cosmos NoSQL SDK with a `TokenCredential`:

```csharp
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = false, ... });
_cosmosClient = new CosmosClient(cosmosEndpoint, credential);
```

`DataQualityAnalysisService.AnalyzeContainerAsync` then issues `container.GetItemQueryIterator<JsonDocument>(query)` calls to sample documents for quality scoring.

That means the running identity needs **all four** of the following grants. The classic ARM "Cosmos DB Account Reader" role is **not sufficient** on its own — the NoSQL SDK constructed with a `TokenCredential` requires Cosmos DB's separate **data-plane SQL RBAC**:

| # | Role | Plane | Scope | Why this tool needs it |
|---|---|---|---|---|
| 1 | `Cosmos DB Built-in Data Reader` (`00000000-0000-0000-0000-000000000001`) | Cosmos DB SQL RBAC (data plane) | Cosmos DB account | NoSQL SDK metadata reads (`readMetadata`) + document reads for data-quality sampling |
| 2 | `Reader` (built-in) | Azure RBAC (control plane) | Cosmos DB account or its resource group | Discovering diagnostic settings and resource metadata via the ARM endpoints |
| 3 | `Log Analytics Reader` (built-in) | Azure RBAC | Log Analytics workspace | `LogsQueryClient` queries of the 6-month diagnostic logs |
| 4 | `Monitoring Reader` (built-in) | Azure RBAC | Cosmos DB account or resource group | Azure Monitor metrics collection |

> Custom role JSON for these grants will land in [#202](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/202). For now use the built-ins above.

`DefaultAzureCredential` walks a chain of credential types. In production the chain should resolve to a **managed identity** so the tool runs without secrets on disk or in environment variables.

---

## Choosing between system-assigned and user-assigned managed identity

| Scenario | Recommendation |
|---|---|
| One-off run on a single VM, App Service, Functions app, or Container App | **System-assigned** — simpler, lifecycle tied to the host, no extra resource to manage |
| AKS pods (Microsoft Entra Workload ID) | **User-assigned** — workload identity federation requires it |
| Multiple hosts share the same permissions (fleet, blue/green, multi-region) | **User-assigned** — assign once, attach to many hosts |
| Permissions must exist before the host is created (pre-provisioning, IaC pipelines) | **User-assigned** — its object ID is stable across host re-creates |
| Tightly-scoped, short-lived, ephemeral worker | **System-assigned** — deleted with the host |

If you mix both, set `AZURE_CLIENT_ID` on the host to the **user-assigned** identity's client ID so `DefaultAzureCredential` selects it deterministically.

---

## Managed identity setup per host

> The four role assignments at the top of this guide apply in every example below. After creating the identity, capture its **principal/object ID** and assign all four roles to it before running the tool.
>
> Tip: Microsoft Graph can take 30–60 seconds to propagate a new managed identity. If `az role assignment create --assignee <id>` fails with a graph lookup error, prefer `--assignee-object-id <principalId> --assignee-principal-type ServicePrincipal` (which skips the graph lookup), and retry.

### Azure Virtual Machine

Single VM, system-assigned:

```bash
RG=rg-cosmos-assessment
VM=vm-assessment
SUB=$(az account show --query id -o tsv)
COSMOS=contoso-cosmos
WORKSPACE=law-cosmos

# Enable system-assigned managed identity
PRINCIPAL_ID=$(az vm identity assign \
  --resource-group $RG --name $VM \
  --query systemAssignedIdentity -o tsv)

# 1. Cosmos DB data-plane: Built-in Data Reader
az cosmosdb sql role assignment create \
  --account-name $COSMOS --resource-group $RG \
  --scope "/" \
  --principal-id $PRINCIPAL_ID \
  --role-definition-id 00000000-0000-0000-0000-000000000001

# 2. ARM control-plane: Reader on the Cosmos account
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Reader" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS"

# 3. Log Analytics Reader on the workspace
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Log Analytics Reader" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.OperationalInsights/workspaces/$WORKSPACE"

# 4. Monitoring Reader on the resource group
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Monitoring Reader" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG"
```

### Azure Virtual Machine Scale Set (VMSS)

```bash
PRINCIPAL_ID=$(az vmss identity assign \
  --resource-group $RG --name $VMSS \
  --query systemAssignedIdentity -o tsv)
# Then assign the same four roles as above.
```

For user-assigned, pre-create the identity with `az identity create`, then attach with `az vm identity assign --identities <uami-resource-id>` / `az vmss identity assign --identities <uami-resource-id>` and set `AZURE_CLIENT_ID=<uami-client-id>` on the host so `DefaultAzureCredential` selects it.

### Azure App Service

```bash
# Web App
PRINCIPAL_ID=$(az webapp identity assign \
  --resource-group $RG --name $WEBAPP \
  --query principalId -o tsv)
# Then assign the four roles using $PRINCIPAL_ID as above.
```

User-assigned:

```bash
UAMI_ID=$(az identity show --resource-group $RG --name uami-assessment --query id -o tsv)
UAMI_CLIENT_ID=$(az identity show --resource-group $RG --name uami-assessment --query clientId -o tsv)

az webapp identity assign --resource-group $RG --name $WEBAPP --identities $UAMI_ID
az webapp config appsettings set --resource-group $RG --name $WEBAPP \
  --settings AZURE_CLIENT_ID=$UAMI_CLIENT_ID
```

### Azure Functions

```bash
# System-assigned
PRINCIPAL_ID=$(az functionapp identity assign \
  --resource-group $RG --name $FUNCAPP \
  --query principalId -o tsv)
# Then assign the four roles as above.
```

User-assigned uses the same `--identities` + `AZURE_CLIENT_ID` pattern as App Service.

### Azure Container Apps

System-assigned:

```bash
PRINCIPAL_ID=$(az containerapp identity assign \
  --resource-group $RG --name $CAAPP \
  --system-assigned \
  --query principalId -o tsv)
```

User-assigned (preferred when the same image runs in multiple environments):

```bash
az identity create --resource-group $RG --name uami-assessment
UAMI_ID=$(az identity show --resource-group $RG --name uami-assessment --query id -o tsv)
UAMI_CLIENT_ID=$(az identity show --resource-group $RG --name uami-assessment --query clientId -o tsv)

az containerapp identity assign \
  --resource-group $RG --name $CAAPP \
  --user-assigned $UAMI_ID

# Tell DefaultAzureCredential which UAMI to use
az containerapp update \
  --resource-group $RG --name $CAAPP \
  --set-env-vars AZURE_CLIENT_ID=$UAMI_CLIENT_ID
```

Then assign all four roles to `$UAMI_CLIENT_ID`'s principal ID (`az identity show --query principalId -o tsv`).

> **Init-container limitation:** init containers cannot access managed identities in consumption-only or dedicated workload-profile environments. Run the assessment in a regular container, not an init container. See the [Container Apps managed identity docs](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity#limitations).

### Azure Kubernetes Service — Microsoft Entra Workload ID

AKS uses Microsoft Entra **Workload Identity** (the OIDC-federation model). The legacy AAD Pod Identity is **deprecated** and should not be used for new deployments. See the [AKS Workload ID overview](https://learn.microsoft.com/en-us/azure/aks/workload-identity-overview).

There are six moving parts. Missing any one of them will cause `DefaultAzureCredential`/`WorkloadIdentityCredential` to fail at runtime:

```bash
RG=rg-cosmos-assessment
AKS=aks-assessment
NAMESPACE=cosmos-assessment
SA_NAME=assessment-sa
UAMI=uami-assessment-aks
SUB=$(az account show --query id -o tsv)

# 1. Enable OIDC issuer + workload identity on the cluster
az aks update --resource-group $RG --name $AKS \
  --enable-oidc-issuer --enable-workload-identity

OIDC_ISSUER=$(az aks show --resource-group $RG --name $AKS --query oidcIssuerProfile.issuerUrl -o tsv)

# 2. Create the user-assigned managed identity
az identity create --resource-group $RG --name $UAMI
UAMI_CLIENT_ID=$(az identity show --resource-group $RG --name $UAMI --query clientId -o tsv)
UAMI_PRINCIPAL_ID=$(az identity show --resource-group $RG --name $UAMI --query principalId -o tsv)

# 3. Assign the four Azure roles to $UAMI_PRINCIPAL_ID (see Cosmos / Reader / Log Analytics Reader / Monitoring Reader commands at the top of this section)

# 4. Create a federated identity credential binding the UAMI to the K8s service account
az identity federated-credential create \
  --name $UAMI-fed \
  --identity-name $UAMI \
  --resource-group $RG \
  --issuer $OIDC_ISSUER \
  --subject "system:serviceaccount:${NAMESPACE}:${SA_NAME}" \
  --audience "api://AzureADTokenExchange"
```

Then in the cluster:

```yaml
# 5. Service account annotated with the UAMI's client ID
apiVersion: v1
kind: ServiceAccount
metadata:
  name: assessment-sa
  namespace: cosmos-assessment
  annotations:
    azure.workload.identity/client-id: <UAMI_CLIENT_ID>
---
# 6. Pod template labeled to opt into workload identity injection
apiVersion: batch/v1
kind: Job
metadata:
  name: cosmos-assessment-run
  namespace: cosmos-assessment
spec:
  template:
    metadata:
      labels:
        azure.workload.identity/use: "true"
    spec:
      serviceAccountName: assessment-sa
      restartPolicy: Never
      containers:
        - name: assessment
          image: <your-registry>/cosmos-assessment:1.0.0
          args: ["--test-connection", "--endpoint", "https://contoso-cosmos.documents.azure.com:443/"]
```

The workload identity mutating webhook injects `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE`, and `AZURE_AUTHORITY_HOST` into the pod automatically — `DefaultAzureCredential` (Azure.Identity ≥ 1.9.0, which this project already references) picks them up via `WorkloadIdentityCredential` without code changes.

---

## DefaultAzureCredential behaviour on each host

`DefaultAzureCredential` tries credential types in a fixed order and returns the first one that yields a token. Knowing the order — and the host environment that satisfies each — prevents surprise auth bindings:

| Order | Credential | Triggers when… | Typical host |
|---|---|---|---|
| 1 | `EnvironmentCredential` | `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET` + `AZURE_TENANT_ID` (or cert vars) are all set | CI build agents, legacy hosts (⚠️ see footgun below) |
| 2 | `WorkloadIdentityCredential` | `AZURE_FEDERATED_TOKEN_FILE` + `AZURE_CLIENT_ID` + `AZURE_TENANT_ID` + `AZURE_AUTHORITY_HOST` are set (the workload-identity webhook does this) | AKS pods with workload identity |
| 3 | `ManagedIdentityCredential` | IMDS endpoint reachable | VM, VMSS, App Service, Functions, Container Apps |
| 4 | `SharedTokenCacheCredential` / `VisualStudioCredential` / `AzureCliCredential` / `AzurePowerShellCredential` / `AzureDeveloperCliCredential` | Developer login present | Developer laptops |
| 5 | `InteractiveBrowserCredential` | Only if explicitly enabled (`ExcludeInteractiveBrowserCredential = false`) | Interactive debugging |

### Two production footguns

1. **Stray secret env vars shadow managed identity.** If a host still has `AZURE_CLIENT_SECRET` set (e.g. left behind from a service-principal migration), `EnvironmentCredential` wins at position 1 and your managed identity is never used. Audit the host's environment and explicitly unset `AZURE_CLIENT_SECRET` / `AZURE_USERNAME` / `AZURE_PASSWORD` when you switch to MI.
2. **Multiple user-assigned identities on one host.** When a host has more than one UAMI attached, `ManagedIdentityCredential` cannot guess which to use and will fail or pick a wrong one. Always set `AZURE_CLIENT_ID` to the UAMI's client ID so the SDK selects it deterministically.

---

## Smoke test from the host

After deploying, validate end-to-end **from the host**, not from your laptop, so you actually exercise the deployed identity.

### Primary — run the tool itself

The tool ships a `--test-connection` flag (`Program.cs` lines 949–991, 1188) that verifies Cosmos DB and Azure Monitor connectivity using the running identity:

```bash
dotnet run -- --test-connection \
  --endpoint "https://contoso-cosmos.documents.azure.com:443/" \
  --workspace-id "$WORKSPACE_ID" \
  --subscription-id "$SUB" \
  --resource-group "$RG" \
  --cosmos-account "$COSMOS"
```

This is the only smoke test that proves every component of the auth chain works end-to-end with the deployed identity.

### Secondary — Azure CLI diagnostics

If the tool fails, narrow down which grant is missing:

```bash
# Log in *as the managed identity* (only after this do the checks below test MI, not your user)
az login --identity

# Control plane — does Reader work?
az cosmosdb show --name "$COSMOS" --resource-group "$RG"

# Data plane — is the Cosmos SQL role assignment in place?
az cosmosdb sql role assignment list \
  --account-name "$COSMOS" --resource-group "$RG"

# Log Analytics
az monitor log-analytics query \
  --workspace "$WORKSPACE_ID" \
  --analytics-query "AzureDiagnostics | where ResourceProvider == 'MICROSOFT.DOCUMENTDB' | take 1"

# Monitoring
az monitor metrics list \
  --resource "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS" \
  --metric TotalRequests --interval PT1M
```

Each command maps one-to-one to one of the four role grants at the top of this guide. The first one that fails tells you which role is missing.

## Network isolation

The runtime tool talks to three Azure data planes — Cosmos DB, Log Analytics (query), and Microsoft Entra (token issuance). In production, those flows should never traverse the public internet. This section covers the private-network design for each, the host VNet integration that makes the host actually use private endpoints, and the DNS plumbing that ties them together.

### What this tool reaches over the network

| Endpoint | Protocol / port | DNS zone (when private) | Private Link surface | This tool's path |
|---|---|---|---|---|
| Cosmos DB NoSQL — `<account>.documents.azure.com` | TCP 443 (gateway), TCP 10250–10256 (direct mode if used) | `privatelink.documents.azure.com` (subresource `Sql`); dedicated gateway uses `privatelink.sqlx.cosmos.azure.com` | Private Endpoint on the Cosmos DB account | `CosmosClient` metadata + `GetItemQueryIterator<JsonDocument>` |
| Log Analytics query API — `api.loganalytics.io`, `*.ods.opinsights.azure.com` | TCP 443 | Provisioned by AMPLS — see below | Azure Monitor Private Link Scope (AMPLS) + Private Endpoint on the AMPLS | `Azure.Monitor.Query.LogsQueryClient` (query only — the tool does not ingest) |
| Microsoft Entra — `login.microsoftonline.com`, `*.identity.azure.net` | TCP 443 | Cannot be made private; reach via service tag `AzureActiveDirectory` | Service-tag firewall rule | All `DefaultAzureCredential` token requests |
| Azure Resource Manager — `management.azure.com` | TCP 443 | `privatelink.azure.com` (via **Resource Management Private Link**, a separate feature from AMPLS / Cosmos PE) | Resource Management Private Link | **Not used by the tool at runtime** (no `ArmClient`), but needed if you run the suggested `az cosmosdb show` / `az monitor` smoke-test diagnostics |

Microsoft Entra cannot be made private — keep `AzureActiveDirectory` reachable via Azure Firewall service tags or VNet outbound rules.

### Cosmos DB private endpoint

```bash
RG=rg-cosmos-assessment
VNET=vnet-assessment
PE_SUBNET=snet-pe-cosmos
COSMOS=contoso-cosmos
SUB=$(az account show --query id -o tsv)

# Provision the private endpoint targeting the Sql subresource
az network private-endpoint create \
  --name pe-cosmos-$COSMOS \
  --resource-group $RG \
  --vnet-name $VNET --subnet $PE_SUBNET \
  --private-connection-resource-id "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS" \
  --group-id Sql \
  --connection-name pe-conn-cosmos

# Create the private DNS zone (once per tenant/topology) and link it to the VNet
az network private-dns zone create \
  --resource-group $RG --name privatelink.documents.azure.com

az network private-dns link vnet create \
  --resource-group $RG --zone-name privatelink.documents.azure.com \
  --name link-$VNET --virtual-network $VNET --registration-enabled false

# Wire the PE NIC to the private DNS zone group so A-records are auto-created
az network private-endpoint dns-zone-group create \
  --resource-group $RG --endpoint-name pe-cosmos-$COSMOS \
  --name zg-cosmos --private-dns-zone privatelink.documents.azure.com \
  --zone-name privatelink.documents.azure.com

# Once verified end-to-end, lock down public access
az cosmosdb update --name $COSMOS --resource-group $RG \
  --public-network-access Disabled
```

**Multi-region accounts.** A single Cosmos PE/DNS-zone-group exposes records for both the global account FQDN (`<account>.documents.azure.com`) and each Azure region (`<account>-<region>.documents.azure.com`). Create one PE in **each client VNet** that needs private access (typically one per region for resiliency), not necessarily one per Cosmos region. When Cosmos regions are added or removed, the auto-managed private DNS zone group updates the A-records; manually-managed DNS does not — keep regions in sync if you bypass the zone group.

For accounts using the [Cosmos DB dedicated gateway](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/integrated-cache), repeat the steps above with `--group-id SqlDedicated` and DNS zone `privatelink.sqlx.cosmos.azure.com`. This tool itself does not use the dedicated gateway.

### Azure Monitor Private Link Scope (AMPLS)

The tool's `LogsQueryClient` calls `api.loganalytics.io` to query the workspace. Locking it down uses Azure Monitor **Private Link Scope** (AMPLS), not a direct per-resource private endpoint on the workspace.

```bash
WORKSPACE=law-cosmos
AMPLS=ampls-cosmos-assessment

# 1. Create AMPLS
az monitor private-link-scope create \
  --resource-group $RG --name $AMPLS

# 2. Associate the Log Analytics workspace
az monitor private-link-scope scoped-resource create \
  --resource-group $RG --scope-name $AMPLS \
  --name link-$WORKSPACE \
  --linked-resource "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.OperationalInsights/workspaces/$WORKSPACE"

# 3. Create the AMPLS private endpoint in your client VNet
az network private-endpoint create \
  --name pe-ampls-$AMPLS --resource-group $RG \
  --vnet-name $VNET --subnet $PE_SUBNET \
  --private-connection-resource-id "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Insights/privateLinkScopes/$AMPLS" \
  --group-id azuremonitor --connection-name pe-conn-ampls

# 4. AMPLS provisions DNS records for the full Monitor surface (5 zones).
# Use the all-zones private DNS zone group — this tool's query path
# primarily resolves under privatelink.monitor.azure.com, but the other zones
# (oms/ods/agentsvc/blob) are commonly provisioned together because they back
# ingestion and the wider Monitor experience.
for ZONE in privatelink.monitor.azure.com \
            privatelink.oms.opinsights.azure.com \
            privatelink.ods.opinsights.azure.com \
            privatelink.agentsvc.azure-automation.net \
            privatelink.blob.core.windows.net; do
  az network private-dns zone create --resource-group $RG --name $ZONE 2>/dev/null
  az network private-dns link vnet create --resource-group $RG \
    --zone-name $ZONE --name link-$VNET-$(echo $ZONE | tr '.' '-') \
    --virtual-network $VNET --registration-enabled false 2>/dev/null
done
```

**Two AMPLS postures — pick where you are on the rollout:**

| Posture | AMPLS `ingestionAccessMode` | AMPLS `queryAccessMode` | Workspace `publicNetworkAccessForQuery` | Use when |
|---|---|---|---|---|
| **Initial / compatibility** | `PrivateOnly` | `Open` | `Enabled` | You're onboarding; some legacy ingestion or query clients still need public access |
| **Locked-down** | `PrivateOnly` | `PrivateOnly` | `Disabled` | All query clients — including this tool, the portal, Power BI, and any automation — run from AMPLS-connected networks |

AMPLS access modes and workspace `publicNetworkAccessFor*` properties are independent. AMPLS controls who comes in *via the scope's private endpoint*; the workspace property controls whether the workspace accepts *any* public call at all. Lock down both for the fully isolated posture.

**Limits.** A VNet can link to at most **one AMPLS**, and an AMPLS supports up to **10 private endpoints** and **1,000 scoped resources**. If you operate a fleet of VNets, plan to share one AMPLS across them via PE-per-VNet rather than per-VNet AMPLSes.

### Key Vault private endpoint

If you use the Key Vault patterns from [`docs/secrets-management.md`](secrets-management.md), give Key Vault a private endpoint too:

- Subresource: `vault`
- DNS zone: `privatelink.vaultcore.azure.net`
- Then set `--public-network-access Disabled` on the vault

The setup pattern mirrors the Cosmos block above. See the [Secrets Management](secrets-management.md) doc for vault creation flags.

### Host VNet integration

Every private endpoint above is wasted if the host doesn't actually use the VNet for outbound DNS and traffic. Per host:

| Host | What to do | Critical setting / gotcha |
|---|---|---|
| **VM / VMSS** | Deploy NIC into the VNet with the PEs and DNS links | None beyond standard NSG/firewall rules |
| **Azure Functions / App Service** | Enable regional VNet integration: `az webapp vnet-integration add --name $WEBAPP --resource-group $RG --vnet $VNET --subnet snet-app-integration` | Set the modern `properties.outboundVnetRouting.allTraffic=true` (Azure-Policy-auditable). `vnetRouteAllEnabled=true` and legacy `WEBSITE_VNET_ROUTE_ALL=1` work but are backward-compat only. Without this, DNS resolves the **public** Cosmos FQDN even though the PE exists |
| **Azure Container Apps** | Deploy the environment with `--infrastructure-subnet-resource-id` pointing at a delegated subnet (delegation: `Microsoft.App/environments`). Workload-profile env requires `/27` minimum; Consumption requires `/23` | The environment's internal load balancer must be `internal` for full isolation. Outbound traffic to PEs goes through the env's outbound IP |
| **AKS** | Cluster VNet sees PE IPs natively; if PEs are in a peered VNet, ensure UDR / Azure Firewall allows the PE subnet | With **Azure CNI Overlay**, pod egress is SNATed to node IPs — firewall and NSG rules see node IPs, not pod IPs. DNS still works identically: CoreDNS must forward to a resolver that can resolve the linked private zones. Private DNS zones must be linked to the **cluster (node)** VNet, not only the PE VNet |

### DNS forwarding patterns

The single most common failure mode for "I configured the PE but the app still hits the public endpoint" is split-horizon DNS. Pick exactly one of the patterns below and validate at the host before assuming the PE works:

- **Azure-provided DNS (`168.63.129.16`)** with private DNS zones linked to the VNet — simplest. VMs use it by default; App Service / Container Apps inherit it as long as no custom DNS server is set on the VNet.
- **Custom DNS server in the VNet** (e.g. AD DS, BIND) — must conditionally forward each `privatelink.*` zone to `168.63.129.16`, or zone-transfer those records. Forgetting one zone silently sends some traffic public.
- **Azure DNS Private Resolver** — the modern hub-and-spoke pattern: deploy a Private Resolver in the hub VNet, set every spoke VNet's DNS to the resolver's inbound IPs, conditional-forward the `privatelink.*` zones at the resolver.

> **Split-horizon footgun.** If the host resolves `<account>.documents.azure.com` to a *public* IP (e.g. `40.x` or `52.x`), every request gets rejected by Cosmos with `503 ServiceUnavailable` or `403 ForbiddenByFirewall` after you set `publicNetworkAccess=Disabled` — even though the credential, RBAC, and PE are all correct. Always validate DNS resolution from the host before declaring private access "done."

### Cosmos DB firewall hardening

Once a PE is verified end-to-end:

```bash
az cosmosdb update --name $COSMOS --resource-group $RG \
  --public-network-access Disabled

# Optional: restrict to specific private endpoints (default is "allow all PEs")
az cosmosdb network-rule list --name $COSMOS --resource-group $RG
```

With `Disabled`, even a correctly authenticated AAD principal that reaches the public FQDN gets `403 ForbiddenByFirewall`. That is the desired end state: the public endpoint exists as a network address but accepts zero connections.

### Diagnostic recipe

When the tool times out or returns `503`/`403` in a private-networking deployment, walk the layers from the bottom up. Each command tells you which layer is broken:

```powershell
# DNS — must return a private IP (10.x / 172.16-31.x / 192.168.x)
nslookup <account>.documents.azure.com
nslookup api.loganalytics.io
# Same on Windows with richer CNAME/A-record output:
Resolve-DnsName <account>.documents.azure.com
Resolve-DnsName api.loganalytics.io

# TCP — must succeed against that private IP, not a public one
Test-NetConnection -Port 443 <account>.documents.azure.com
Test-NetConnection -Port 443 api.loganalytics.io

# Application — exercises Cosmos PE + AMPLS + AAD in one shot
dotnet run -- --test-connection \
  --endpoint "https://<account>.documents.azure.com:443/" \
  --workspace-id "$WORKSPACE_ID" \
  --subscription-id "$SUB" \
  --resource-group "$RG" \
  --cosmos-account "$COSMOS"
```

If `nslookup` returns a public IP, the DNS plumbing is wrong (private DNS zone not linked to the host's VNet, or a custom DNS server is shadowing the private zone). If DNS is private but `Test-NetConnection` fails, the routing/firewall is wrong (NSG, UDR, or Azure Firewall blocking the PE subnet). If both succeed and `--test-connection` still fails, the problem is identity/RBAC — see the smoke-test section above.

---

## Custom RBAC role definitions

The "four required role grants" above can be satisfied with Azure built-in roles (`Cosmos DB Built-in Data Reader`, `Reader`, `Log Analytics Reader`, `Monitoring Reader`) — fine for proof-of-concept work but typically over-granting for audit or governance.

For tenants that need bounded permissions, this repo ships four ready-to-edit least-privilege role definitions under [`docs/security/rbac/`](security/rbac/README.md):

| File | Purpose | Plane | Deploy with |
|---|---|---|---|
| `cosmos-assessment-data-reader.json` | NoSQL data-plane reads (metadata, items, query, change-feed) | Cosmos DB data plane | `az cosmosdb sql role definition create` |
| `cosmos-assessment-arm-reader.json` | ARM read on the Cosmos account + diagnostic settings; omits `listKeys/action` so it cannot exfiltrate account keys | ARM control plane | `az role definition create` |
| `cosmos-assessment-monitor-reader.json` | Azure Monitor metrics + Log Analytics query; omits `*/read` and `workspaces/sharedKeys/action` | ARM control plane | `az role definition create` |
| `sql-schema-deployer.sql` | Database-scoped T-SQL role for `sqlpackage publish` steady-state schema deploys (use `db_owner` for first/bootstrap deploy) | Azure SQL DB | `sqlcmd` / SSMS / ADS |

Two important schema details that trip people up the first time:

1. **Cosmos data-plane role JSON is not Azure RBAC JSON.** It uses `RoleName` + `Type: "CustomRole"` + `Permissions[].DataActions` and is created with `az cosmosdb sql role definition create --body @file.json`, not `az role definition create`. The two schemas cannot be cross-deployed.
2. **The "Monitor Reader" role is intentionally split from the "ARM Reader" role.** Cosmos accounts and Log Analytics workspaces typically live in different resource groups, often in different subscriptions — keeping the roles separate means each role can be assigned at its true target scope without inheriting unrelated permissions through a common parent.

See [`docs/security/rbac/README.md`](security/rbac/README.md) for end-to-end deploy commands, placeholder-substitution instructions, the SQL deploy escalation matrix (when `db_owner` vs `db_schema_deployer` is appropriate), and what was deliberately omitted from each role versus its built-in equivalent.

---

## References

- [Use a Microsoft Entra Workload ID on Azure Kubernetes Service (AKS)](https://learn.microsoft.com/en-us/azure/aks/workload-identity-overview)
- [Managed identities in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity)
- [Managed identities for Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity)
- [Connect to Azure Cosmos DB for NoSQL using role-based access control](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control)
- [DefaultAzureCredential class (Azure.Identity)](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential)
- [Configure private endpoints for Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-configure-private-endpoints)
- [Use Azure Private Link to connect networks to Azure Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-security)
- [Configure private link for Azure Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-configure)
- [Use a private endpoint with an Azure Container Apps environment](https://learn.microsoft.com/en-us/azure/container-apps/how-to-use-private-endpoint)
- [Integrate App Service apps with an Azure virtual network](https://learn.microsoft.com/en-us/azure/app-service/configure-vnet-integration-enable)
- [Azure Private Endpoint DNS configuration](https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-dns)
- [Azure custom roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles)
- [Azure RBAC resource provider operations](https://learn.microsoft.com/en-us/azure/role-based-access-control/resource-provider-operations)
- Existing tool docs: [Azure Permissions](azure-permissions.md), [Secrets Management](secrets-management.md), [Custom RBAC role definitions](security/rbac/README.md), [Secret Rotation and Audit Logging](secret-rotation-and-audit.md), [Production-readiness checklist](production-readiness-checklist.md), [Getting Started](getting-started.md), [Troubleshooting](troubleshooting.md)
