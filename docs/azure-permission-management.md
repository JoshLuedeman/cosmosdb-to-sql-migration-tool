# Azure Permission Management Feature

## Overview

The Cosmos DB to SQL Migration Assessment Tool now includes an automated Azure permission management feature that:

1. **Checks permissions** before starting analysis
2. **Generates scripts** to create minimal custom roles when permissions are missing
3. **Provides cleanup scripts** to remove roles after analysis completion
4. **Follows principle of least privilege** for maximum security

## How It Works

### 1. Automatic Permission Check
When you run the tool, it automatically checks if you have the required Azure permissions:

```
🔐 Checking Azure permissions...
   ⚠️  Missing 8 required permissions for enhanced metrics
```

### 2. User Options
If permissions are missing, you get three options:

```
⚠️  MISSING AZURE PERMISSIONS DETECTED

Available options:
1. 🛠️  Generate scripts to create custom role and assign to current user
2. ▶️  Continue without enhanced metrics (basic analysis only)
3. 🛑 Generate scripts for administrator and stop application
```

### 3. Script Generation
Both options 1 and 3 create four script files in your output directory:

- `create-cosmos-metrics-role.sh` (Linux/macOS)
- `create-cosmos-metrics-role.ps1` (Windows PowerShell)
- `remove-cosmos-metrics-role.sh` (Linux/macOS cleanup)
- `remove-cosmos-metrics-role.ps1` (Windows PowerShell cleanup)

## Required Permissions

The custom role includes only the **minimum permissions needed for Cosmos DB fallback metrics**:

### Control Plane Permissions (Required)
- `Microsoft.DocumentDB/databaseAccounts/read` - Access account configuration
- `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/read` - Read database properties
- `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/read` - Read container configuration and indexing policies
- `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/throughputSettings/read` - Read database-level throughput (RU/s)
- `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/throughputSettings/read` - Read container-level throughput (RU/s)

### Data Plane Permissions (Required)
- `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read` - Sample documents for schema analysis

### 🔒 **Permissions Intentionally Excluded for Security:**
- `listKeys/readonlykeys` - Not needed with RBAC authentication
- `partitionKeyRangeId/metrics` - Advanced metrics not required for basic fallback
- `consistencyPolicy/locations` - Account details not critical for migration assessment

## Usage Workflow

### Step 1: Run Assessment
```bash
dotnet run --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases
```

### Step 2: Handle Permission Check
If permissions are missing, choose option 1 to generate scripts.

### Step 3: Create Role (with Administrator)
Run the generated script with an Azure administrator account:

**Linux/macOS:**
```bash
chmod +x create-cosmos-metrics-role.sh
./create-cosmos-metrics-role.sh
```

**Windows PowerShell:**
```powershell
.\create-cosmos-metrics-role.ps1
```

### Step 4: Wait for Propagation
Wait 5 minutes for Azure role assignments to take effect.

### Step 5: Re-run Assessment
Run the assessment tool again:
```bash
dotnet run --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases
```

### Step 6: Cleanup (Optional)
After analysis completion, run the cleanup script to remove the custom role:

**Linux/macOS:**
```bash
./remove-cosmos-metrics-role.sh
```

**Windows PowerShell:**
```powershell
.\remove-cosmos-metrics-role.ps1
```

## Security Best Practices

### Principle of Least Privilege ✅
- Role scoped to specific Cosmos DB account only
- No subscription or resource group level permissions
- Only necessary actions included
- Temporary role creation for assessment duration

### Role Management ✅
- Custom role name: `Cosmos-DB-Metrics-Reader`
- Automatic cleanup scripts provided
- Clear documentation of permissions granted
- No permanent access modifications

### Authentication ✅
- Uses Azure DefaultAzureCredential chain
- Supports Azure CLI, Managed Identity, Interactive Browser
- No hardcoded credentials or keys

## Troubleshooting

### Permission Check Fails
```
Permission check error: [Error details]
▶️  Continuing with basic analysis...
```
The tool continues with limited functionality when permission checks fail.

### Script Creation Fails
```
❌ Error creating scripts: [Error details]
```
Check output directory write permissions and try again.

### Role Assignment Takes Time
```
⚠️  Note: Role assignments may take up to 5 minutes to take effect
```
This is normal Azure behavior. Wait 5 minutes before re-running.

### Manual Role Assignment
If automatic role assignment fails:
```
💡 Manually assign the role using:
   az role assignment create --assignee YOUR_USER_EMAIL --role 'Cosmos-DB-Metrics-Reader' --scope '/subscriptions/.../databaseAccounts/...'
```

## Configuration Options

### Disable Permission Checking
Add to `appsettings.json`:
```json
{
  "AzurePermissions": {
    "SkipPermissionCheck": true
  }
}
```

### Custom Role Name
The role name can be customized by modifying the `AzurePermissionService.cs`:
```csharp
var roleName = "Your-Custom-Role-Name";
```

## Benefits

1. **Self-Service**: Users can resolve permission issues without IT tickets
2. **Security**: Minimal permissions following least privilege principle
3. **Temporary**: Easy cleanup after assessment completion
4. **Cross-Platform**: Scripts for both Windows and Linux/macOS
5. **Transparent**: Clear documentation of what permissions are granted

This feature significantly improves the user experience while maintaining security best practices.
