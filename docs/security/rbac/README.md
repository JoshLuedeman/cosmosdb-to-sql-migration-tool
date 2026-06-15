# Custom RBAC role definitions

Least-privilege role definitions for running the **CosmosToSqlAssessment** tool in production and for deploying the SQL Database Project it generates. These customs are strict subsets of the Azure built-in roles cited in [`../azure-permissions.md`](../../azure-permissions.md) and [`../production-hardening.md`](../../production-hardening.md).

Use the built-ins (`Cosmos DB Built-in Data Reader`, `Reader`, `Log Analytics Reader`, `Monitoring Reader`) when you are doing a proof-of-concept and want to be up in five minutes. Use these custom roles when audit, governance, or Azure Policy requires bounded permissions or when a security review needs to enumerate exactly what the tool can touch.

---

## Files in this folder

| File | Schema | Plane | Deploy with |
|---|---|---|---|
| [`cosmos-assessment-data-reader.json`](cosmos-assessment-data-reader.json) | Cosmos DB SQL RBAC | Cosmos DB **data plane** | `az cosmosdb sql role definition create` |
| [`cosmos-assessment-arm-reader.json`](cosmos-assessment-arm-reader.json) | Azure RBAC custom role | Azure **control plane** (ARM) | `az role definition create` |
| [`cosmos-assessment-monitor-reader.json`](cosmos-assessment-monitor-reader.json) | Azure RBAC custom role | Azure **control plane** (ARM) | `az role definition create` |
| [`sql-schema-deployer.sql`](sql-schema-deployer.sql) | T-SQL `CREATE ROLE` script | Azure SQL **database scope** | `sqlcmd -d <db> -i sql-schema-deployer.sql` or run in SSMS / Azure Data Studio |

### Two schemas, two deploy commands

Cosmos DB SQL data-plane role definitions are **native to the Cosmos account** — they are not Azure RBAC. They use a different JSON shape and a different CLI command:

| Aspect | ARM custom role (`arm-reader`, `monitor-reader`) | Cosmos data-plane role (`data-reader`) |
|---|---|---|
| Top-level fields | `Name`, `IsCustom`, `Description`, `Actions`, `NotActions`, `DataActions`, `NotDataActions`, `AssignableScopes` | `RoleName`, `Type: "CustomRole"`, `Description`, `Permissions[]` (with `DataActions` / `NotDataActions`), `AssignableScopes` |
| Deploy CLI | `az role definition create --role-definition @<file>.json` | `az cosmosdb sql role definition create --account-name <COSMOS> --resource-group <RG> --body @<file>.json` |
| Assign CLI | `az role assignment create --role <name> --assignee <principal> --scope <scope>` | `az cosmosdb sql role assignment create --account-name <COSMOS> --resource-group <RG> --role-definition-id <id> --principal-id <principal> --scope "/"` |
| Lives in | Microsoft Entra (visible in subscription RBAC) | Cosmos DB account (visible via `az cosmosdb sql role definition list`) |

Mixing the two formats — for example, trying `az role definition create --role-definition @cosmos-assessment-data-reader.json` — will fail with a schema error.

---

## Required edits before deploy

Every JSON file ships with placeholder values that must be replaced. JSON does not support comments, so each placeholder uses an obviously-invalid GUID (`00000000-0000-0000-0000-000000000000`) or `REPLACE_*` string so failures are loud rather than silent:

| Placeholder | Replace with |
|---|---|
| `/subscriptions/00000000-0000-0000-0000-000000000000` | Your subscription resource ID, e.g. `/subscriptions/aaaa0a0a-bb1b-cc2c-dd3d-eeeeee4e4e4e` |
| `REPLACE_RG` (in `cosmos-assessment-data-reader.json` only) | The resource group containing the Cosmos account |
| `REPLACE_COSMOS_ACCOUNT` (in `cosmos-assessment-data-reader.json` only) | The Cosmos account name |
| `REPLACE_WITH_ENTRA_PRINCIPAL_NAME` (in `sql-schema-deployer.sql`) | The Microsoft Entra user, group, or managed-identity display name |

You can edit in place or use `jq` / `sed` in a deploy script. Either way, run the JSON through `Get-Content x.json | ConvertFrom-Json` (or `jq .`) after editing to confirm it still parses before invoking `az`.

---

## What each role grants and why

### `cosmos-assessment-data-reader.json`

Cosmos DB **data-plane** role for the running assessment workload. Mirrors the four `DataActions` the NoSQL SDK uses for read + query workloads:

| Action | Why this tool needs it |
|---|---|
| `Microsoft.DocumentDB/databaseAccounts/readMetadata` | NoSQL SDK metadata reads when `CosmosClient` is constructed with a `TokenCredential` |
| `.../sqlDatabases/containers/items/read` | `ReadItemAsync` / response stream materialization during the data-quality sampling phase |
| `.../sqlDatabases/containers/executeQuery` | `container.GetItemQueryIterator<JsonDocument>(query)` in `DataQualityAnalysisService` |
| `.../sqlDatabases/containers/readChangeFeed` | Kept even though `ReadChangeFeed` is not directly called — the SDK query pipeline can require it transiently |

`readChangeFeed` is the one entry that looks unused but is intentionally retained — dropping it has historically caused intermittent query failures.

### `cosmos-assessment-arm-reader.json`

