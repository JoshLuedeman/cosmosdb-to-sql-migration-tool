# SQL Transformation Logic Documentation

## Overview

The Cosmos DB to SQL Migration Tool generates production-ready T-SQL stored procedures that implement data transformation logic. This document explains the five transformation types, their implementation, and how to configure them for your specific migration needs.

## Transformation Types

### 1. Flatten Transformation
**Purpose**: Convert nested JSON objects into flat relational table columns.

**Use Case**: Cosmos DB documents often contain nested objects (e.g., `address.street`, `address.city`). In SQL, these should typically be flattened into columns like `address_street`, `address_city`.

**Generated T-SQL Logic**:
```sql
-- Extracts nested properties using JSON_VALUE
UPDATE t
SET 
    t.address_street = JSON_VALUE(t.SourceJson, '$.address.street'),
    t.address_city = JSON_VALUE(t.SourceJson, '$.address.city'),
    t.address_zipCode = JSON_VALUE(t.SourceJson, '$.address.zipCode'),
    t.ProcessedFlag = 1
FROM [TableName] t
WHERE t.SourceJson IS NOT NULL
    AND (t.ProcessedFlag IS NULL OR t.ProcessedFlag = 0);
```

**Features**:
- Batch processing to avoid locking issues
- Null-safe JSON extraction using `JSON_VALUE`
- `ProcessedFlag` to track transformation progress
- Handles multiple nested levels using `OPENJSON` for complex structures

**Configuration Example**:
```json
{
  "RuleName": "Flatten Address Objects",
  "SourcePattern": "document.address.*",
  "TargetPattern": "address_*",
  "TransformationType": "Flatten",
  "Logic": "Flatten nested address objects into flat columns",
  "AffectedTables": ["Customers", "Suppliers"]
}
```

### 2. Split Transformation
**Purpose**: Normalize array data into child table rows with foreign key relationships.

**Use Case**: Cosmos DB documents with arrays (e.g., `orders.items[]`) need to be split into separate child tables with proper parent-child relationships.

**Generated T-SQL Logic**:
```sql
-- Split array elements into child table rows using OPENJSON
INSERT INTO [ChildTableName] (ParentId, ArrayIndex, ArrayValue, ArrayItemJson)
SELECT 
    t.Id AS ParentId,
    CAST(j.[key] AS INT) AS ArrayIndex,
    j.[value] AS ArrayValue,
    CASE 
        WHEN ISJSON(j.[value]) = 1 THEN j.[value]
        ELSE NULL
    END AS ArrayItemJson
FROM [ParentTable] t
CROSS APPLY OPENJSON(t.ArrayJson) j
WHERE t.ArrayJson IS NOT NULL 
    AND ISJSON(t.ArrayJson) = 1;
```

**Features**:
- Maintains array order using `ArrayIndex`
- Handles both primitive and complex object arrays
- Uses `ISJSON` for validation
- Creates proper foreign key relationships
- Batch processing for large datasets

**Configuration Example**:
```json
{
  "RuleName": "Split Order Items Array",
  "SourcePattern": "document.items[]",
  "TargetPattern": "OrderItems table",
  "TransformationType": "Split",
  "Logic": "Split order items array into child table",
  "AffectedTables": ["Orders"]
}
```

### 3. Combine Transformation
**Purpose**: Merge multiple fields into a single computed or concatenated column.

**Use Case**: Combine separate fields like `firstName` and `lastName` into `fullName`, or create computed values from multiple source fields.

**Generated T-SQL Logic**:
```sql
-- Combine fields using various strategies
UPDATE t
SET 
    -- String concatenation with null handling
    t.FullName = TRIM(CONCAT(t.FirstName, ' ', t.MiddleName, ' ', t.LastName)),
    
    -- Address combination with comma separator
    t.FullAddress = TRIM(CONCAT_WS(', ', t.Street, t.City, t.State, t.ZipCode)),
    
    -- Computed values (e.g., area from width and height)
    t.Area = CASE 
        WHEN t.Width IS NOT NULL AND t.Height IS NOT NULL 
        THEN t.Width * t.Height 
        ELSE NULL 
    END,
    
    -- Date and time combination
    t.FullDateTime = CASE 
        WHEN t.DateValue IS NOT NULL AND t.TimeValue IS NOT NULL
        THEN CAST(CAST(t.DateValue AS DATE) + CAST(t.TimeValue AS TIME) AS DATETIME2)
        ELSE NULL 
    END,
    
    t.CombineProcessedFlag = 1
FROM [TableName] t;
```

