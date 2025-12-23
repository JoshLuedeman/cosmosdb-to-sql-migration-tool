# Example Generated Transformation Stored Procedure

This document shows an example of a generated stored procedure for flattening nested objects in a Cosmos DB to SQL migration.

## Source Data Structure (Cosmos DB)

```json
{
  "id": "123",
  "name": "John Doe",
  "address": {
    "street": "123 Main St",
    "city": "Seattle",
    "state": "WA",
    "zipCode": "98101"
  },
  "contactInfo": {
    "email": "john@example.com",
    "phone": "+1-555-0123"
  }
}
```

## Target SQL Schema

```sql
CREATE TABLE [dbo].[Customers]
(
    [Id] NVARCHAR(100) NOT NULL,
    [Name] NVARCHAR(255) NULL,
    [address_street] NVARCHAR(255) NULL,
    [address_city] NVARCHAR(100) NULL,
    [address_state] NVARCHAR(50) NULL,
    [address_zipCode] NVARCHAR(20) NULL,
    [contactInfo_email] NVARCHAR(255) NULL,
    [contactInfo_phone] NVARCHAR(50) NULL,
    [SourceJson] NVARCHAR(MAX) NULL,  -- Original Cosmos DB document
    [ProcessedFlag] BIT NULL,          -- Transformation tracking
    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id])
);
```

## Generated Transformation Stored Procedure

```sql
-- Stored Procedure: sp_Migrate_FlattenCustomerAddress
-- Transformation Rule: Flatten Customer Address
-- Type: Flatten
-- Logic: Flatten nested address objects into flat columns
-- Affected Tables: Customers
-- Created: 2025-12-23 01:07:21 UTC

CREATE PROCEDURE [dbo].[sp_Migrate_FlattenCustomerAddress]
    @BatchSize INT = 1000,
    @LogProgress BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RowsProcessed INT = 0;
    DECLARE @TotalRows INT = 0;
    DECLARE @StartTime DATETIME2 = GETUTCDATE();

    -- Transformation logic for: Flatten
    -- Pattern: document.address.* => address_*

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Flatten nested objects into flat table columns
        -- This transformation extracts nested JSON properties into individual columns

        -- Process table: Customers
        DECLARE @CurrentBatch_Customers INT = 0;
        DECLARE @TotalBatches_Customers INT;

        -- Get total number of rows to process
        SELECT @TotalBatches_Customers = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)
        FROM [Customers]
        WHERE SourceJson IS NOT NULL; -- Assuming SourceJson column exists

        -- Process batches to avoid locking issues
        WHILE @CurrentBatch_Customers < @TotalBatches_Customers
        BEGIN
            -- Extract and flatten nested JSON properties
            -- Example: Extract 'address.street', 'address.city', 'address.zipCode' from nested object
            UPDATE TOP (@BatchSize) t
            SET 
                -- Flatten nested properties using JSON_VALUE
                -- Example transformations based on source pattern:
                t.address_street = JSON_VALUE(t.SourceJson, '$.address.street'),
                t.address_city = JSON_VALUE(t.SourceJson, '$.address.city'),
                t.address_state = JSON_VALUE(t.SourceJson, '$.address.state'),
                t.address_zipCode = JSON_VALUE(t.SourceJson, '$.address.zipCode'),
                t.contactInfo_email = JSON_VALUE(t.SourceJson, '$.contactInfo.email'),
                t.contactInfo_phone = JSON_VALUE(t.SourceJson, '$.contactInfo.phone'),
                -- Handle null values gracefully
                t.ProcessedFlag = 1, -- Mark as processed
                @RowsProcessed = @RowsProcessed + @@ROWCOUNT
            FROM [Customers] t
            WHERE t.SourceJson IS NOT NULL
                AND (t.ProcessedFlag IS NULL OR t.ProcessedFlag = 0);

            SET @CurrentBatch_Customers = @CurrentBatch_Customers + 1;

            IF @LogProgress = 1
            BEGIN
                PRINT 'Flattening Customers: Batch ' + CAST(@CurrentBatch_Customers AS VARCHAR) + ' of ' + CAST(@TotalBatches_Customers AS VARCHAR);
            END
        END

        -- Additional flattening logic
        -- For complex nested structures, you can use CROSS APPLY with OPENJSON
        -- Example:
        -- UPDATE t
        -- SET t.flattened_column = j.value
        -- FROM [TableName] t
        -- CROSS APPLY OPENJSON(t.SourceJson, '$.path.to.nested') j

        IF @LogProgress = 1
        BEGIN
            PRINT 'Transformation completed successfully.';
            PRINT 'Rows processed: ' + CAST(@RowsProcessed AS VARCHAR(20));
            PRINT 'Execution time: ' + CAST(DATEDIFF(SECOND, @StartTime, GETUTCDATE()) AS VARCHAR(20)) + ' seconds';
        END

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
END
```

## Usage Example

### Execute the Transformation
```sql
-- Execute with default batch size (1000 rows)
EXEC [dbo].[sp_Migrate_FlattenCustomerAddress];

-- Execute with custom batch size and logging enabled
EXEC [dbo].[sp_Migrate_FlattenCustomerAddress] 
    @BatchSize = 5000, 
    @LogProgress = 1;

-- Execute without progress logging for automated processes
EXEC [dbo].[sp_Migrate_FlattenCustomerAddress] 
    @BatchSize = 2000, 
    @LogProgress = 0;
```

### Verify Results
```sql
-- Check transformation progress
SELECT 
    COUNT(*) AS TotalRows,
    SUM(CASE WHEN ProcessedFlag = 1 THEN 1 ELSE 0 END) AS ProcessedRows,
    SUM(CASE WHEN ProcessedFlag IS NULL OR ProcessedFlag = 0 THEN 1 ELSE 0 END) AS RemainingRows
FROM [Customers];

-- View sample transformed data
SELECT TOP 10
    Id,
    Name,
    address_street,
    address_city,
    address_state,
    address_zipCode,
    contactInfo_email,
    contactInfo_phone,
    ProcessedFlag
FROM [Customers]
WHERE ProcessedFlag = 1;
```

### Monitor Performance
```sql
-- Check execution statistics
SELECT 
    execution_count,
    total_elapsed_time / 1000000.0 AS total_elapsed_seconds,
    (total_elapsed_time / execution_count) / 1000000.0 AS avg_elapsed_seconds,
    last_execution_time
FROM sys.dm_exec_procedure_stats
WHERE object_id = OBJECT_ID('sp_Migrate_FlattenCustomerAddress');
```

## Key Features Demonstrated

1. **Batch Processing**: Processes data in configurable batches (default 1000 rows)
2. **Progress Tracking**: Uses `ProcessedFlag` to track transformation state
3. **Error Handling**: Comprehensive error handling with transaction rollback
4. **Logging**: Optional progress logging for monitoring
5. **Performance**: Optimized batch updates to avoid locking
6. **Null Safety**: Handles null JSON values gracefully with `JSON_VALUE`
7. **Flexibility**: Customizable batch size and logging options

## Customization

You can customize the generated procedure to match your specific needs:

1. **Adjust Field Names**: Update the column names to match your actual schema
2. **Add Validations**: Include data quality checks during transformation
3. **Handle Special Cases**: Add custom logic for edge cases in your data
4. **Optimize Batch Size**: Tune batch size based on your dataset and performance requirements
5. **Add Indexes**: Create appropriate indexes before running the transformation

## Related Documentation

- [Transformation Logic Overview](transformation-logic.md)
- [SQL Project Generation](sql-project-generation.md)
- [Getting Started](getting-started.md)
