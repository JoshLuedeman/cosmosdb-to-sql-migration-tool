# Post-Migration Validation Guide

This guide describes how to use the post-migration validation scripts to verify data integrity and performance after migrating from Azure Cosmos DB to Azure SQL.

## Overview

The validation suite consists of four SQL scripts and a PowerShell orchestration script that verify different aspects of the migration:

| Script | Purpose |
|--------|---------|
| `01-RowCountValidation.sql` | Compares expected row counts (from Cosmos DB) against actual SQL row counts |
| `02-DataIntegrityChecks.sql` | Validates foreign keys, indexes, primary keys, checksums, and NULL constraints |
| `03-SampleDataComparison.sql` | Displays first/last N rows per table for manual data verification |
| `04-PerformanceBaseline.sql` | Captures database size, index usage, fragmentation, and performance metrics |
| `RunAllValidations.ps1` | Orchestrates all scripts and generates a consolidated report |

## Prerequisites

- **SQL Server tools**: Either `sqlcmd` CLI or the `SqlServer` PowerShell module
  ```powershell
  # Install SqlServer module (recommended)
  Install-Module -Name SqlServer -Scope CurrentUser
  ```
- **Database access**: Read permissions on the target SQL database
- **Assessment report**: Row count data from the Cosmos DB migration assessment (for script 01)

## Quick Start

### Run All Validations

```powershell
# Using Windows Authentication
.\Scripts\PostMigration\RunAllValidations.ps1 `
    -ServerName "localhost" `
    -DatabaseName "MyMigratedDB" `
    -UseWindowsAuth

# Using SQL Authentication
.\Scripts\PostMigration\RunAllValidations.ps1 `
    -ServerName "myserver.database.windows.net" `
    -DatabaseName "MyMigratedDB" `
    -UserName "sqladmin" `
    -Password "YourPassword"
```

### Run Individual Scripts

```powershell
# Using sqlcmd
sqlcmd -S localhost -d MyMigratedDB -E -i Scripts\PostMigration\01-RowCountValidation.sql

# Using Invoke-Sqlcmd
Invoke-Sqlcmd -ServerInstance "localhost" -Database "MyMigratedDB" -InputFile "Scripts\PostMigration\02-DataIntegrityChecks.sql"
```

## Script Details

### 01 — Row Count Validation

Compares the number of rows in each SQL table against expected counts from the Cosmos DB source.

**Setup required**: Edit the script to populate expected row counts from your migration assessment report:

```sql
INSERT INTO @ExpectedCounts VALUES ('dbo', 'Users', 150000);
INSERT INTO @ExpectedCounts VALUES ('dbo', 'Orders', 500000);
INSERT INTO @ExpectedCounts VALUES ('dbo', 'Products', 25000);
```

**Configuration**:
- `@TolerancePercent`: Set to allow a percentage variance (default: `0.00` for exact match)

**Output**: Pass/fail status per table with variance details.

### 02 — Data Integrity Checks

Validates structural integrity of the migrated database:

- **Foreign key integrity**: Detects orphaned child rows that violate referential integrity
- **Index existence**: Lists all indexes and flags tables missing non-clustered indexes
- **Primary key validation**: Ensures every table has a primary key
- **Checksum validation**: Computes `CHECKSUM_AGG` per table for data consistency tracking
- **NOT NULL compliance**: Verifies no NULL values exist in NOT NULL columns

**Output**: Pass/fail per check category with detailed discrepancy reporting.

### 03 — Sample Data Comparison

Retrieves sample rows from each table for manual comparison against source data.

**Configuration**:
- `@SampleSize`: Number of rows to retrieve from each end (default: `5`)

**Output**:
- First N and last N rows per table (ordered by primary key)
- Column data type summary
- Empty table warnings

### 04 — Performance Baseline

Establishes performance metrics as a baseline for ongoing monitoring.

**Metrics captured**:
- Database and table space usage
- Index usage statistics (seeks, scans, lookups)
- Index fragmentation levels with maintenance recommendations
- Table scan durations
- Wait statistics snapshot
- Missing index suggestions from the query optimizer

**Output**: Performance metrics with ratings and recommendations.

## PowerShell Runner Options

```powershell
.\RunAllValidations.ps1
    -ServerName <string>        # SQL Server instance (default: localhost)
    -DatabaseName <string>      # Target database (required)
    -OutputPath <string>        # Report output directory (default: ./ValidationResults)
    -UseWindowsAuth             # Use Windows Authentication
    -UserName <string>          # SQL Auth username
    -Password <string>          # SQL Auth password
    -ScriptsPath <string>       # Path to SQL scripts (default: script directory)
    -SkipScripts <int[]>        # Script numbers to skip (e.g., @(3,4))
```

### Output Files

The runner generates:
- `ValidationReport_<timestamp>.txt` — Consolidated report with all results
- `01-RowCountValidation_<timestamp>.txt` — Individual script output
- `02-DataIntegrityChecks_<timestamp>.txt` — Individual script output
- `03-SampleDataComparison_<timestamp>.txt` — Individual script output
- `04-PerformanceBaseline_<timestamp>.txt` — Individual script output

### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | All validations passed |
| `1` | One or more validations failed |

## Interpreting Results

Each validation outputs a clear **PASS**, **FAIL**, or **WARNING** status:

- **PASS**: Validation completed successfully with no issues
- **FAIL**: Issues detected that require investigation
- **WARNING**: Non-critical items that should be reviewed

### Common Issues and Resolutions

| Issue | Possible Cause | Resolution |
|-------|---------------|------------|
| Row count mismatch | Incomplete data transfer | Re-run ADF pipeline for affected tables |
| Orphaned FK rows | Data loaded out of order | Reload parent table first, then children |
| Missing indexes | Index creation skipped | Run the SQL project DDL scripts |
| Empty tables | Pipeline filter issue | Check ADF pipeline source query |
| High fragmentation | Bulk insert operations | Rebuild indexes post-migration |

## Integration with Migration Workflow

1. **Pre-migration**: Run the Cosmos DB assessment tool to generate expected counts
2. **Migration**: Execute ADF pipelines (see issue #10)
3. **Post-migration**: Run these validation scripts
4. **Remediation**: Address any failures and re-run validations
5. **Sign-off**: Archive the validation report as migration evidence

## Related

- [Data Quality Analysis](transformation-logic.md) — Pre-migration data quality checks
- [SQL Project Generation](sql-project-generation.md) — Schema and index DDL generation