**Features**:
- Multiple combination strategies (concatenation, calculation, date/time merging)
- Null-safe operations using `CONCAT`, `COALESCE`, `ISNULL`
- JSON object creation for structured data
- Supports computed columns and derived values

**Configuration Example**:
```json
{
  "RuleName": "Combine Name Fields",
  "SourcePattern": "firstName + lastName",
  "TargetPattern": "fullName",
  "TransformationType": "Combine",
  "Logic": "Combine first and last name into full name",
  "AffectedTables": ["Customers", "Employees"]
}
```

### 4. TypeConvert Transformation
**Purpose**: Convert data types from Cosmos DB format to SQL Server types with validation.

**Use Case**: Handle type conversions between loosely-typed Cosmos DB data and strongly-typed SQL columns, with error handling for invalid conversions.

**Generated T-SQL Logic**:
```sql
-- Perform type conversions with validation
UPDATE t
SET 
    -- String to INT with validation
    t.IntValue = TRY_CAST(t.SourceStringValue AS INT),
    
    -- String to DATETIME2 (ISO 8601 format)
    t.DateTimeValue = TRY_CONVERT(DATETIME2, t.SourceDateString, 127),
    
    -- String to DECIMAL with validation
    t.DecimalValue = TRY_CAST(t.SourceDecimalString AS DECIMAL(18,2)),
    
    -- Boolean strings to BIT
    t.BoolValue = CASE 
        WHEN LOWER(t.SourceBoolString) IN ('true', '1', 'yes', 't', 'y') THEN 1
        WHEN LOWER(t.SourceBoolString) IN ('false', '0', 'no', 'f', 'n') THEN 0
        ELSE NULL
    END,
    
    -- GUID strings to UNIQUEIDENTIFIER
    t.GuidValue = TRY_CAST(t.SourceGuidString AS UNIQUEIDENTIFIER),
    
    -- Unix epoch to DATETIME2
    t.DateTimeFromEpoch = CASE
        WHEN TRY_CAST(t.SourceEpochValue AS BIGINT) IS NOT NULL
        THEN DATEADD(SECOND, t.SourceEpochValue, '1970-01-01')
        ELSE NULL
    END,
    
    -- Track conversion errors
    t.ConversionErrors = CONCAT_WS('; ',
        CASE WHEN TRY_CAST(t.Field1 AS INT) IS NULL AND t.Field1 IS NOT NULL 
            THEN 'Field1: Invalid INT' END,
        CASE WHEN TRY_CAST(t.Field2 AS DATETIME2) IS NULL AND t.Field2 IS NOT NULL 
            THEN 'Field2: Invalid DATE' END
    ),
    
    t.ConvertProcessedFlag = 1
FROM [TableName] t;
```

**Features**:
- Safe conversion using `TRY_CAST` and `TRY_CONVERT`
- Multiple data type support (INT, DATETIME2, DECIMAL, BIT, UNIQUEIDENTIFIER)
- Unix epoch timestamp conversion
- Conversion error tracking and logging
- JSON validation using `ISJSON`

**Configuration Example**:
```json
{
  "RuleName": "Convert Date Strings",
  "SourcePattern": "string dateValue",
  "TargetPattern": "DATETIME2",
  "TransformationType": "TypeConvert",
  "Logic": "Convert date strings to DATETIME2",
  "AffectedTables": ["Events", "Logs"]
}
```

### 5. Custom Transformation
**Purpose**: Provide extensibility for custom business logic and transformations.

**Use Case**: Implement domain-specific transformations, data enrichment, validation rules, or any custom logic not covered by the standard transformation types.

