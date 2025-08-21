# Troubleshooting Guide

## Common Issues and Solutions

### Authentication Issues

For comprehensive authentication and permission setup, see the [Azure Permissions Guide](azure-permissions.md).

#### Issue: "Unable to authenticate with Azure"

**Symptoms**:
- Application fails during Azure authentication
- Error messages about missing credentials
- Azure CLI authentication failures

**Solutions**:

1. **Check Azure CLI Login**:
   ```powershell
   az login
   az account show
   ```

2. **Verify Permissions**:
   ```powershell
   # Check Cosmos DB access
   az cosmosdb show --name <cosmos-account> --resource-group <rg>
   
   # Check Monitor access
   az monitor log-analytics workspace show --workspace-name <workspace>
   ```

3. **Set Explicit Credentials** (if managed identity fails):
   ```json
   {
     "AzureAd": {
       "TenantId": "your-tenant-id",
       "ClientId": "your-client-id",
       "ClientSecret": "your-client-secret"
     }
   }
   ```

4. **Environment Variables** (alternative):
   ```powershell
   $env:AZURE_TENANT_ID = "your-tenant-id"
   $env:AZURE_CLIENT_ID = "your-client-id"
   $env:AZURE_CLIENT_SECRET = "your-client-secret"
   ```

#### Issue: "DefaultAzureCredential failed to retrieve a token"

**Root Causes**:
- Not logged into Azure CLI
- Managed Identity not configured
- Service Principal missing permissions

**Resolution Steps**:

1. **Enable Detailed Logging**:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Azure.Identity": "Debug",
         "Azure.Core": "Debug"
       }
     }
   }
   ```

2. **Try Credential Chain Manually**:
   ```csharp
   var credential = new ChainedTokenCredential(
       new ManagedIdentityCredential(),
       new AzureCliCredential(),
       new InteractiveBrowserCredential()
   );
   ```

3. **Test Each Credential Type**:
   ```powershell
   # Test CLI
   az account get-access-token --resource https://cosmos.azure.com/
   
   # Test managed identity (if running in Azure)
   curl "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://cosmos.azure.com/" -H "Metadata: true"
   ```

### Cosmos DB Connection Issues

#### Issue: "CosmosException: Forbidden"

**Symptoms**:
- Cannot connect to Cosmos DB
- 403 Forbidden errors
- "Request blocked by your Cosmos DB account firewall"

**Solutions**:

1. **Check Firewall Rules**:
   ```powershell
   # Allow your IP address
   az cosmosdb network-rule add \
     --account-name <cosmos-account> \
     --resource-group <resource-group> \
     --ip-address <your-ip>
   ```

2. **Verify Endpoint URL**:
   ```bash
   # Use command-line arguments instead of configuration file
   dotnet run -- --endpoint "https://your-account.documents.azure.com:443/" --database "correct-database-name" --output "C:\Reports"
   ```

3. **Test Connectivity**:
   ```bash
   # Test with Azure CLI first
   az cosmosdb show --name <cosmos-account> --resource-group <resource-group>
   
   # Then test the application
   dotnet run -- --endpoint "https://your-account.documents.azure.com:443/" --help
   ```

#### Issue: "Database not found"

**Root Cause**: Incorrect database name in configuration

**Solution**:
```powershell
# List available databases
az cosmosdb sql database list --account-name <account> --resource-group <rg>
```

### Performance Issues

#### Issue: "Assessment takes too long"

**Symptoms**:
- Application hangs during analysis
- Very slow container analysis
- High memory usage

**Optimization Strategies**:

1. **Reduce Sample Size**:
   ```json
   {
     "CosmosDb": {
       "MaxSampleDocuments": 100,
       "SamplePercentage": 0.01
     }
   }
   ```

2. **Enable Parallel Processing**:
   ```json
   {
     "Analysis": {
       "MaxParallelContainers": 5,
       "MaxParallelDocuments": 10
     }
   }
   ```

3. **Optimize Queries**:
   ```sql
   -- Instead of SELECT * FROM c
   SELECT TOP 100 c.id, c._ts FROM c
   ```

4. **Monitor Memory Usage**:
   ```csharp
   // Add memory monitoring
   GC.Collect();
   var memoryBefore = GC.GetTotalMemory(false);
   // ... analysis code ...
   var memoryAfter = GC.GetTotalMemory(false);
   Console.WriteLine($"Memory used: {(memoryAfter - memoryBefore) / 1024 / 1024} MB");
   ```

#### Issue: "Out of Memory Exception"

**Solutions**:

1. **Stream Large Results**:
   ```csharp
   var iterator = container.GetItemQueryIterator<dynamic>(
       "SELECT * FROM c",
       requestOptions: new QueryRequestOptions
       {
           MaxItemCount = 100 // Smaller page size
       });
   ```

2. **Process in Batches**:
   ```csharp
   await foreach (var document in GetDocumentsAsync())
   {
       ProcessDocument(document);
       
       if (++count % 1000 == 0)
       {
           GC.Collect(); // Force cleanup every 1000 docs
       }
   }
   ```

3. **Increase Memory Limits** (if running in Azure):
   ```yaml
   resources:
     requests:
       memory: "4Gi"
     limits:
       memory: "8Gi"
   ```

### Azure Monitor Issues

#### Issue: "No performance metrics found"

**Root Causes**:
- Wrong workspace ID
- Insufficient time range
- Missing metric data

**Solutions**:

1. **Verify Workspace Configuration**:
   ```json
   {
     "AzureMonitor": {
       "WorkspaceId": "correct-workspace-id",
       "TimeRangeHours": 168
     }
   }
   ```

2. **Test KQL Query Manually**:
   ```kql
   AzureDiagnostics
   | where ResourceProvider == "MICROSOFT.DOCUMENTDB"
   | where TimeGenerated > ago(7d)
   | take 10
   ```

3. **Check Diagnostic Settings**:
   ```powershell
   az monitor diagnostic-settings list --resource <cosmos-resource-id>
   ```

#### Issue: "KQL query timeout"

**Solutions**:

1. **Optimize Query Performance**:
   ```kql
   // Use time filters early
   AzureDiagnostics
   | where TimeGenerated > ago(24h)
   | where ResourceProvider == "MICROSOFT.DOCUMENTDB"
   | summarize avg(DurationMs) by bin(TimeGenerated, 1h)
   ```

2. **Reduce Time Range**:
   ```json
   {
     "AzureMonitor": {
       "TimeRangeHours": 24
     }
   }
   ```

### Report Generation Issues

#### Issue: "Failed to generate Excel report"

**Symptoms**:
- IOException during file creation
- Corrupted Excel files
- Missing data in reports

**Solutions**:

1. **Check File Permissions**:
   ```powershell
   # Ensure output directory is writable
   Test-Path C:\temp -PathType Container
   [System.IO.Directory]::CreateDirectory("C:\temp")
   ```

2. **Verify Data Integrity**:
   ```csharp
   // Add validation before report generation
   if (result.CosmosAnalysis?.Containers?.Count == 0)
   {
       throw new InvalidOperationException("No containers to report");
   }
   ```

3. **Handle Large Datasets**:
   ```csharp
   // Stream large datasets to Excel
   var worksheet = workbook.Worksheets.Add("Data");
   int row = 1;
   
   foreach (var batch in data.Batch(1000))
   {
       worksheet.Cell(row, 1).InsertData(batch);
       row += batch.Count();
   }
   ```

#### Issue: "Word document creation fails"

**Common Causes**:
- Template file missing
- Invalid document structure
- Encoding issues

**Solutions**:

1. **Verify Template Path**:
   ```csharp
   var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "report-template.docx");
   if (!File.Exists(templatePath))
   {
       throw new FileNotFoundException($"Template not found: {templatePath}");
   }
   ```

2. **Create Simple Document** (if template fails):
   ```csharp
   using var document = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
   var mainPart = document.AddMainDocumentPart();
   mainPart.Document = new Document(new Body());
   ```

### Configuration Issues

#### Issue: "Configuration section not found"

**Symptoms**:
- NullReferenceException during startup
- Missing configuration values
- Default values not working

**Solutions**:

1. **Validate appsettings.json**:
   ```json
   {
     "CosmosDb": {
       "ConnectionString": "required",
       "DatabaseName": "required"
     },
     "AzureMonitor": {
       "WorkspaceId": "required"
     }
   }
   ```

2. **Check File Location**:
   ```powershell
   # Ensure appsettings.json is in output directory
   Get-ChildItem -Path bin\Debug\net8.0\ -Filter "appsettings*"
   ```

3. **Verify Configuration Binding**:
   ```csharp
   // Add validation in Program.cs
   var cosmosConfig = configuration.GetSection("CosmosDb");
   if (!cosmosConfig.Exists())
   {
       throw new InvalidOperationException("CosmosDb configuration missing");
   }
   ```

#### Issue: "Environment-specific configuration not loading"

**Solution**:
```csharp
var builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
    .AddEnvironmentVariables();
```

## Logging and Diagnostics

### Enable Debug Logging

1. **Update appsettings.json**:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Information",
         "Microsoft": "Warning",
         "Microsoft.Hosting.Lifetime": "Information",
         "CosmosToSqlMigration": "Debug",
         "Azure": "Debug"
       }
     }
   }
   ```

