# Azure Permissions and Resource Requirements

## Overview

This document provides comprehensive details about the Azure permissions, resources, and configurations required for the Cosmos DB to SQL Migration Assessment Tool to function properly and gather all designed information.

## 🔐 Required Azure Permissions

### Minimum Required Permissions

To use the basic functionality of the tool, your Azure account or service principal needs the following permissions:

#### Cosmos DB Permissions
- **Role**: `Cosmos DB Account Reader`
- **Scope**: Cosmos DB Account level
- **Purpose**: Read database and container metadata, indexing policies, and throughput settings
- **Alternative**: `DocumentDB Account Contributor` (provides broader access)

#### Azure Active Directory Permissions
- **Role**: `Azure Active Directory User` (if using Azure CLI authentication)
- **Purpose**: Authentication and token acquisition for Azure services

### Enhanced Functionality Permissions

For complete functionality including historical performance metrics, additional permissions are required:

#### Azure Monitor / Log Analytics Permissions
- **Role**: `Log Analytics Reader`
- **Scope**: Log Analytics Workspace or Subscription level
- **Purpose**: Query Azure Monitor logs for Cosmos DB performance metrics
- **Alternative**: `Monitoring Reader` (subscription-wide monitoring access)

#### Resource Group Permissions (for auto-discovery)
- **Role**: `Reader`
- **Scope**: Resource Group containing Cosmos DB account
- **Purpose**: Auto-discover Log Analytics workspaces and related monitoring resources

#### Subscription Permissions (for comprehensive analysis)
- **Role**: `Reader`
- **Scope**: Subscription level
- **Purpose**: Access to all monitoring and diagnostic resources associated with Cosmos DB

## 🏗️ Required Azure Resources

### Core Resources (Required)

#### Azure Cosmos DB Account
- **Resource Type**: `Microsoft.DocumentDB/databaseAccounts`
- **API**: SQL API (Core)
- **Requirements**:
  - At least one database and container
  - Account must be accessible from your execution environment
  - Network access configured (public endpoint or private endpoint with proper connectivity)

#### Azure Active Directory Tenant
- **Purpose**: Authentication and authorization
- **Requirements**:
  - User account or service principal with appropriate permissions
  - Multi-factor authentication may be required depending on tenant policies

### Enhanced Analytics Resources (Recommended)

#### Log Analytics Workspace
- **Resource Type**: `Microsoft.OperationalInsights/workspaces`
- **Purpose**: Store and query Azure Monitor logs for Cosmos DB
- **Requirements**:
  - Connected to Cosmos DB account for diagnostic logging
  - Minimum 30 days of log retention for meaningful analysis
  - Configured diagnostic settings on Cosmos DB account

#### Azure Monitor Diagnostic Settings
- **Resource Type**: `Microsoft.Insights/diagnosticSettings`
- **Purpose**: Stream Cosmos DB metrics and logs to Log Analytics
- **Required Configuration**:
  ```json
  {
    "logs": [
      {
        "category": "DataPlaneRequests",
        "enabled": true
      },
      {
        "category": "MongoRequests", 
        "enabled": false
      },
      {
        "category": "QueryRuntimeStatistics",
        "enabled": true
      },
      {
        "category": "PartitionKeyStatistics",
        "enabled": true
      },
      {
        "category": "ControlPlaneRequests",
        "enabled": true
      }
    ],
    "metrics": [
      {
        "category": "Requests",
        "enabled": true
      }
    ]
  }
  ```

## 🎯 Permission Setup Guide

### Option 1: Azure CLI Authentication (Development)

1. **Install Azure CLI**:
   ```bash
   # Windows
   winget install Microsoft.AzureCLI
   
   # macOS
   brew install azure-cli
   
   # Linux
   curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
   ```

2. **Login and Set Subscription**:
   ```bash
   az login
   az account set --subscription "your-subscription-id"
   ```

3. **Verify Permissions**:
   ```bash
   # Test Cosmos DB access
   az cosmosdb show --name "your-cosmos-account" --resource-group "your-rg"
   
   # Test Log Analytics access
   az monitor log-analytics workspace show --workspace-name "your-workspace" --resource-group "your-rg"
   ```

### Option 2: Service Principal (Production)

1. **Create Service Principal**:
   ```bash
   az ad sp create-for-rbac --name "cosmos-migration-tool" --role "Reader" --scopes "/subscriptions/your-subscription-id"
   ```

2. **Assign Cosmos DB Permissions**:
   ```bash
   az role assignment create \
     --assignee "service-principal-id" \
     --role "Cosmos DB Account Reader" \
     --scope "/subscriptions/your-subscription-id/resourceGroups/your-rg/providers/Microsoft.DocumentDB/databaseAccounts/your-cosmos-account"
   ```

3. **Assign Log Analytics Permissions**:
   ```bash
   az role assignment create \
     --assignee "service-principal-id" \
     --role "Log Analytics Reader" \
     --scope "/subscriptions/your-subscription-id/resourceGroups/your-rg/providers/Microsoft.OperationalInsights/workspaces/your-workspace"
   ```

4. **Set Environment Variables**:
   ```bash
   export AZURE_TENANT_ID="your-tenant-id"
   export AZURE_CLIENT_ID="your-client-id"
   export AZURE_CLIENT_SECRET="your-client-secret"
   ```

### Option 3: Managed Identity (Azure Hosted)

When running on Azure compute services (VMs, Container Instances, App Service), assign a managed identity:

1. **Enable System-Assigned Managed Identity**:
   ```bash
   az vm identity assign --name "your-vm" --resource-group "your-rg"
   ```

