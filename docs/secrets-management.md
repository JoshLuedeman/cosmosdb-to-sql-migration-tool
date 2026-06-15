# Secrets Management

This guide covers how to handle secrets — both the ones this tool needs at runtime and the ones that appear in the deployment artifacts it generates — using **Azure Key Vault** and managed identities.

It is a companion to the [Production Hardening Guide](production-hardening.md) and lands under parent issue [#128](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/128) / sub-issue [#200](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/200).

---

## Inventory of secrets used by this tool

| Where | Secret type | Required? | Recommended handling |
|---|---|---|---|
| Runtime (`CosmosToSqlAssessment` console app) | **None.** The app uses `DefaultAzureCredential` and acquires tokens; it never reads a key, password, or connection string with embedded credentials. | n/a | Continue using managed identity (see [Production Hardening Guide](production-hardening.md)) |
| `appsettings.json` | No secrets today — only operational tuning (sample sizes, region hints, output dirs) | n/a | Keep secret-free; do not add API keys here |
| Generated `*.publish.xml` (emitted by `Services/SqlProjectIntegrationService.cs:513`) | SQL `User ID` + `Password` placeholder in `TargetConnectionString` | Only if you use SQL authentication for the target database | Replace with Microsoft Entra auth where possible; otherwise inject the password from Key Vault at deploy time and **do not commit the populated file** |
| Generated `azure-pipelines.yml` (emitted by `Services/SqlProjectIntegrationService.cs:574`) | `$(SqlPassword)` pipeline variable reference | Only if SQL auth is used | Bind the variable to a Key Vault-backed variable group |
| Generated `Deploy-SqlProject.ps1` (emitted by `Services/SqlProjectIntegrationService.cs:596,613`) | `[string]$Password` parameter | Only if SQL auth is used | Populate from Key Vault (governed) or `Read-Host -AsSecureString` (one-off) |

> **Prefer Microsoft Entra authentication for Azure SQL wherever the target platform supports it.** Azure SQL Database, Azure SQL Managed Instance, and SQL Server 2022+ on Azure VM all support Entra auth — for those targets, you can drop SQL passwords entirely and use the managed identity of the deployment principal (CI runner, App Service, container) instead. The patterns below are for the cases where the target platform, tooling, or legacy database configuration cannot use Entra.

---

## Key Vault setup

Use **Azure RBAC** for the vault, not the legacy access-policies model. RBAC is the only recommended option for new vaults and is consistent with the rest of this guide.

```bash
RG=rg-cosmos-assessment
KV=kv-cosmos-assessment-$RANDOM
LOCATION=eastus
SUB=$(az account show --query id -o tsv)

# Create vault with RBAC authorization
az keyvault create \
  --name $KV --resource-group $RG --location $LOCATION \
  --enable-rbac-authorization true \
  --enable-purge-protection true \
  --retention-days 90 \
  --public-network-access Disabled  # set to Enabled for proof-of-concept; see "Network considerations" below

# Grant the consuming managed identity read access to secrets
PRINCIPAL_ID=$(az identity show --resource-group $RG --name uami-assessment --query principalId -o tsv)
az role assignment create \
  --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"
```

`Key Vault Secrets User` is the least-privilege role for a workload that only needs to **read** secret values. Use `Key Vault Secrets Officer` for administrators who rotate secrets; never grant it to the running workload.

---

## Pattern A — SQL deployment password at deploy time

> **Stop here and use Microsoft Entra auth instead** if your target Azure SQL platform supports it (it almost certainly does — see the [Microsoft Entra auth for Azure SQL docs](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview)). The patterns below are a fallback for unavoidable SQL credentials.

### A1 — Azure DevOps Pipelines

The pipeline YAML emitted by the tool references `$(SqlPassword)`. Bind that variable to a Key Vault-backed variable group so the YAML never contains the secret:

```yaml
# In an existing pipeline:
variables:
  - group: cosmos-assessment-sql-creds  # variable group bound to Key Vault

# Or, inline with the v2 task:
steps:
  - task: AzureKeyVault@2
    inputs:
      azureSubscription: 'sc-cosmos-assessment'
      KeyVaultName: 'kv-cosmos-assessment-1234'
      SecretsFilter: 'SqlAdminPassword'
      RunAsPreJob: true
  - task: SqlAzureDacpacDeployment@1
    inputs:
      azureSubscription: 'sc-cosmos-assessment'
      AuthenticationType: 'server'
      ServerName: 'contoso-sql.database.windows.net'
      DatabaseName: 'TargetDb'
      SqlUsername: 'sqladmin'
      SqlPassword: '$(SqlAdminPassword)'  # populated from Key Vault, never echoed
      DacpacFile: '$(Build.ArtifactStagingDirectory)/**/*.dacpac'
```

The service connection (`azureSubscription`) should be a **workload-identity federation** service connection, not a service-principal secret connection. The connection's principal needs `Key Vault Secrets User` on the vault.

### A2 — GitHub Actions

Use OpenID Connect federation (`azure/login@v2`) to log in as a federated identity, then pull the secret with the Azure CLI and mask it. Avoid the deprecated `Azure/get-keyvault-secrets@v1` action.

```yaml
permissions:
  id-token: write
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}      # UAMI client ID (no secret stored)
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Get SQL admin password from Key Vault and deploy
        run: |
          SQL_PWD=$(az keyvault secret show \
            --vault-name kv-cosmos-assessment-1234 \
            --name SqlAdminPassword \
            --query value -o tsv)
          echo "::add-mask::$SQL_PWD"
          # Use $SQL_PWD inside this same step — do NOT write it to $GITHUB_ENV
          # unless a later step truly needs it; that widens the blast radius.
          sqlpackage /a:Publish /sf:./bin/Release/TargetDb.dacpac \
            /tcs:"Server=tcp:contoso-sql.database.windows.net,1433;Initial Catalog=TargetDb;User ID=sqladmin;Password=$SQL_PWD;Encrypt=True;TrustServerCertificate=False"
```

Scope the federated identity's `Key Vault Secrets User` role to the specific vault (or even a single secret if your governance model permits) — not the resource group or subscription.

### A3 — Local PowerShell with the tool-generated `Deploy-SqlProject.ps1`

The generated `Deploy-SqlProject.ps1` accepts a `[string]$Password` parameter. Two acceptable patterns, depending on context:

**Governed deployments (preferred):** pull from Key Vault so the secret is centrally managed, rotated, and audited:

```powershell
Connect-AzAccount -Identity   # or -DeviceCode for interactive
$securePwd = (Get-AzKeyVaultSecret -VaultName 'kv-cosmos-assessment-1234' -Name 'SqlAdminPassword').SecretValue
$plainPwd  = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd))

./Deploy-SqlProject.ps1 -ServerName 'contoso-sql.database.windows.net' `
                        -DatabaseName 'TargetDb' `
                        -Username 'sqladmin' `
                        -Password $plainPwd
```

**One-off local deployment (acceptable):** prompt interactively so the password never appears on the command line or in shell history:

```powershell
$securePwd = Read-Host -Prompt 'SQL admin password' -AsSecureString
$plainPwd  = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd))

