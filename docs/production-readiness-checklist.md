# Production-readiness checklist

A security-review gate for production rollouts of the **CosmosToSqlAssessment** tool. Each row is binary — if a box is unchecked, the rollout is not ready.

The checklist closes parent issue [#128](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/128) and assumes the preceding five sub-issues' guidance has been adopted: [Production Hardening](production-hardening.md), [Secrets Management](secrets-management.md), [Custom RBAC role definitions](security/rbac/README.md), [Secret Rotation and Audit Logging](secret-rotation-and-audit.md).

**How to use:** copy this file's tables into your change-management ticket or PR description, then tick boxes and paste evidence as you walk down. The **Evidence** column tells the reviewer exactly what string / screenshot / CLI output to attach so the decision is auditable later.

---

## One-time tool-adoption review

These rows are reviewed **once when the tool is first adopted**, and re-reviewed only on a tool upgrade or material change to the report-generation pipeline. They are *not* a per-deploy gate.

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | The approved tool version's security review confirms reports emit aggregate stats only — no sample document bodies. | Reviewer name + date + tool version SHA. Re-run on tool upgrade or change to `Services/ReportGeneration*`. | [`Services/ReportGenerationService.cs`](../Services/ReportGenerationService.cs) |
| [ ] | A data-flow / trust-boundary review has been completed for the chosen deployment pattern (which compute host, which network posture, which secret pattern). | Diagram or 1-page write-up attached to the adoption ticket. | [Production Hardening](production-hardening.md), [Network isolation](production-hardening.md#network-isolation) |

---

## Per-deployment checklist

Walk this before every production rollout, every promotion between environments, and every time the assessment scope materially changes (new Cosmos account, new SQL target, new tenant).

### 1. Identity & RBAC

#### 1A. Assessment runtime identity

The principal that runs `CosmosToSqlAssessment` against the source Cosmos account.

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Runtime identity is a **managed identity** (system- or user-assigned) — not a service principal with a client secret. | `az identity show -g <rg> -n <uami>` JSON output. | [Choosing system- vs user-assigned](production-hardening.md#choosing-between-system-assigned-and-user-assigned-managed-identity) |
| [ ] | Runtime identity holds **exactly** the four required read role grants from the hardening guide: `Cosmos DB Built-in Data Reader`, `Reader`, `Log Analytics Reader`, `Monitoring Reader`. No broader roles (Contributor, Owner, Cosmos DB Operator). | `az role assignment list --assignee <object-id> -o table` plus `az cosmosdb sql role assignment list --account-name <cosmos> -g <rg>` for the data-plane role. | [Required role grants](production-hardening.md#what-identity-the-tool-needs-at-runtime) |
| [ ] | Runtime identity is **not** assigned `Cosmos DB Built-in Data Contributor` or any custom Cosmos SQL role with item / container write or delete `DataActions`. (Note: Cosmos data-plane "built-in" roles are distinct from Azure RBAC built-ins.) | Same `az cosmosdb sql role assignment list` output — no `00000000-0000-0000-0000-000000000002` (Data Contributor) and no custom role with `items/create`, `items/replace`, `items/upsert`, `items/delete`. | [Cosmos DB data-plane RBAC](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control) |
| [ ] | Runtime identity does **not** hold `Microsoft.DocumentDB/databaseAccounts/listKeys/action` or `regenerateKey/action` anywhere in its effective role assignments. | `az role assignment list --assignee <object-id> --include-inherited` filtered for the two action names. | [Cosmos account key residual exposure](secret-rotation-and-audit.md#cosmos-account-key-residual-exposure) |
| [ ] | The source Cosmos account has `disableLocalAuth=true`. | `az cosmosdb show -g <rg> -n <cosmos> --query disableLocalAuth`. | [`security/rbac/README.md`](security/rbac/README.md) |
| [ ] | `dotnet run -- --test-connection ...` succeeds end-to-end as the production identity (positive evidence). | Truncated CLI output showing the green-check final line. | [Smoke test from the host](production-hardening.md#smoke-test-from-the-host) |
| [ ] | Negative evidence: (a) connection-string / account-key auth from any non-Entra principal **fails** after `disableLocalAuth=true`; (b) the runtime identity is **denied** when attempting `listKeys`, `regenerateKey`, or a Cosmos item write. | Three short CLI traces showing the expected `403` / `Unauthorized` / `Forbidden` responses. | [Cosmos DB data-plane RBAC](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control) |

#### 1B. SQL deploy identity

The principal that runs `sqlpackage publish` against the target Azure SQL database. This is a **separate identity** from the assessment runtime — do not consolidate.

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Deploy account's only group membership relevant to this database is the Entra group mapped to `db_schema_deployer` (or `db_owner` for first-time / bootstrap deploy only). Group object ID recorded in the change ticket. | `az ad group member list -g <group>` + the change-ticket entry. | [`security/rbac/README.md` SQL section](security/rbac/README.md) |
| [ ] | Deploy account does **not** hold any of the four assessment-runtime roles from 1A. (Separation of duties between read-and-assess vs deploy.) | Cross-reference: deploy account does not appear in 1A's role-assignment outputs. | [`security/rbac/sql-schema-deployer.sql`](security/rbac/sql-schema-deployer.sql) |
| [ ] | If the bootstrap `db_owner` elevation was used, the deploy account has been demoted back to `db_schema_deployer` and the elevation window is documented (start, end, who approved). | T-SQL `EXEC sp_helprolemember 'db_owner'` output + change-ticket entry. | [`security/rbac/sql-schema-deployer.sql`](security/rbac/sql-schema-deployer.sql) |

### 2. Network isolation

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Cosmos account `publicNetworkAccess=Disabled`; private endpoint is provisioned in a VNet the assessment host can reach. | `az cosmosdb show ... --query "{public:publicNetworkAccess,pe:privateEndpointConnections}"`. | [Network isolation](production-hardening.md#network-isolation) |
| [ ] | From the assessment host, `nslookup <cosmos>.documents.azure.com` (or `Resolve-DnsName`) returns a private IP, not a public one. | nslookup output. | [DNS forwarding](production-hardening.md#network-isolation) |
| [ ] | AMPLS posture is documented in the change ticket and matches the intended security mode (initial = ingestion PrivateOnly + query Open; locked-down = both PrivateOnly + workspace `publicNetworkAccessForQuery=Disabled`). | AMPLS access-modes screenshot or `az monitor private-link-scope show` JSON. | [Network isolation](production-hardening.md#network-isolation) |
| [ ] | Key Vault holding any non-Entra secrets has `publicNetworkAccess=Disabled` (or firewall restricted to trusted services + explicit subnets only). | `az keyvault show --query properties.networkAcls`. | [Secrets Management network considerations](secrets-management.md) |
| [ ] | Host VNet integration is in place for the assessment compute (AKS pod subnet, App Service `outboundVnetRouting.allTraffic=true`, Container Apps internal env, VM/VMSS NIC). | Host's outbound-routing config (CLI or portal screenshot). | [Network isolation](production-hardening.md#network-isolation) |
| [ ] | If Azure Monitor Private Link is in use, the host can reach `*.privatelink.monitor.azure.com` endpoints (else the `LogsQueryClient` calls will fail at the network layer with no descriptive error). | `Test-NetConnection` or `nc -vz` from the host to one of the five AMPLS DNS zones. | [Network isolation](production-hardening.md#network-isolation) |

### 3. Secrets

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Zero non-Entra secrets exist in the **assessment runtime** path — no `AZURE_CLIENT_SECRET`, no connection strings with keys, no SAS tokens. | `env | grep -iE 'AZURE_|SECRET|PASSWORD'` from the running host (truncated). | [Production Hardening DefaultAzureCredential footguns](production-hardening.md#defaultazurecredential-behaviour-on-each-host) |
| [ ] | Generated SQL deploy artifacts (`publish.xml`, `Deploy-SqlProject.ps1`) are **not** committed to source control. | `git log -p -- "**/publish.xml" "**/Deploy-SqlProject.ps1"` returns no entries with populated `<SqlPassword>` or non-placeholder `-Password`. | [Secrets Management Pattern A](secrets-management.md) |
| [ ] | Pipeline service connections use OIDC federated credentials wherever supported (Azure DevOps workload identity federation; GitHub Actions `azure/login@v2` with `client-id` / `tenant-id` and **no** `client-secret`). | Service-connection JSON or workflow YAML showing OIDC. | [Secrets Management Pattern A](secrets-management.md) |
| [ ] | If any service-principal client secret still exists, it is stored in Key Vault (not in pipeline variables, not in `.env` files) and has a Key Vault rotation policy or owner-tracked expiry. | Key Vault secret URI + rotation-policy JSON. | [Service-principal client secret rotation](secret-rotation-and-audit.md#service-principal-client-secret) |

### 4. Audit & monitoring

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Cosmos diagnostic setting is enabled with `DataPlaneRequests` + `ControlPlaneRequests` to the central Log Analytics workspace, with `LogAnalyticsDestinationType=Dedicated` (`--export-to-resource-specific true`). | `az monitor diagnostic-settings list --resource <cosmos-id>` showing both categories. | [Audit logging recipe](secret-rotation-and-audit.md#audit-logging-recipe) |
| [ ] | Key Vault diagnostic setting is enabled with `AuditEvent` + `AzurePolicyEvaluationDetails` to the same workspace. | `az monitor diagnostic-settings list --resource <kv-id>`. | [Audit logging recipe](secret-rotation-and-audit.md#audit-logging-recipe) |
| [ ] | Azure SQL database auditing is enabled and writing `SQLSecurityAuditEvents` to the same workspace (or to dedicated Storage). | `az sql db audit-policy show` showing `state=Enabled`. | [Audit logging recipe](secret-rotation-and-audit.md#audit-logging-recipe) |
| [ ] | Microsoft Entra log streaming is enabled for `SignInLogs`, `AuditLogs`, `ServicePrincipalSignInLogs`, `ManagedIdentitySignInLogs` to the workspace. Requires Entra ID P1+. | `az monitor diagnostic-settings subscription list` filtered for the Entra tenant resource. | [Audit logging recipe](secret-rotation-and-audit.md#audit-logging-recipe) |
| [ ] | Log Analytics workspace retention meets the documented org compliance baseline. If immutable / WORM retention is required, logs are **also** exported to an immutable Azure Blob Storage target with a time-based immutability policy. | Workspace retention setting + Storage container's immutability policy JSON. | [Log Analytics retention](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-retention-configure), [Storage immutability](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview) |
| [ ] | **Microsoft Defender for Key Vault** is **On** at the subscription level. | `az security pricing show -n KeyVaults --query pricingTier`. | [Defender for Key Vault](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-key-vault-introduction) |
| [ ] | **Microsoft Defender plan covering Azure Cosmos DB** is **On** at the subscription level (covers the source Cosmos accounts). | `az security pricing show -n CosmosDbs --query pricingTier`. | [Defender for Databases](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-databases-introduction) |
| [ ] | **Microsoft Defender for Azure SQL Databases** is **On** at the subscription level (covers the target SQL servers / managed instances). | `az security pricing show -n SqlServers --query pricingTier`. | [Defender for SQL](https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-sql-introduction) |
| [ ] | All five KQL detection rules from `secret-rotation-and-audit.md` are deployed as Azure Monitor scheduled alert rules with an action group that notifies the on-call rotation. | Alert-rule resource IDs + action-group resource ID. | [KQL detection library](secret-rotation-and-audit.md#kql-detection-library) |

### 5. Change control

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Production deployment uses an **approved release tag, signed artifact, or reviewed commit SHA** — not an unreviewed moving branch like `main`. | The tag/SHA used by the deploy pipeline, recorded in the change ticket. | — |
| [ ] | The DACPAC's pre-deploy diff (sqlpackage `/Action:DeployReport` or `/p:GenerateSmartDefaults=False`) was reviewed and approved before deploy. Reviewer name + approval timestamp are in the deploy log. | DeployReport XML attached + approval audit trail. | [`security/rbac/README.md` deploy walkthrough](security/rbac/README.md) |
| [ ] | Rollback strategy is documented: prior DACPAC SHA, restore-from-backup procedure, RTO/RPO commitment. | One-page runbook attached to the change ticket. | — |

### 6. Secret rotation readiness

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Quarterly rotation runbook is scheduled (calendar event, automated work-item, or equivalent) with a named owner. | Calendar invite / work-item URL. | [Quarterly rotation runbook](secret-rotation-and-audit.md#putting-it-together-a-quarterly-rotation-runbook) |
| [ ] | If a Key Vault rotation Function exists, it has been **test-fired in the last 30 days** and verified to update both Key Vault **and** the downstream consumer (Container Apps revision rolled, App Service config refresh confirmed, etc.). | Function-run log + downstream-consumer config showing the post-rotation secret version. | [Rotation fallback procedures](secret-rotation-and-audit.md#rotation-fallback-procedures) |
| [ ] | All known service-principal client secrets have a stored expiry date and an owner email. | Inventory spreadsheet or Key Vault secret tags. | [Service-principal client secret](secret-rotation-and-audit.md#service-principal-client-secret) |
| [ ] | The workload-identity federated-credential failure alert (KQL detection #5) is **either** (a) tested in non-prod, **or** (b) deployed enabled with action group + synthetic test scheduled within 14 days of first production run, with owner + date recorded. | Test-trigger screenshot OR the synthetic-test calendar entry. | [Federated-credential failures KQL](secret-rotation-and-audit.md#5-workload-identity-federated-credential-failures) |

### 7. Data protection

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | Source containers' data classification (Public / Internal / Confidential / Restricted) is documented in the change ticket; assessment scope and sample strategy have been approved against that classification. | Classification + approval comment in the ticket. | — |
| [ ] | Generated reports (Excel / Word artifacts under `reports/`) are written to a location with appropriate access controls (RBAC-protected SharePoint, locked-down storage account, or equivalent). Reports are **not** committed to source control. | Storage-target ACL + a `git log --diff-filter=A -- "**/reports/*"` check. | [`Services/ReportGenerationService.cs`](../Services/ReportGenerationService.cs) |

### 8. Disaster recovery & continuity

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | The user-assigned managed identity has at least one human owner holding `Managed Identity Contributor` on the UAMI resource (use `Managed Identity Operator` only for assignment-only delegation). Avoids dependency on Microsoft support if the identity must be re-keyed. | `az role assignment list --scope <uami-id>` showing the named human + role. | [Built-in identity roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/identity) |
| [ ] | At least one privileged break-glass identity (hardware-MFA-only sign-in) can be elevated to `Cosmos DB Built-in Data Contributor` on the production Cosmos account, gated behind **either** (a) PIM eligible assignment with approval + time-bound activation, **or** (b) a documented manual time-boxed role-assignment runbook (request → approval ticket → grant with expiry → audit-query verification of removal). | PIM eligibility config OR the manual runbook. | [Privileged Identity Management](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-getting-started) |
| [ ] | The Log Analytics workspace lives in a **different resource group** from the workloads it monitors, so a delete of the workload RG does not wipe the audit trail. | `az monitor log-analytics workspace show --query resourceGroup` vs the Cosmos/SQL/KV RG. | — |

### 9. Threat model & freshness

| Done | Check | Evidence | Reference |
|:--:|---|---|---|
| [ ] | A data-flow / trust-boundary review has been completed for this deployment pattern and is re-reviewed on material architecture or tool-version changes. (The one-time tool-adoption review covers the *tool*; this row covers the *deployment*.) | Diagram + last-reviewed date + reviewer. | [Production Hardening](production-hardening.md) |
| [ ] | The tool version in production is an approved release; security advisories and release notes since that version have been reviewed and have no unaddressed action items. | Release tag + advisory-review note. | [Project releases](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/releases) |

---

## Failed any row? Remediation pointers

| Category that failed | Most likely root cause | Where to look |
|---|---|---|
| 1A — runtime identity | Inherited Contributor / Owner from RG or subscription scope | [Production Hardening](production-hardening.md), [Custom RBAC role definitions](security/rbac/README.md) |
| 1B — deploy identity | Bootstrap `db_owner` was never demoted; or the deploy SP also has assessment-read roles | [`security/rbac/sql-schema-deployer.sql`](security/rbac/sql-schema-deployer.sql) |
| 2 — network isolation | Custom DNS server shadowing the private DNS zone; AMPLS scope not linked to the host VNet | [Network isolation](production-hardening.md#network-isolation) |
| 3 — secrets | Old pipeline still using `$(SqlPassword)`; service connection still using SP secret | [Secrets Management](secrets-management.md) |
| 4 — audit & monitoring | Diagnostic setting created without `--export-to-resource-specific true`, so dedicated tables are empty | [Audit logging recipe](secret-rotation-and-audit.md#audit-logging-recipe) |
| 5 — change control | Deploy pipeline triggers off `main`; no deploy-report review step in the pipeline | — |
| 6 — secret rotation | No owner on the rotation Function; SP expiry tracked only in someone's head | [Rotation fallback procedures](secret-rotation-and-audit.md#rotation-fallback-procedures) |
| 7 — data protection | Reports landing on a developer laptop or unsecured share | [`Services/ReportGenerationService.cs`](../Services/ReportGenerationService.cs) |
| 8 — DR & continuity | Audit workspace in the same RG as Cosmos; no PIM and no break-glass runbook | [Privileged Identity Management](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-getting-started) |
| 9 — threat model | Last review predates the current deployment topology | [Production Hardening](production-hardening.md) |

---

## What this checklist deliberately does **not** cover

- Operational SRE concerns (HA topology, region failover, capacity planning) — separate doc if needed.
- Code-review checklist — this is a *deployment* gate, not a *PR* gate.
- Compliance-framework control mapping (SOC 2, FedRAMP, HIPAA, PCI). The Evidence column is structured to make that mapping straightforward later, but the mapping itself is left to the org's security team.
- Customer-managed key (CMK) rotation for Cosmos DB and Azure SQL TDE — addressed at the platform level, not specific to this tool.
- A Bicep / Terraform / Azure Policy module that *enforces* the checklist. Gate first, automate later.

---

## References

- [Production Hardening Guide](production-hardening.md)
- [Secrets Management](secrets-management.md)
- [Custom RBAC role definitions](security/rbac/README.md)
- [Secret Rotation and Audit Logging](secret-rotation-and-audit.md)
- [Azure Permissions](azure-permissions.md)
- [Microsoft Defender for Cloud documentation index](https://learn.microsoft.com/en-us/azure/defender-for-cloud/)
- [Microsoft Entra Privileged Identity Management](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-getting-started)
