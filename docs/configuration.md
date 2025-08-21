# Configuration Guide

## Azure Resource Requirements

Before configuring the application, ensure you have the required Azure resources and permissions. See the [Azure Permissions Guide](azure-permissions.md) for detailed requirements.

**Required Resources**:
- Azure Cosmos DB account (SQL API)
- Azure Active Directory access
- Appropriate role assignments

**Optional for Enhanced Analysis**:
- Log Analytics workspace with Cosmos DB diagnostic logs
- Azure Monitor configuration

## Overview

The application now uses **command-line arguments as the primary configuration method**, with `appsettings.json` providing only default/optional settings. This approach is more secure and flexible for deployment.

## Primary Usage: Command-Line Arguments

### Basic Usage
```bash
# Analyze a specific database
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --database "YourDatabase" --output "C:\Reports"

# Analyze all databases in the account
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases --output "C:\Reports"
```

### Command-Line Options
```bash
--endpoint, -e     Cosmos DB endpoint URL (required)
--database, -d     Specific database name to analyze
--all-databases    Analyze all databases in the account
--output, -o       Output directory for reports
--auto-discover    Auto-discover Azure Monitor settings
--help            Show help information
```

## Configuration File (appsettings.json)

The configuration file now contains only **optional settings and defaults**. No sensitive information is stored here.

### Current Configuration Structure

```json
{
  "CosmosDb": {
    "MaxSampleDocuments": 1000,
    "SamplePercentage": 0.1,
    "AnalyzeAllContainers": true
  },
  "AzureMonitor": {
    "AutoDiscover": true,
    "TimeRangeHours": 168,
    "UseCosmosMetricsAsFallback": true
  },
  "SqlAssessment": {
    "RecommendedPlatforms": [
      "AzureSqlDatabase",
      "AzureSqlManagedInstance",
      "SqlOnAzureVm"
    ]
  },
  "DataFactory": {
    "EstimateParallelCopies": 4,
    "NetworkBandwidthMbps": 1000,
    "SourceRegion": "East US",
    "TargetRegion": "East US"
  },
  "Reports": {
    "OutputDirectory": "",
    "PromptForOutputDirectory": true,
    "GenerateExcel": true,
    "GenerateWord": true,
    "IncludeDetailedMapping": true,
    "DefaultDirectoryPattern": "CosmosDB-Analysis_{DateTime:yyyy-MM-dd__HH-mm-ss}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Azure": "Information",
      "CosmosToSqlAssessment.Services.CosmosDbAnalysisService": "Debug"
    }
  }
}
```

1. **Azure Monitor (Recommended)**: Comprehensive historical metrics, advanced analytics
2. **Cosmos DB SDK (Fallback)**: Basic real-time metrics, limited historical data

### Required Settings (Azure Monitor)

```json
"AzureMonitor": {
  "WorkspaceId": "12345678-1234-1234-1234-123456789012"
}
```

### Auto-Discovery

**New Feature**: The application can automatically discover Azure Monitor settings:

```json
"AzureMonitor": {
  "AutoDiscover": true,
  "UseCosmosMetricsAsFallback": true
}
```

**Command Line**: Use `--auto-discover` to automatically find monitoring settings.

### Optional Settings

```json
"AzureMonitor": {
  "WorkspaceId": "12345678-1234-1234-1234-123456789012",
  "AutoDiscover": false,
  "UseCosmosMetricsAsFallback": true,
  "TimeRangeHours": 168,
  
  // Enable metrics collection (requires Log Analytics)
  "EnableMetricsCollection": true,
  
  // Query timeout for Log Analytics queries
  "QueryTimeoutMinutes": 5,
  
  // Custom KQL queries for specific metrics
  "CustomQueries": {
    "ThroughputUtilization": "your-custom-kql-query",
    "LatencyMetrics": "your-custom-kql-query"
  }
}
```

## SQL Migration Configuration

```json
"SqlMigration": {
  // Target Azure region for cost estimation
  "TargetRegion": "East US",
  
  // Preferred SQL platform (SqlDatabase, SqlManagedInstance, SqlVM)
  "PreferredPlatform": "SqlDatabase",
  
  // Cost optimization strategy
  "CostOptimization": {
    "EnableReservedCapacity": true,
    "EnableAzureHybridBenefit": true,
    "PreferBurstableCompute": false
  },
  
  // Migration complexity factors
  "ComplexityFactors": {
    "ConsiderNestedDocuments": true,
    "ConsiderArrayFields": true,
    "ConsiderDynamicSchema": true
  }
}
```