./Deploy-SqlProject.ps1 -ServerName 'contoso-sql.database.windows.net' `
                        -DatabaseName 'TargetDb' `
                        -Username 'sqladmin' `
                        -Password $plainPwd
```

Key Vault is better for centralized management, audit, and rotation; an interactive prompt is better for ephemeral local runs where setting up vault access for the user is overkill. The two patterns are not ordered by "security" at the local-machine boundary — once `$plainPwd` exists in process memory, both options have the same exposure surface.

> ⚠️ **Never commit a populated `*.publish.xml`.** The template the tool generates contains the literal string `your-password`, but a populated profile will contain real credentials. Treat publish profiles as deployment artifacts: keep them out of source control (`.gitignore` them) and pass connection-string overrides to `sqlpackage /tcs:...` at deploy time instead.

---

## Pattern B — Key Vault references for hosted runtimes

If you run the **assessment tool itself** (not the SQL deploy step) inside App Service, Functions, or Container Apps, and a future contribution introduces a non-Entra secret (e.g. a notification webhook URL), reference Key Vault from the host so the secret never appears in `appsettings.json`.

### Azure App Service / Functions

```bash
az webapp config appsettings set \
  --resource-group $RG --name $WEBAPP \
  --settings "NotificationWebhookUrl=@Microsoft.KeyVault(SecretUri=https://kv-cosmos-assessment-1234.vault.azure.net/secrets/NotificationWebhookUrl/)"
```