ARM **control-plane** read access. Strict subset of the built-in `Reader` role. The tool itself does not call ARM at runtime, but the smoke-test commands documented in [`../production-hardening.md`](../../production-hardening.md) (`az cosmosdb show`, `az cosmosdb sql role assignment list`) and the workspace auto-discovery flag (`--auto-discover`) need it.

Deliberately **omits** `Microsoft.DocumentDB/databaseAccounts/listKeys/action` and `listConnectionStrings/action` — combined with `disableLocalAuth=true` on the Cosmos account (recommended in [`../production-hardening.md`](../../production-hardening.md)), this guarantees no caller of this role can ever pull a primary key.

Assign at Cosmos account scope (tightest) or RG scope (typical):

```bash
az role assignment create \
  --assignee-object-id <principal> --assignee-principal-type ServicePrincipal \
  --role "Cosmos DB Assessment ARM Reader" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.DocumentDB/databaseAccounts/<cosmos>"
```

### `cosmos-assessment-monitor-reader.json`

ARM custom role for Azure Monitor metrics + Log Analytics query. Strict subset of `Monitoring Reader` + `Log Analytics Reader`. Deliberately **omits** `*/read` (a frequent over-grant in built-ins), `workspaces/sharedKeys/action` (would leak ingestion keys), alert-rule reads, workbook reads, and action-group reads — none of which the tool touches.

Assign at workspace scope:

```bash
az role assignment create \
  --assignee-object-id <principal> --assignee-principal-type ServicePrincipal \
  --role "Cosmos DB Assessment Monitor Reader" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.OperationalInsights/workspaces/<workspace>"
```

### `sql-schema-deployer.sql`

Database-scoped T-SQL role for `sqlpackage publish` against the SQL Database Project the tool generates. Azure SQL DDL permissions live **inside the database**, not in Azure RBAC, so this is a `.sql` script rather than a JSON file.

**Escalation matrix:**

| Scenario | Recommended permissions |
|---|---|
| First/bootstrap deploy against an empty database; DACPAC creates users, logins, roles, or extended properties | Elevate to `db_owner` temporarily, run sqlpackage, then drop the principal back to `db_schema_deployer` |
| Steady-state deploy — adding tables/views/procedures, altering columns, dropping unused objects | `db_schema_deployer` (the role this script creates) |
| Schema includes `CREATE/ALTER LOGIN` or server-level objects | Server-level `loginmanager` on `master`; not covered here |

When `sqlpackage` hits `permission denied` errors during deploy, raise the deploying principal to `CONTROL ON DATABASE` first; only fall back to `db_owner` if `CONTROL` is still insufficient. Avoid making `db_owner` the steady-state grant.

---

## End-to-end deploy walkthrough (Cosmos + Monitor roles, single tenant)

```bash
SUB="<your-subscription-guid>"
RG="rg-cosmos-assessment"
COSMOS="contoso-cosmos"
WORKSPACE="law-cosmos"
PRINCIPAL_ID="<object-id-of-uami-or-app>"   # NOT the client-id

# Replace placeholders in the JSON files (sed for Linux/macOS, equivalent in PowerShell)
sed -i "s|00000000-0000-0000-0000-000000000000|$SUB|g; s|REPLACE_RG|$RG|g; s|REPLACE_COSMOS_ACCOUNT|$COSMOS|g" \
  docs/security/rbac/*.json

# ---- Cosmos data-plane role ----
COSMOS_ROLE_DEF_ID=$(az cosmosdb sql role definition create \
  --account-name $COSMOS --resource-group $RG \
  --body @docs/security/rbac/cosmos-assessment-data-reader.json \
  --query id -o tsv)

az cosmosdb sql role assignment create \
  --account-name $COSMOS --resource-group $RG \
  --role-definition-id $COSMOS_ROLE_DEF_ID \
  --principal-id $PRINCIPAL_ID --scope "/"

# ---- ARM Reader custom role ----
az role definition create --role-definition @docs/security/rbac/cosmos-assessment-arm-reader.json
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Cosmos DB Assessment ARM Reader" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS"

# ---- Monitor + Log Analytics custom role ----
az role definition create --role-definition @docs/security/rbac/cosmos-assessment-monitor-reader.json
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Cosmos DB Assessment Monitor Reader" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.OperationalInsights/workspaces/$WORKSPACE"
```

After these three assignments the principal has exactly the permissions the assessment tool needs — and nothing more. Validate with:

```bash
dotnet run -- --test-connection \
  --endpoint "https://$COSMOS.documents.azure.com:443/" \
  --workspace-id "<workspace-customer-id>" \
  --subscription-id "$SUB" --resource-group "$RG" --cosmos-account "$COSMOS"
```

If any layer fails, walk the diagnostic recipe in [`../production-hardening.md`](../../production-hardening.md) to see which role is missing.

---

## References

- [Connect to Azure Cosmos DB for NoSQL using role-based access control](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control)
- [Azure custom roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles)
- [Resource provider operations: Microsoft.DocumentDB / Microsoft.Insights / Microsoft.OperationalInsights](https://learn.microsoft.com/en-us/azure/role-based-access-control/resource-provider-operations)
- [sqlpackage publish minimum permissions](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-publish)
- Related project docs: [Production Hardening](../../production-hardening.md), [Secrets Management](../../secrets-management.md), [Azure Permissions](../../azure-permissions.md)