**Generated T-SQL Logic**:
```sql
-- Custom transformation logic with extensibility points
UPDATE t
SET 
    -- Data enrichment from lookup tables
    t.CategoryName = c.Name,
    t.CategoryPath = c.FullPath,
    
    -- Apply business rules (e.g., bulk discount)
    t.DiscountedPrice = CASE 
        WHEN t.Quantity >= 100 THEN t.Price * 0.9  -- 10% bulk discount
        WHEN t.Quantity >= 50 THEN t.Price * 0.95   -- 5% discount
        ELSE t.Price
    END,
    
    -- Data standardization
    t.PhoneNumber = dbo.fn_FormatPhoneNumber(t.RawPhoneNumber),
    t.EmailAddress = LOWER(TRIM(t.EmailAddress)),
    
    t.CustomProcessedFlag = 1
FROM [TableName] t
LEFT JOIN LookupTable c ON t.CategoryId = c.Id;
```

**Features**:
- Extensible framework for custom logic
- Support for joins and data enrichment
- Business rule enforcement
- Data standardization and validation
- Configuration guidance and examples

**Configuration Example**:
```json
{
  "RuleName": "Custom Business Logic",
  "SourcePattern": "custom logic",
  "TargetPattern": "enriched data",
  "TransformationType": "Custom",
  "Logic": "Apply custom business rules and enrichment",
  "AffectedTables": ["Products"]
}
```

## Common Features Across All Transformations

### 1. Batch Processing
All transformations support batch processing to handle large datasets without locking issues:

```sql
DECLARE @BatchSize INT = 1000;
DECLARE @CurrentBatch INT = 0;
DECLARE @TotalBatches INT;

-- Calculate total batches
SELECT @TotalBatches = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)
FROM [TableName];

-- Process in batches
WHILE @CurrentBatch < @TotalBatches
BEGIN
    -- Transformation logic here
    SET @CurrentBatch = @CurrentBatch + 1;
END
```

### 2. Error Handling
Robust error handling with transaction management:

```sql
BEGIN TRY
    BEGIN TRANSACTION;
    
    -- Transformation logic
    
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();
    
    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH
```

### 3. Progress Logging
Optional progress logging for monitoring long-running transformations:

```sql
DECLARE @LogProgress BIT = 1;

IF @LogProgress = 1
BEGIN
    PRINT 'Transformation completed successfully.';
    PRINT 'Rows processed: ' + CAST(@RowsProcessed AS VARCHAR(20));
    PRINT 'Execution time: ' + CAST(DATEDIFF(SECOND, @StartTime, GETUTCDATE()) AS VARCHAR(20)) + ' seconds';
END
```

### 4. Null Handling
All transformations properly handle null values:

- Use `IS NULL` and `IS NOT NULL` checks
- Employ null-safe functions (`COALESCE`, `ISNULL`, `CONCAT`)
- Safe conversion functions (`TRY_CAST`, `TRY_CONVERT`)
- Conditional logic with `CASE` statements

## Edge Cases and Special Scenarios

### Empty Arrays
Split transformation handles empty arrays gracefully:
```sql
WHERE t.ArrayJson IS NOT NULL 
    AND ISJSON(t.ArrayJson) = 1
    AND JSON_QUERY(t.ArrayJson, '$') != '[]'  -- Skip empty arrays
```

### Type Mismatches
TypeConvert transformation logs conversion errors:
```sql
-- Track failed conversions
t.ConversionErrors = CONCAT_WS('; ',
    CASE WHEN TRY_CAST(t.Field AS INT) IS NULL AND t.Field IS NOT NULL 
        THEN 'Field: Invalid INT' END
)
```

### Deeply Nested Objects
Flatten transformation handles multiple nesting levels:
```sql
-- For simple nesting
t.field1 = JSON_VALUE(t.SourceJson, '$.level1.field1')

-- For complex nesting, use OPENJSON
CROSS APPLY OPENJSON(t.SourceJson, '$.path.to.nested') j
```

## Usage in Migration Workflow

### 1. Generated Automatically
Transformation stored procedures are automatically generated based on detected schema patterns:

```bash
# Run assessment - transformations are detected and generated
CosmosToSqlAssessment --database MyDatabase
```

### 2. Review Generated Procedures
Generated stored procedures are in the SQL project:
```
sql-projects/{DatabaseName}.Database/
└── StoredProcedures/
    ├── sp_Migrate_FlattenAddressObjects.sql
    ├── sp_Migrate_SplitOrderItemsArray.sql
    ├── sp_Migrate_CombineNameFields.sql
    ├── sp_Migrate_ConvertDateStrings.sql
    └── sp_Migrate_CustomBusinessLogic.sql
```