## Data Factory Configuration

```json
"DataFactory": {
  // Region for Data Factory instance
  "Region": "East US",
  
  // Migration performance settings
  "Performance": {
    "DefaultDIUs": 4,
    "MaxDIUs": 32,
    "DefaultParallelCopies": 4,
    "BatchSize": 10000
  },
  
  // Cost calculation settings
  "Pricing": {
    "DIUHourlyRate": 0.25,
    "PipelineRunCost": 1.00,
    "RegionalMultiplier": 1.0
  }
}
```

## Reporting Configuration

### Output Directory Management

**New Feature**: Interactive output directory selection:

```json
"Reporting": {
  "OutputDirectory": "",
  "PromptForOutputDirectory": true,
  "DefaultDirectoryPattern": "CosmosAssessment_{DateTime:yyyyMMdd_HHmmss}",
  
  // Report file naming
  "FileNaming": {
    "IncludeTimestamp": true,
    "IncludeAccountName": true,
    "CustomPrefix": ""
  },
  
  // Excel report settings
  "Excel": {
    "IncludeCharts": true,
    "IncludeRawData": true,
    "EnableDataValidation": true
  },
  
  // Word report settings
  "Word": {
    "IncludeExecutiveSummary": true,
    "IncludeTechnicalDetails": true,
    "IncludeRecommendations": true
  }
}
```

**Options**:
- **Command Line**: Use `--output <path>` to specify directory
- **Interactive**: Application prompts for directory if not specified
- **Configuration**: Set `OutputDirectory` to use fixed location
- **Auto-Generated**: Uses timestamp-based directory names if not specified
```

## Environment-Specific Configuration

### Development

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  },
  "CosmosDb": {
    "MaxDocumentSamples": 100,
    "PerformanceAnalysisMonths": 1
  }
}
```

### Production

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "CosmosDb": {
    "MaxDocumentSamples": 10000,
    "PerformanceAnalysisMonths": 6
  }
}
```

## Security Configuration

### Azure Key Vault Integration

```json
"AzureKeyVault": {
  "VaultUri": "https://your-vault.vault.azure.net/",
  "UseKeyVault": true,
  "Secrets": {
    "CosmosDbConnectionString": "cosmos-connection-string",
    "StorageAccountKey": "storage-account-key"
  }
}
```

### Managed Identity Configuration

```json
"Azure": {
  "Authentication": {
    "UseManagedIdentity": true,
    "ManagedIdentityClientId": "optional-user-assigned-id"
  }
}
```

## Validation Rules

The application validates configuration on startup:

- **CosmosDb.AccountEndpoint**: Must be valid HTTPS URL
- **CosmosDb.DatabaseName**: Must not be empty
- **AzureMonitor.WorkspaceId**: Must be valid GUID format
- **PerformanceAnalysisMonths**: Must be between 1 and 12
- **MaxDocumentSamples**: Must be between 10 and 100,000

## Configuration Overrides

### Command Line Arguments

```bash
# Override specific settings
dotnet run -- --CosmosDb:DatabaseName "different-database"

# Override Azure region
dotnet run -- --SqlMigration:TargetRegion "West US 2"
```

### Environment Variables

```bash
# Set environment variables (prefix with COSMOSAPP_)
export COSMOSAPP_CosmosDb__DatabaseName="production-database"
export COSMOSAPP_AzureMonitor__WorkspaceId="prod-workspace-id"
```

### Azure App Configuration

For enterprise deployments, integrate with Azure App Configuration:

```json
"AppConfiguration": {
  "ConnectionString": "your-app-config-connection-string",
  "CacheExpirationTime": "00:05:00"
}
```

## Configuration Best Practices

1. **Use Azure Key Vault** for sensitive data in production
2. **Separate configurations** by environment
3. **Use managed identity** when running on Azure
4. **Validate configurations** before deployment
5. **Monitor configuration changes** in production
6. **Document custom settings** for your team
7. **Use configuration transformations** for CI/CD

## Troubleshooting Configuration

### Common Issues

1. **Invalid JSON**: Use a JSON validator to check syntax
2. **Missing required fields**: Check the validation error messages
3. **Authentication failures**: Verify Azure credentials and permissions
4. **Network connectivity**: Ensure firewall rules allow Azure connections

### Diagnostic Commands

```bash
# Test configuration validation
dotnet run -- --validate-config

# Test Azure connectivity
dotnet run -- --test-azure-connection

# Show effective configuration
dotnet run -- --show-config
```