2. **Assign Required Permissions**:
   ```bash
   # Get the managed identity principal ID
   PRINCIPAL_ID=$(az vm identity show --name "your-vm" --resource-group "your-rg" --query principalId -o tsv)
   
   # Assign Cosmos DB permissions
   az role assignment create \
     --assignee $PRINCIPAL_ID \
     --role "Cosmos DB Account Reader" \
     --scope "/subscriptions/your-subscription-id/resourceGroups/your-rg/providers/Microsoft.DocumentDB/databaseAccounts/your-cosmos-account"
   
   # Assign Log Analytics permissions
   az role assignment create \
     --assignee $PRINCIPAL_ID \
     --role "Log Analytics Reader" \
     --scope "/subscriptions/your-subscription-id/resourceGroups/your-rg/providers/Microsoft.OperationalInsights/workspaces/your-workspace"
   ```

## 📊 Resource Configuration Requirements

### Cosmos DB Account Configuration

#### Required Settings
- **API**: SQL (Core)
- **Consistency Level**: Any (tool reads metadata only)
- **Network Access**: 
  - Public endpoint with allowed IP ranges, OR
  - Private endpoint with connectivity from execution environment

#### Recommended Settings for Enhanced Analysis
- **Diagnostic Settings**: Enabled with logs flowing to Log Analytics
- **Azure Monitor**: Metrics collection enabled
- **Indexing Policy**: Documented for migration analysis

### Log Analytics Workspace Configuration

#### Required Settings
- **Workspace**: Deployed in same region as Cosmos DB (recommended)
- **Retention**: Minimum 30 days, recommended 90+ days
- **Data Sources**: Cosmos DB diagnostic logs configured

#### Required Diagnostic Categories
Enable these categories in Cosmos DB diagnostic settings:
- `DataPlaneRequests` - Request-level metrics
- `QueryRuntimeStatistics` - Query performance data  
- `PartitionKeyStatistics` - Partition usage patterns
- `ControlPlaneRequests` - Management operations

### Network Requirements

#### Outbound Connectivity
The tool requires outbound HTTPS connectivity to:
- `*.documents.azure.com` (Cosmos DB endpoints)
- `*.ods.opinsights.azure.com` (Azure Monitor ingestion)
- `*.oms.opinsights.azure.com` (Azure Monitor query)
- `login.microsoftonline.com` (Azure AD authentication)

#### Firewall Rules
If using IP-based access control on Cosmos DB:
- Add the execution environment's public IP to Cosmos DB firewall rules
- Consider using service endpoints or private endpoints for enhanced security

## 🔍 Validation Checklist

Use this checklist to verify your environment is properly configured:

### Authentication Validation
- [ ] Azure CLI login successful (`az account show`)
- [ ] Correct subscription selected
- [ ] Service principal credentials configured (if applicable)
- [ ] Managed identity enabled and configured (if applicable)

### Cosmos DB Access Validation
- [ ] Can list databases: `az cosmosdb sql database list --account-name "account" --resource-group "rg"`
- [ ] Can read account properties: `az cosmosdb show --name "account" --resource-group "rg"`
- [ ] Network connectivity confirmed

### Azure Monitor Access Validation
- [ ] Can access Log Analytics workspace: `az monitor log-analytics workspace show --workspace-name "workspace" --resource-group "rg"`
- [ ] Diagnostic settings configured on Cosmos DB account
- [ ] Recent log data available in workspace

### Tool Execution Validation
- [ ] Help command works: `dotnet run -- --help`
- [ ] Authentication test passes: `dotnet run -- --endpoint "https://account.documents.azure.com:443/" --database "test" --output "./test" --dry-run`

## 🚨 Common Permission Issues

### "Authentication Failed" Error
**Cause**: Insufficient Azure AD permissions or expired tokens
**Solution**: 
1. Run `az login` to refresh authentication
2. Verify account has required roles
3. Check multi-factor authentication requirements

### "Cosmos DB Access Denied" Error  
**Cause**: Missing Cosmos DB Account Reader role
**Solution**:
1. Assign `Cosmos DB Account Reader` role at account level
2. Verify network access (firewall rules, private endpoints)
3. Check subscription access

### "Log Analytics Access Denied" Error
**Cause**: Missing Log Analytics Reader role
**Solution**:
1. Assign `Log Analytics Reader` role at workspace level
2. Verify workspace exists and is accessible
3. Check diagnostic settings configuration

### "No Performance Data Available" Warning
**Cause**: Diagnostic settings not configured or insufficient data
**Solution**:
1. Configure diagnostic settings on Cosmos DB account
2. Wait 24-48 hours for sufficient data collection
3. Verify logs are flowing to Log Analytics workspace

## 📋 Security Best Practices

### Principle of Least Privilege
- Use specific resource-scoped permissions rather than subscription-wide access
- Prefer `Reader` roles over `Contributor` roles where possible
- Regularly review and rotate service principal credentials

### Network Security
- Use private endpoints for Cosmos DB when possible
- Implement network security groups and firewall rules
- Monitor access patterns and unusual activities

### Data Protection
- Tool processes metadata only, not document content
- Reports are generated locally and not transmitted to external services
- Ensure secure handling of generated reports containing migration assessments

## 📞 Support and Troubleshooting

For permission-related issues:
1. Review the [Troubleshooting Guide](troubleshooting.md)
2. Use the `--verbose` flag for detailed error information
3. Check Azure Activity Logs for permission denials
4. Validate network connectivity using Azure CLI commands
