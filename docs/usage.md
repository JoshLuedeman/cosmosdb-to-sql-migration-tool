# Usage Guide

## Command-Line Interface

The application is designed as a **command-line first tool** with all key parameters provided via arguments for security and flexibility.

### Basic Usage

```bash
# Analyze a specific database
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --database "YourDatabase" --output "C:\Reports"

# Analyze all databases in the account
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases --output "C:\Reports"
```

### Command-Line Options

```bash
dotnet run -- --help
```

**Available Options:**
- `--endpoint, -e` : Cosmos DB endpoint URL (required)
- `--database, -d` : Specific database name to analyze
- `--all-databases` : Analyze all databases in the account
- `--output, -o` : Output directory for reports
- `--auto-discover` : Auto-discover Azure Monitor settings
- `--help` : Show help information

### Report Output Structure

The application creates a **timestamped folder** for each analysis run:

```
C:\Reports\
└── CosmosDB-Analysis_2025-08-21__14-30-45\
    ├── DatabaseA-Analysis.xlsx
    ├── DatabaseB-Analysis.xlsx
    ├── DatabaseC-Analysis.xlsx
    └── Migration-Assessment.docx
```

**File Structure:**
- **Timestamped Folder**: `CosmosDB-Analysis_YYYY-MM-dd__HH-mm-ss`
- **Excel Files**: `{DatabaseName}-Analysis.xlsx` (one per database)
- **Word Document**: `Migration-Assessment.docx` (combined assessment)

### Multi-Database Analysis

#### Analyze All Databases
```bash
dotnet run -- --endpoint "https://your-cosmos-account.documents.azure.com:443/" --all-databases --output "C:\MultiDBAssessment"
```

This will:
- Connect to your Cosmos DB account
- Discover all databases automatically
- Generate separate Excel reports for each database
- Create one combined Word document with proper heading structure
- Use proper Word styles for navigation and accessibility
- Discover all databases automatically
- Analyze each database independently  
- Generate combined assessment results
- Create consolidated reports covering all databases

#### Interactive Output Directory Selection
```bash
# Application will prompt for output directory
dotnet run
```

If no output directory is specified via command line or configuration, the application will:
1. Prompt you to enter a directory path
2. Suggest a default timestamped directory
3. Create the directory if it doesn't exist
4. Validate write permissions

### Advanced Usage Examples

#### Enterprise Multi-Database Assessment

```bash
# Full enterprise assessment across all databases
dotnet run -- \
  --all-databases \
  --output "\\shared\reports\cosmos-migration-$(Get-Date -Format 'yyyyMMdd')" \
  --auto-discover
```

This command will:
- Analyze all databases in the Cosmos DB account
- Auto-discover Azure Monitor settings
- Save reports to a shared network location with date stamp
- Generate comprehensive cross-database analysis

#### Single Database Deep Analysis

```bash
# Detailed analysis of specific database
dotnet run -- \
  --database "production-ecommerce" \
  --output "C:\Reports\EcommerceAnalysis"
```

#### Quick Development Assessment

```bash
# Fast assessment for development environments
dotnet run -- \
  --database "dev-testing" \
  --output ".\dev-reports"
```

#### Automated Assessment Pipeline

```bash
# For CI/CD or scheduled assessments
dotnet run -- \
  --all-databases \
  --output "%ASSESSMENT_OUTPUT_DIR%" \
  --auto-discover
```

## Understanding the Assessment Process

### Phase 1: Connection and Discovery

```
🔍 Connecting to Azure services...
   ✅ Cosmos DB account: myapp-cosmos
   ✅ Database: ecommerce
   ✅ Azure Monitor workspace: connected
   ✅ Found 5 containers for analysis
```

**What happens**: The application authenticates with Azure and discovers your Cosmos DB structure.

### Phase 2: Container Analysis

```
📊 Analyzing Cosmos DB containers...
   📦 users (15,234 documents, 2.1 GB)
   📦 orders (89,567 documents, 5.8 GB)
   📦 products (2,456 documents, 0.3 GB)
   📦 inventory (45,789 documents, 1.2 GB)
   📦 sessions (234,567 documents, 8.9 GB)
```