2. **Add Console Logging**:
   ```csharp
   logging.AddConsole();
   logging.AddDebug();
   ```

3. **Custom Log Categories**:
   ```csharp
   logger.LogDebug("Starting container analysis: {ContainerName}", containerName);
   logger.LogInformation("Processed {DocumentCount} documents", documentCount);
   logger.LogWarning("Large document detected: {Size} bytes", documentSize);
   logger.LogError(ex, "Failed to analyze container: {ContainerName}", containerName);
   ```

### Performance Monitoring

1. **Add Timing Logs**:
   ```csharp
   using var activity = new Activity("AnalyzeContainer");
   activity.Start();
   
   var stopwatch = Stopwatch.StartNew();
   var result = await AnalyzeContainerAsync(container);
   stopwatch.Stop();
   
   logger.LogInformation("Container analysis completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
   ```

2. **Memory Tracking**:
   ```csharp
   var initialMemory = GC.GetTotalMemory(false);
   
   // ... processing ...
   
   var finalMemory = GC.GetTotalMemory(true);
   logger.LogInformation("Memory delta: {MemoryDelta} bytes", finalMemory - initialMemory);
   ```

## Error Recovery Strategies

### Retry Policies

1. **Azure SDK Retry Configuration**:
   ```csharp
   var cosmosClientOptions = new CosmosClientOptions
   {
       MaxRetryAttemptsOnRateLimitedRequests = 5,
       MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
   };
   ```

2. **Custom Retry Logic**:
   ```csharp
   public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxAttempts = 3)
   {
       for (int attempt = 1; attempt <= maxAttempts; attempt++)
       {
           try
           {
               return await operation();
           }
           catch (Exception ex) when (attempt < maxAttempts && IsRetryableException(ex))
           {
               var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
               logger.LogWarning("Attempt {Attempt} failed, retrying in {Delay}s: {Error}", 
                   attempt, delay.TotalSeconds, ex.Message);
               await Task.Delay(delay);
           }
       }
       
       throw new InvalidOperationException($"Operation failed after {maxAttempts} attempts");
   }
   ```

### Graceful Degradation

1. **Partial Results**:
   ```csharp
   var analysis = new CosmosDbAnalysis();
   var exceptions = new List<Exception>();
   
   foreach (var container in containers)
   {
       try
       {
           var containerAnalysis = await AnalyzeContainerAsync(container);
           analysis.Containers.Add(containerAnalysis);
       }
       catch (Exception ex)
       {
           logger.LogError(ex, "Failed to analyze container {Container}", container.Id);
           exceptions.Add(ex);
       }
   }
   
   if (analysis.Containers.Count == 0 && exceptions.Count > 0)
   {
       throw new AggregateException("All container analyses failed", exceptions);
   }
   ```

2. **Fallback Data Sources**:
   ```csharp
   try
   {
       return await GetPerformanceMetricsFromMonitorAsync();
   }
   catch (Exception ex)
   {
       logger.LogWarning(ex, "Azure Monitor unavailable, using fallback metrics");
       return GetBasicPerformanceMetrics();
   }
   ```

## Support and Resources

### Getting Help

1. **GitHub Issues**: Report bugs and feature requests
2. **Azure Support**: For Azure service-specific issues
3. **Stack Overflow**: Community support with tags: `azure-cosmosdb`, `azure-sql`

### Useful Commands

```powershell
# Check application logs
Get-Content .\logs\assessment-*.log | Select-String "ERROR"

# Test Azure connectivity
az account show
az cosmosdb list
az monitor log-analytics workspace list

# Validate configuration
dotnet user-secrets list
dotnet user-secrets set "CosmosDb:ConnectionString" "your-connection-string"

# Performance profiling
dotnet run --configuration Release
dotnet trace collect --process-id <pid> --output trace.nettrace
```

### Emergency Procedures

1. **Application Hanging**:
   - Check process memory usage
   - Review active Azure connections
   - Restart with smaller sample sizes

2. **Data Corruption**:
   - Verify source Cosmos DB integrity
   - Re-run analysis with validation enabled
   - Check output file permissions

3. **Resource Exhaustion**:
   - Scale up compute resources
   - Implement data streaming
   - Use batched processing

### Log Analysis Examples

```powershell
# Find slow operations
Select-String "elapsed.*[5-9][0-9]{3,}ms" .\logs\*.log

# Track memory issues
Select-String "OutOfMemory|GC|Memory" .\logs\*.log

# Azure authentication problems
Select-String "Authentication|Token|Credential" .\logs\*.log

# Cosmos DB errors
Select-String "CosmosException|Forbidden|Timeout" .\logs\*.log
```
