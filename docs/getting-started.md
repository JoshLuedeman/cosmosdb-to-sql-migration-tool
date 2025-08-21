# Getting Started

## Prerequisites

### System Requirements

- **.NET 8.0 SDK** or later
- **Azure CLI** (for authentication) or access to Azure managed identity
- **Windows, macOS, or Linux** (cross-platform compatible)
- **Azure subscription** with access to:
  - Azure Cosmos DB account
  - Azure Monitor/Log Analytics workspace (optional, for enhanced metrics)

### Azure Permissions Required

Your Azure account needs specific permissions to access Cosmos DB and Azure Monitor resources. For a complete list of required permissions and setup instructions, see the [Azure Permissions Guide](azure-permissions.md).

**Quick Reference**:
- **Cosmos DB**: `Cosmos DB Account Reader` role
- **Azure Monitor**: `Log Analytics Reader` role (optional, for enhanced metrics)
- **Resource Groups**: `Reader` role (for auto-discovery features)

## Installation

### 1. Clone the Repository

```bash
git clone https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool.git
cd cosmosdb-to-sql-migration-tool
```

### 2. Restore Dependencies

```bash
dotnet restore
```

### 3. Build the Application

```bash
dotnet build --configuration Release
```

### 4. Verify Installation

```bash
dotnet run -- --help
```

You should see the application help information displaying available command-line options.

## Quick Start

### Single Database Analysis

```bash
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --database "YourDatabase" --output "C:\Reports"
```

### Multi-Database Analysis

```bash
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases --output "C:\Reports"
```

### Example Output

After running, you'll find reports in a timestamped folder:
```
C:\Reports\
└── CosmosDB-Analysis_2025-08-21__14-30-45\
    ├── YourDatabase-Analysis.xlsx
    └── Migration-Assessment.docx
```

## First-Time Setup

### 1. Azure Authentication

Choose one of the following authentication methods:

#### Option A: Azure CLI (Recommended for Development)

```bash
# Install Azure CLI if not already installed
# https://docs.microsoft.com/en-us/cli/azure/install-azure-cli

# Login to Azure
az login

# Set your subscription (optional)
az account set --subscription "your-subscription-id"
```

#### Option B: Managed Identity (Recommended for Production)

When running on Azure VMs, Container Instances, or App Services, the application will automatically use managed identity.

#### Option C: Service Principal

Set environment variables:

```bash
export AZURE_CLIENT_ID="your-client-id"
export AZURE_CLIENT_SECRET="your-client-secret"
export AZURE_TENANT_ID="your-tenant-id"
```

### 2. Configure the Application

Copy the sample configuration:

```bash
cp appsettings.sample.json appsettings.json
```

Edit `appsettings.json` with your specific Azure details:

```json
{
  "CosmosDb": {
    "AccountEndpoint": "https://your-cosmos-account.documents.azure.com:443/",
    "DatabaseName": "your-database-name",
    "AnalysisContainers": ["container1", "container2"],
    "PerformanceAnalysisMonths": 6
  },
  "AzureMonitor": {
    "WorkspaceId": "your-log-analytics-workspace-id",
    "EnableMetricsCollection": true
  }
}
```

### 3. Test Your Setup

Run a basic connectivity test:

```bash
dotnet run -- --test-connection
```

This will verify:
- Azure authentication is working
- Cosmos DB access is configured correctly
- Azure Monitor connectivity is established

## Next Steps

- **[Configuration Guide](configuration.md)** - Detailed configuration options
- **[Usage Guide](usage.md)** - Running your first assessment
- **[Troubleshooting](troubleshooting.md)** - If you encounter issues

## Quick Assessment

Once setup is complete, run a quick assessment:

```bash
dotnet run
```

The application will:
1. Connect to your Cosmos DB account
2. Analyze specified containers
3. Collect performance metrics
4. Generate migration assessment
5. Create Excel and Word reports in the `Reports/` directory

## Security Best Practices

- **Never commit** `appsettings.json` with real credentials to source control
- **Use Azure Key Vault** for production credentials
- **Enable managed identity** when running on Azure
- **Rotate credentials** regularly
- **Use least privilege** access principles for Azure permissions
