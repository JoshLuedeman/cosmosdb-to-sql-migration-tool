/*
    Post-Migration Validation: Sample Data Comparison
    ==================================================
    Purpose: Compares sample data from migrated tables to verify data accuracy.
             Displays first and last N rows per table for manual or automated review.
    
    Usage:
        1. Configure @SampleSize to set how many rows to retrieve from each end.
        2. Execute this script against the target SQL database.
        3. Compare output against source Cosmos DB data exports.
    
    Notes:
        - Uses primary key ordering where available, falls back to first column.
        - Designed to provide a quick visual comparison for data spot-checks.
        - For large tables, only samples are compared to keep execution fast.
*/

SET NOCOUNT ON;

-- ============================================================================
-- Configuration
-- ============================================================================
DECLARE @SampleSize INT = 5; -- Number of rows to retrieve from each end of each table

PRINT '================================================================';
PRINT ' Post-Migration Validation: Sample Data Comparison';
PRINT ' Database: ' + DB_NAME();
PRINT ' Server:   ' + @@SERVERNAME;
PRINT ' Run Date: ' + CONVERT(VARCHAR(30), GETUTCDATE(), 120) + ' UTC';
PRINT ' Sample Size: ' + CAST(@SampleSize AS VARCHAR(10)) + ' rows (first/last)';
PRINT '================================================================';
PRINT '';

-- ============================================================================
-- Gather table metadata
-- ============================================================================
DECLARE @Tables TABLE (
    SchemaName    NVARCHAR(128),
    TableName     NVARCHAR(128),
    OrderColumn   NVARCHAR(128),
    RowCount      BIGINT,
    ColumnList    NVARCHAR(MAX)
);

-- Identify tables with their primary key or first column for ordering
INSERT INTO @Tables (SchemaName, TableName, OrderColumn, RowCount)
SELECT 
    s.name,
    t.name,
    COALESCE(
        -- Use primary key column
        (SELECT TOP 1 c.name 
         FROM sys.index_columns ic
         INNER JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
         INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
         WHERE i.object_id = t.object_id AND i.is_primary_key = 1
         ORDER BY ic.key_ordinal),
        -- Fall back to first column
        (SELECT TOP 1 c.name 
         FROM sys.columns c 
         WHERE c.object_id = t.object_id 
         ORDER BY c.column_id)
    ),
    (SELECT SUM(p.rows) 
     FROM sys.partitions p 
     WHERE p.object_id = t.object_id AND p.index_id IN (0, 1))
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0;

-- Build column lists (first 10 columns to keep output manageable)
UPDATE tb
SET ColumnList = col.cols
FROM @Tables tb
CROSS APPLY (
    SELECT STUFF((
        SELECT TOP 10 ', ' + QUOTENAME(c.name)
        FROM sys.columns c
        INNER JOIN sys.tables t ON c.object_id = t.object_id
        INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = tb.SchemaName AND t.name = tb.TableName
        ORDER BY c.column_id
        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS cols
) col;

-- ============================================================================
-- Sample Data Retrieval
-- ============================================================================
DECLARE @tSchema NVARCHAR(128);
DECLARE @tTable NVARCHAR(128);
DECLARE @tOrderCol NVARCHAR(128);
DECLARE @tRowCount BIGINT;
DECLARE @tColList NVARCHAR(MAX);
DECLARE @dynSql NVARCHAR(MAX);

DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT SchemaName, TableName, OrderColumn, RowCount, ColumnList
FROM @Tables
WHERE RowCount > 0
ORDER BY SchemaName, TableName;

OPEN table_cursor;
FETCH NEXT FROM table_cursor INTO @tSchema, @tTable, @tOrderCol, @tRowCount, @tColList;

WHILE @@FETCH_STATUS = 0
BEGIN
    PRINT '--- ' + @tSchema + '.' + @tTable + ' (' + CAST(@tRowCount AS VARCHAR(20)) + ' rows) ---';
    PRINT '';
    
    -- First N rows
    PRINT 'First ' + CAST(@SampleSize AS VARCHAR(10)) + ' rows:';
    SET @dynSql = N'SELECT TOP (' + CAST(@SampleSize AS NVARCHAR(10)) + ') '
        + @tColList
        + ' FROM ' + QUOTENAME(@tSchema) + '.' + QUOTENAME(@tTable)
        + ' ORDER BY ' + QUOTENAME(@tOrderCol) + ' ASC';
    
    BEGIN TRY
        EXEC sp_executesql @dynSql;
    END TRY
    BEGIN CATCH
        PRINT 'Error retrieving first rows: ' + ERROR_MESSAGE();
    END CATCH
    
    -- Last N rows
    PRINT '';
    PRINT 'Last ' + CAST(@SampleSize AS VARCHAR(10)) + ' rows:';
    SET @dynSql = N'SELECT TOP (' + CAST(@SampleSize AS NVARCHAR(10)) + ') '
        + @tColList
        + ' FROM ' + QUOTENAME(@tSchema) + '.' + QUOTENAME(@tTable)
        + ' ORDER BY ' + QUOTENAME(@tOrderCol) + ' DESC';
    
    BEGIN TRY
        EXEC sp_executesql @dynSql;
    END TRY
    BEGIN CATCH
        PRINT 'Error retrieving last rows: ' + ERROR_MESSAGE();
    END CATCH
    
    PRINT '';
    
    FETCH NEXT FROM table_cursor INTO @tSchema, @tTable, @tOrderCol, @tRowCount, @tColList;
END

CLOSE table_cursor;
DEALLOCATE table_cursor;

-- ============================================================================
-- Data Type Distribution Summary
-- ============================================================================
PRINT '================================================================';
PRINT ' Column Data Type Summary';
PRINT '================================================================';
PRINT '';

SELECT 
    s.name + '.' + t.name AS [Table],
    c.name AS [Column],
    ty.name + CASE 
        WHEN ty.name IN ('varchar', 'nvarchar', 'char', 'nchar') 
            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS VARCHAR(10)) END + ')'
        WHEN ty.name IN ('decimal', 'numeric') 
            THEN '(' + CAST(c.precision AS VARCHAR(5)) + ',' + CAST(c.scale AS VARCHAR(5)) + ')'
        ELSE ''
    END AS [Data Type],
    CASE c.is_nullable WHEN 1 THEN 'Yes' ELSE 'No' END AS [Nullable],
    CASE c.is_identity WHEN 1 THEN 'Yes' ELSE 'No' END AS [Identity]
FROM sys.columns c
INNER JOIN sys.tables t ON c.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name, c.column_id;

-- ============================================================================
-- Empty Tables Check
-- ============================================================================
PRINT '';
PRINT '--- Empty Tables (may indicate migration issues) ---';

SELECT 
    SchemaName + '.' + TableName AS [Table],
    'WARNING - No data' AS [Status]
FROM @Tables
WHERE RowCount = 0
ORDER BY SchemaName, TableName;

DECLARE @emptyCount INT = (SELECT COUNT(*) FROM @Tables WHERE RowCount = 0);
IF @emptyCount > 0
    PRINT 'WARNING: ' + CAST(@emptyCount AS VARCHAR(10)) + ' empty table(s) found';
ELSE
    PRINT 'All tables contain data.';

PRINT '';
PRINT '================================================================';
PRINT ' Sample data comparison complete.';
PRINT ' Compare the output above with source Cosmos DB data exports.';
PRINT '================================================================';