**What happens**: Each container is analyzed for:
- Document count and storage size
- Schema detection and field analysis
- Partition key effectiveness
- Indexing policy review

### Phase 3: Performance Metrics Collection

```
⚡ Collecting performance metrics (6 months)...
   📈 Request unit consumption patterns
   📈 Latency percentiles (P95, P99)
   📈 Throttling events and error rates
   📈 Regional distribution analysis
```

**What happens**: Historical performance data is collected from Azure Monitor to understand usage patterns.

### Phase 4: SQL Migration Assessment

```
🎯 Generating SQL migration assessment...
   🏗️  Platform recommendation: Azure SQL Database
   🏗️  Service tier: Business Critical Gen5 8 vCore
   🏗️  Estimated monthly cost: $2,847.60
   🏗️  Migration complexity: Medium (6.2/10)
```

**What happens**: AI-driven analysis recommends optimal Azure SQL configuration based on your workload.

### Phase 5: Data Factory Migration Planning

```
🚛 Calculating Data Factory migration estimates...
   ⏱️  Estimated duration: 14.2 hours
   💰 Estimated cost: $47.83
   🔧 Recommended DIUs: 8
   🔄 Parallel degree: 6
```

**What happens**: Migration timeline and costs are calculated based on data volume and complexity.

### Phase 6: Report Generation

```
📄 Generating assessment reports...
   ✅ Excel report: Reports/CosmosDB_Assessment_20250820_143022.xlsx
   ✅ Word summary: Reports/CosmosDB_Assessment_20250820_143022.docx
```

**What happens**: Professional reports are generated for technical and executive audiences.

## Interpreting Results

### Excel Report Structure

The Excel report contains multiple worksheets:

#### 1. Executive Summary
- **High-level metrics**: Container count, data volume, cost estimates
- **Complexity assessment**: Overall migration difficulty score
- **Key recommendations**: Top 3-5 actionable items

#### 2. Container Analysis
- **Per-container details**: Document counts, storage, performance
- **Schema complexity**: Field types, nesting levels, arrays
- **Partition key effectiveness**: Hot partitions, distribution

#### 3. Performance Metrics
- **Historical trends**: 6-month RU consumption patterns
- **Latency analysis**: P95/P99 response times
- **Throttling events**: When and why throttling occurred

#### 4. SQL Migration Plan
- **Platform recommendation**: SQL Database vs Managed Instance vs VM
- **Sizing details**: vCores, storage, service tier
- **Cost breakdown**: Compute, storage, backup costs

#### 5. Data Factory Estimates
- **Migration timeline**: Detailed time estimates per container
- **Resource requirements**: DIU recommendations
- **Cost projections**: Detailed cost breakdown

### Word Report Overview

The Word document provides:
- **Executive summary** for stakeholders
- **Key findings** and recommendations
- **Next steps** for migration planning
- **Risk assessment** and mitigation strategies

### Understanding Complexity Scores

| Score | Level | Description |
|-------|-------|-------------|
| 1-3 | Low | Simple schema, minimal transformations needed |
| 4-6 | Medium | Moderate complexity, some data transformation required |
| 7-8 | High | Complex schema, significant transformation work |
| 9-10 | Very High | Extensive restructuring and custom migration logic needed |

## Common Scenarios

### Scenario 1: E-commerce Platform Migration

**Context**: Online store with user profiles, product catalog, and order history

**Assessment Command**:
```bash
dotnet run -- --database "ecommerce" --analysis-months 12
```

**Expected Results**:
- **Users container**: Low complexity (simple profile data)
- **Products container**: Medium complexity (nested categories, arrays)
- **Orders container**: High complexity (embedded line items, complex relationships)

**Recommendations**:
- Normalize nested order data into separate tables
- Consider Azure SQL Database Business Critical tier
- Implement staged migration approach

### Scenario 2: IoT Data Migration

**Context**: Time-series data from IoT sensors

