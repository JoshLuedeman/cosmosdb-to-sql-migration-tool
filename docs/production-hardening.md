# Production Hardening Guide

This guide describes how to deploy and operate the **Cosmos DB to SQL Migration Assessment Tool** in production-style Azure environments without static secrets, while meeting common enterprise security baselines.

It is the umbrella document for parent issue [#128](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/128). Sections are landed incrementally as the parent's sub-issues close:

| Sub-issue | Section | Status |
|---|---|---|
| [#199](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/199) | Managed identity setup (this section) | ✅ |
| [#200](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/200) | Azure Key Vault integration for non-Microsoft Entra secrets | coming next |
| [#201](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/201) | Network isolation (Private Endpoints, VNet integration) | coming next |
| [#202](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/202) | Least-privilege custom RBAC role definitions (JSON) | coming next |
| [#203](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/203) | Secret rotation procedures and audit logging | coming next |
| [#204](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/204) | Production-readiness checklist (security review gate) | coming next |

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

---

## References

- [Use a Microsoft Entra Workload ID on Azure Kubernetes Service (AKS)](https://learn.microsoft.com/en-us/azure/aks/workload-identity-overview)
- [Managed identities in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity)
- [Managed identities for Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity)
- [Connect to Azure Cosmos DB for NoSQL using role-based access control](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control)
- [DefaultAzureCredential class (Azure.Identity)](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential)
- Existing tool docs: [Azure Permissions](azure-permissions.md), [Getting Started](getting-started.md), [Troubleshooting](troubleshooting.md)