### 3. Customize if Needed
Review and adjust the generated procedures to match your specific requirements:

```sql
-- Example: Adjust field names to match your schema
UPDATE t
SET 
    t.actual_column_name = JSON_VALUE(t.SourceJson, '$.actual.field.path'),
    t.ProcessedFlag = 1
FROM [YourTableName] t;
```

### 4. Execute Transformations
Run the stored procedures after data migration:

```sql
-- Execute transformations in order
EXEC sp_Migrate_FlattenAddressObjects @BatchSize = 5000, @LogProgress = 1;
EXEC sp_Migrate_SplitOrderItemsArray @BatchSize = 1000, @LogProgress = 1;
EXEC sp_Migrate_ConvertDateStrings @BatchSize = 2000, @LogProgress = 1;
```

## Performance Considerations

### Batch Size Selection
Choose appropriate batch sizes based on data volume:
- Small datasets (< 10K rows): 1000-5000
- Medium datasets (10K-1M rows): 500-1000
- Large datasets (> 1M rows): 100-500

### Index Strategy
Create appropriate indexes before running transformations:
```sql
-- Helpful for filtering processed rows
CREATE NONCLUSTERED INDEX IX_ProcessedFlag 
ON [TableName](ProcessedFlag) 
WHERE ProcessedFlag IS NULL OR ProcessedFlag = 0;
```

### Parallel Execution
For independent transformations on different tables, consider parallel execution:
```sql
-- Use separate sessions for independent transformations
-- Session 1: Transform Table A
-- Session 2: Transform Table B
```

## Testing and Validation

### 1. Test on Sample Data
Always test transformations on a small dataset first:
```sql
-- Create a test table with sample data
SELECT TOP 100 * 
INTO [TableName_Test]
FROM [TableName];

-- Run transformation on test table
-- Verify results before production run
```

### 2. Validate Results
Check transformation results:
```sql
-- Count processed rows
SELECT 
    COUNT(*) AS TotalRows,
    SUM(CASE WHEN ProcessedFlag = 1 THEN 1 ELSE 0 END) AS ProcessedRows,
    SUM(CASE WHEN ConversionErrors IS NOT NULL THEN 1 ELSE 0 END) AS ErrorRows
FROM [TableName];

-- Review error details
SELECT * 
FROM [TableName]
WHERE ConversionErrors IS NOT NULL;
```

### 3. Performance Monitoring
Monitor transformation performance:
```sql
-- Enable Query Store for performance tracking
ALTER DATABASE [YourDatabase] SET QUERY_STORE = ON;

-- Review execution statistics
SELECT 
    query_id,
    execution_count,
    avg_duration/1000 as avg_duration_ms,
    avg_logical_reads
FROM sys.query_store_query
WHERE object_id = OBJECT_ID('sp_Migrate_TransformationName');
```

## Troubleshooting

### Issue: Transformation Too Slow
**Solution**: 
- Reduce batch size
- Create appropriate indexes
- Check for blocking queries
- Consider running during off-peak hours

### Issue: Conversion Errors
**Solution**:
- Review `ConversionErrors` column for details
- Adjust source data or conversion logic
- Use more lenient type conversions
- Add data quality checks

### Issue: Memory Issues
**Solution**:
- Reduce batch size
- Process tables sequentially instead of in parallel
- Increase SQL Server memory allocation
- Consider incremental processing

## Best Practices

1. **Always Test First**: Test transformations on sample data before production
2. **Monitor Progress**: Enable logging to track transformation progress
3. **Handle Errors**: Review and address conversion errors
4. **Document Changes**: Keep track of any customizations made to generated procedures
5. **Version Control**: Store transformation scripts in source control
6. **Backup Data**: Always backup data before running transformations
7. **Validate Results**: Verify data integrity after transformations
8. **Performance Tuning**: Monitor and optimize transformation performance

## Related Documentation

- [SQL Project Generation](sql-project-generation.md)
- [Architecture Overview](architecture.md)
- [Getting Started](getting-started.md)
- [Troubleshooting](troubleshooting.md)