**Assessment Command**:
```bash
dotnet run -- --containers "sensor-data,device-metadata" --analysis-months 3
```

**Expected Results**:
- **Very high data volume** with simple schema
- **High RU consumption** for writes
- **Time-based partitioning** requirements

**Recommendations**:
- Consider Azure SQL Hyperscale for large datasets
- Implement time-based table partitioning
- Use columnstore indexes for analytics

### Scenario 3: Multi-tenant SaaS Migration

**Context**: SaaS application with tenant-isolated data

**Assessment Command**:
```bash
dotnet run -- --enable-deep-analysis --analysis-months 6
```

**Expected Results**:
- **Complex tenant isolation** patterns
- **Varied data volumes** per tenant
- **Different access patterns** by tenant size

**Recommendations**:
- Design tenant isolation strategy for SQL
- Consider elastic pools for smaller tenants
- Implement row-level security

## Automation and CI/CD

### Automated Assessments

Create a PowerShell script for regular assessments:

```powershell
# scheduled-assessment.ps1
param(
    [string]$Database,
    [string]$OutputPath,
    [int]$AnalysisMonths = 1
)

$timestamp = Get-Date -Format "yyyyMMdd"
$reportDir = "$OutputPath\$Database-$timestamp"

dotnet run -- \
  --database $Database \
  --analysis-months $AnalysisMonths \
  --output-dir $reportDir \
  --format json

# Upload reports to Azure Storage
az storage blob upload-batch \
  --source $reportDir \
  --destination "assessments" \
  --account-name "myassessments"
```

### Integration with Azure DevOps

```yaml
# azure-pipelines.yml
trigger:
  schedules:
  - cron: "0 2 * * 0"  # Weekly on Sunday at 2 AM
    branches:
      include:
      - main

jobs:
- job: CosmosAssessment
  pool:
    vmImage: 'ubuntu-latest'
  
  steps:
  - task: DotNetCoreCLI@2
    displayName: 'Run Cosmos Assessment'
    inputs:
      command: 'run'
      projects: 'CosmosToSqlAssessment.csproj'
      arguments: '--database production --output-dir $(Build.ArtifactStagingDirectory)'
  
  - task: PublishBuildArtifacts@1
    displayName: 'Publish Assessment Reports'
    inputs:
      pathtoPublish: '$(Build.ArtifactStagingDirectory)'
      artifactName: 'cosmos-assessment'
```

## Performance Tips

### Optimizing Assessment Speed

1. **Limit document sampling**:
   ```bash
   dotnet run -- --max-samples 1000
   ```

2. **Reduce analysis period**:
   ```bash
   dotnet run -- --analysis-months 1
   ```

3. **Analyze specific containers**:
   ```bash
   dotnet run -- --containers "critical-container"
   ```

4. **Disable deep analysis**:
   ```bash
   dotnet run -- --no-deep-analysis
   ```

### Handling Large Datasets

For containers with millions of documents:

```bash
# Use statistical sampling
dotnet run -- \
  --max-samples 10000 \
  --enable-statistical-sampling \
  --confidence-level 95
```

### Memory Optimization

```bash
# For memory-constrained environments
dotnet run -- \
  --batch-size 100 \
  --memory-limit 2GB \
  --enable-streaming
```

## Troubleshooting Assessment Issues

### Common Problems

1. **Timeout errors**: Increase timeout settings in configuration
2. **Memory issues**: Reduce sample sizes and enable streaming
3. **Permission errors**: Verify Azure RBAC assignments
4. **Network issues**: Check firewall and VPN configurations

### Diagnostic Commands

```bash
# Test individual components
dotnet run -- --test-cosmos-connection
dotnet run -- --test-monitor-connection
dotnet run -- --test-report-generation

# Enable detailed logging
dotnet run -- --log-level Debug --log-to-file
```

## Best Practices

1. **Schedule regular assessments** to track changes over time
2. **Use version control** for configuration files
3. **Archive assessment reports** for historical comparison
4. **Review recommendations** with your SQL DBA team
5. **Test migration approaches** in development environments
6. **Monitor performance** after migration completion