App Service resolves the reference at app-start using the site's managed identity. The MI needs `Key Vault Secrets User` on the vault. See [Key Vault references for App Service](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references).

### Azure Container Apps

```bash
az containerapp secret set \
  --resource-group $RG --name $CAAPP \
  --secrets "notification-webhook=keyvaultref:https://kv-cosmos-assessment-1234.vault.azure.net/secrets/NotificationWebhookUrl,identityref:$UAMI_ID"

# Reference the secret as an env var
az containerapp update \
  --resource-group $RG --name $CAAPP \
  --set-env-vars "NotificationWebhookUrl=secretref:notification-webhook"
```

See [Manage secrets in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets).

> ⚠️ **Container Apps secret-reference lifecycle.** Updating the secret value in Key Vault — or in the Container Apps secret store — does **not** automatically refresh running revisions. You must either deploy a new revision (recommended for predictable rollout) or explicitly restart affected revisions. Plan rotation accordingly (see [Secret Rotation and Audit Logging](secret-rotation-and-audit.md)).

---

## Pattern C — Loading Key Vault secrets into IConfiguration (contributor guidance)

This tool currently has no non-Entra secrets in configuration. If a future contribution adds one, wire it through the `Microsoft.Extensions.Configuration` Key Vault provider rather than `appsettings.json`. Despite the package name, `Azure.Extensions.AspNetCore.Configuration.Secrets` is the correct provider for console and Generic-Host apps (it has no ASP.NET runtime dependency):

```csharp
// In Program.cs, ConfigurationBuilder section:
using Azure.Identity;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddAzureKeyVault(
        new Uri("https://kv-cosmos-assessment-1234.vault.azure.net/"),
        new DefaultAzureCredential())
    .Build();
```

NuGet packages:

```xml
<PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="1.3.*" />
<PackageReference Include="Azure.Identity" Version="1.13.*" />
```

For one-off secret reads outside the configuration system (e.g. an admin tooling script), use `Azure.Security.KeyVault.Secrets` directly:

```csharp
var client = new SecretClient(new Uri("https://kv-cosmos-assessment-1234.vault.azure.net/"), new DefaultAzureCredential());
KeyVaultSecret secret = await client.GetSecretAsync("NotificationWebhookUrl");
```

See [Azure Key Vault Secrets configuration provider](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/extensions.aspnetcore.configuration.secrets-readme).

---

## Network considerations

If the Key Vault is created with `--public-network-access Disabled` (recommended for production), the consuming workload must reach the vault through a **private endpoint**, and the host's DNS must resolve the vault FQDN to the private IP. The exact network design (private DNS zone, VNet integration, hub-and-spoke) is covered by sub-issue [#201](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/201) when it lands.

The minimum signal that network isolation is wrong: the consuming app gets `Failed to acquire token` or `403 ForbiddenByFirewall` from the vault even though the role assignment is correct.

---

## Rotation

Key Vault preserves secret **versions**. Rotating a secret creates a new version; old versions remain readable until explicitly disabled or purged. The implications for running workloads (App Service Key Vault references, Container Apps secrets, in-memory caches in this tool) plus full rotation procedures (Key Vault native rotation policy + Event Grid + Function handler) and an audit-logging recipe with KQL detections are covered in [Secret Rotation and Audit Logging](secret-rotation-and-audit.md).

---

## References

- [Use Key Vault references as app settings in Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references)
- [Manage secrets in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets)
- [Azure Key Vault Secrets configuration provider for `Microsoft.Extensions.Configuration`](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/extensions.aspnetcore.configuration.secrets-readme)
- [Use OpenID Connect to authenticate GitHub Actions to Azure](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [Azure Key Vault task v2 for Azure Pipelines](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-key-vault-v2)
- [Microsoft Entra authentication for Azure SQL](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview)
- Related: [Production Hardening Guide](production-hardening.md), [Azure Permissions](azure-permissions.md)
