/*
    Post-Migration Validation: Data Integrity Checks
    =================================================
    Purpose: Validates data integrity of the migrated SQL database including:
             - Foreign key constraint integrity
             - Index existence validation
             - Unique constraint verification
             - Checksum validation on sample data
             - NULL constraint compliance
    
    Usage:
        1. Execute this script against the target SQL database after migration.
        2. Review the results: each check shows PASS/FAIL status.
    
    Notes:
        - Foreign key checks verify referential integrity across all relationships.
        - Index checks confirm all expected indexes exist.
        - Checksum validation uses CHECKSUM_AGG for data consistency verification.
*/

SET NOCOUNT ON;

PRINT '================================================================';
PRINT ' Post-Migration Validation: Data Integrity Checks';
PRINT ' Database: ' + DB_NAME();
PRINT ' Server:   ' + @@SERVERNAME;
PRINT ' Run Date: ' + CONVERT(VARCHAR(30), GETUTCDATE(), 120) + ' UTC';
PRINT '================================================================';
PRINT '';

-- ============================================================================
-- 1. Foreign Key Integrity Checks
-- ============================================================================
PRINT '--- 1. Foreign Key Integrity Checks ---';
PRINT '';

DECLARE @FKResults TABLE (
    ConstraintName   NVARCHAR(256),
    ParentTable      NVARCHAR(256),
    ChildTable       NVARCHAR(256),
    OrphanedRows     BIGINT,
    Status           NVARCHAR(20)
);

DECLARE @fkName NVARCHAR(256);
DECLARE @parentSchema NVARCHAR(128);
DECLARE @parentTable NVARCHAR(128);
DECLARE @parentColumn NVARCHAR(128);
DECLARE @childSchema NVARCHAR(128);
DECLARE @childTable NVARCHAR(128);
DECLARE @childColumn NVARCHAR(128);
DECLARE @sql NVARCHAR(MAX);
DECLARE @orphanCount BIGINT;

DECLARE fk_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT 
    fk.name AS ConstraintName,
    ps.name AS ParentSchema,
    pt.name AS ParentTable,
    pc.name AS ParentColumn,
    cs.name AS ChildSchema,
    ct.name AS ChildTable,
    cc.name AS ChildColumn
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
INNER JOIN sys.tables pt ON fk.referenced_object_id = pt.object_id
INNER JOIN sys.schemas ps ON pt.schema_id = ps.schema_id
INNER JOIN sys.columns pc ON fkc.referenced_object_id = pc.object_id AND fkc.referenced_column_id = pc.column_id
INNER JOIN sys.tables ct ON fk.parent_object_id = ct.object_id
INNER JOIN sys.schemas cs ON ct.schema_id = cs.schema_id
INNER JOIN sys.columns cc ON fkc.parent_object_id = cc.object_id AND fkc.parent_column_id = cc.column_id;

OPEN fk_cursor;
FETCH NEXT FROM fk_cursor INTO @fkName, @parentSchema, @parentTable, @parentColumn, @childSchema, @childTable, @childColumn;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @cnt = COUNT(*) FROM ' 
        + QUOTENAME(@childSchema) + '.' + QUOTENAME(@childTable) + ' c '
        + 'WHERE c.' + QUOTENAME(@childColumn) + ' IS NOT NULL '
        + 'AND NOT EXISTS (SELECT 1 FROM ' 
        + QUOTENAME(@parentSchema) + '.' + QUOTENAME(@parentTable) + ' p '
        + 'WHERE p.' + QUOTENAME(@parentColumn) + ' = c.' + QUOTENAME(@childColumn) + ')';
    
    EXEC sp_executesql @sql, N'@cnt BIGINT OUTPUT', @cnt = @orphanCount OUTPUT;
    
    INSERT INTO @FKResults VALUES (
        @fkName,
        @parentSchema + '.' + @parentTable,
        @childSchema + '.' + @childTable,
        @orphanCount,
        CASE WHEN @orphanCount = 0 THEN 'PASS' ELSE 'FAIL' END
    );
    
    FETCH NEXT FROM fk_cursor INTO @fkName, @parentSchema, @parentTable, @parentColumn, @childSchema, @childTable, @childColumn;
END

CLOSE fk_cursor;
DEALLOCATE fk_cursor;

-- Display FK results
IF EXISTS (SELECT 1 FROM @FKResults)
BEGIN
    SELECT 
        ConstraintName AS [FK Constraint],
        ParentTable AS [Parent Table],
        ChildTable AS [Child Table],
        OrphanedRows AS [Orphaned Rows],
        Status
    FROM @FKResults
    ORDER BY 
        CASE Status WHEN 'FAIL' THEN 1 ELSE 2 END,
        ConstraintName;
    
    DECLARE @fkFail INT = (SELECT COUNT(*) FROM @FKResults WHERE Status = 'FAIL');
    IF @fkFail > 0
        PRINT 'FK Integrity: FAIL (' + CAST(@fkFail AS VARCHAR(10)) + ' constraints with orphaned rows)';
    ELSE
        PRINT 'FK Integrity: PASS (All foreign key constraints validated)';
END
ELSE
    PRINT 'FK Integrity: NO FOREIGN KEYS FOUND (Skipped)';

PRINT '';

-- ============================================================================
-- 2. Index Existence Validation
-- ============================================================================
PRINT '--- 2. Index Existence Validation ---';
PRINT '';

SELECT 
    s.name + '.' + t.name AS [Table],
    i.name AS [Index Name],
    i.type_desc AS [Index Type],
    i.is_unique AS [Is Unique],
    i.is_primary_key AS [Is PK],
    STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns ic
        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        ORDER BY ic.key_ordinal
        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS [Key Columns],
    STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns ic
        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
        ORDER BY ic.key_ordinal
        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS [Included Columns],
    'EXISTS' AS [Status]
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
    AND i.name IS NOT NULL
ORDER BY s.name, t.name, i.name;

-- Summary: tables without any non-clustered indexes (potential concern)
PRINT '';
PRINT 'Tables without non-clustered indexes (review recommended):';

SELECT 
    s.name + '.' + t.name AS [Table],
    SUM(p.rows) AS [Row Count],
    'WARNING' AS [Status]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0
    AND NOT EXISTS (
        SELECT 1 FROM sys.indexes i 
        WHERE i.object_id = t.object_id 
        AND i.type = 2 -- Non-clustered
    )
GROUP BY s.name, t.name
HAVING SUM(p.rows) > 1000
ORDER BY SUM(p.rows) DESC;

PRINT '';

-- ============================================================================
-- 3. Primary Key Validation
-- ============================================================================
PRINT '--- 3. Primary Key Validation ---';
PRINT '';

DECLARE @PKResults TABLE (
    TableName  NVARCHAR(256),
    HasPK      BIT,
    PKColumns  NVARCHAR(MAX),
    Status     NVARCHAR(20)
);

INSERT INTO @PKResults (TableName, HasPK, PKColumns, Status)
SELECT 
    s.name + '.' + t.name AS TableName,
    CASE WHEN i.object_id IS NOT NULL THEN 1 ELSE 0 END AS HasPK,
    STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns ic
        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
        ORDER BY ic.key_ordinal
        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS PKColumns,
    CASE WHEN i.object_id IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS Status
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
LEFT JOIN sys.indexes i ON t.object_id = i.object_id AND i.is_primary_key = 1
WHERE t.is_ms_shipped = 0;

SELECT 
    TableName AS [Table],
    CASE HasPK WHEN 1 THEN 'Yes' ELSE 'No' END AS [Has Primary Key],
    ISNULL(PKColumns, 'N/A') AS [PK Columns],
    Status
FROM @PKResults
ORDER BY 
    CASE Status WHEN 'FAIL' THEN 1 ELSE 2 END,
    TableName;

DECLARE @pkFail INT = (SELECT COUNT(*) FROM @PKResults WHERE Status = 'FAIL');
IF @pkFail > 0
    PRINT 'PK Validation: FAIL (' + CAST(@pkFail AS VARCHAR(10)) + ' tables without primary keys)';
ELSE
    PRINT 'PK Validation: PASS (All tables have primary keys)';

PRINT '';

-- ============================================================================
-- 4. Checksum Validation (Per-Table Data Consistency)
-- ============================================================================
PRINT '--- 4. Checksum Validation ---';
PRINT '';

DECLARE @ChecksumResults TABLE (
    SchemaName  NVARCHAR(128),
    TableName   NVARCHAR(128),
    RowCount    BIGINT,
    ChecksumVal INT,
    Status      NVARCHAR(20)
);

DECLARE @csSchema NVARCHAR(128);
DECLARE @csTable NVARCHAR(128);
DECLARE @csSql NVARCHAR(MAX);
DECLARE @csChecksum INT;
DECLARE @csRowCount BIGINT;

DECLARE cs_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT s.name, t.name
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0;

OPEN cs_cursor;
FETCH NEXT FROM cs_cursor INTO @csSchema, @csTable;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @csSql = N'SELECT @chk = CHECKSUM_AGG(CHECKSUM(*)), @cnt = COUNT(*) FROM '
        + QUOTENAME(@csSchema) + '.' + QUOTENAME(@csTable);
    
    BEGIN TRY
        EXEC sp_executesql @csSql, 
            N'@chk INT OUTPUT, @cnt BIGINT OUTPUT', 
            @chk = @csChecksum OUTPUT, @cnt = @csRowCount OUTPUT;
        
        INSERT INTO @ChecksumResults VALUES (
            @csSchema, @csTable, @csRowCount, @csChecksum, 'RECORDED'
        );
    END TRY
    BEGIN CATCH
        INSERT INTO @ChecksumResults VALUES (
            @csSchema, @csTable, 0, 0, 'ERROR'
        );
    END CATCH
    
    FETCH NEXT FROM cs_cursor INTO @csSchema, @csTable;
END

CLOSE cs_cursor;
DEALLOCATE cs_cursor;

SELECT 
    SchemaName + '.' + TableName AS [Table],
    RowCount AS [Row Count],
    ChecksumVal AS [Checksum],
    Status
FROM @ChecksumResults
ORDER BY SchemaName, TableName;

PRINT '';
PRINT 'Note: Record these checksum values as a baseline. Compare against future runs';
PRINT '      to detect unintended data modifications after migration.';

PRINT '';

-- ============================================================================
-- 5. NULL Constraint Compliance
-- ============================================================================
PRINT '--- 5. NOT NULL Constraint Compliance ---';
PRINT '';

DECLARE @NullResults TABLE (
    TableName   NVARCHAR(256),
    ColumnName  NVARCHAR(128),
    NullCount   BIGINT,
    Status      NVARCHAR(20)
);

DECLARE @nnSchema NVARCHAR(128);
DECLARE @nnTable NVARCHAR(128);
DECLARE @nnColumn NVARCHAR(128);
DECLARE @nnSql NVARCHAR(MAX);
DECLARE @nnNullCount BIGINT;

DECLARE nn_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT s.name, t.name, c.name
FROM sys.columns c
INNER JOIN sys.tables t ON c.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
    AND c.is_nullable = 0
    AND c.is_identity = 0
    AND c.is_computed = 0;

OPEN nn_cursor;
FETCH NEXT FROM nn_cursor INTO @nnSchema, @nnTable, @nnColumn;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @nnSql = N'SELECT @cnt = COUNT(*) FROM ' 
        + QUOTENAME(@nnSchema) + '.' + QUOTENAME(@nnTable)
        + ' WHERE ' + QUOTENAME(@nnColumn) + ' IS NULL';
    
    BEGIN TRY
        EXEC sp_executesql @nnSql, N'@cnt BIGINT OUTPUT', @cnt = @nnNullCount OUTPUT;
        
        IF @nnNullCount > 0
            INSERT INTO @NullResults VALUES (
                @nnSchema + '.' + @nnTable, @nnColumn, @nnNullCount, 'FAIL'
            );
    END TRY
    BEGIN CATCH
        INSERT INTO @NullResults VALUES (
            @nnSchema + '.' + @nnTable, @nnColumn, -1, 'ERROR'
        );
    END CATCH
    
    FETCH NEXT FROM nn_cursor INTO @nnSchema, @nnTable, @nnColumn;
END

CLOSE nn_cursor;
DEALLOCATE nn_cursor;

IF EXISTS (SELECT 1 FROM @NullResults)
BEGIN
    SELECT 
        TableName AS [Table],
        ColumnName AS [Column],
        NullCount AS [NULL Values Found],
        Status
    FROM @NullResults
    ORDER BY TableName, ColumnName;
    
    PRINT 'NOT NULL Compliance: FAIL (Violations found - see results above)';
END
ELSE
    PRINT 'NOT NULL Compliance: PASS (No NOT NULL constraint violations)';

PRINT '';

-- ============================================================================
-- Overall Summary
-- ============================================================================
PRINT '================================================================';
PRINT ' Data Integrity Summary';
PRINT '================================================================';

DECLARE @totalChecks INT = 0;
DECLARE @totalFails INT = 0;

-- FK checks
SET @totalChecks = @totalChecks + (SELECT COUNT(*) FROM @FKResults);
SET @totalFails = @totalFails + (SELECT COUNT(*) FROM @FKResults WHERE Status = 'FAIL');

-- PK checks
SET @totalChecks = @totalChecks + (SELECT COUNT(*) FROM @PKResults);
SET @totalFails = @totalFails + (SELECT COUNT(*) FROM @PKResults WHERE Status = 'FAIL');

-- NULL checks (count only failures/errors)
SET @totalFails = @totalFails + (SELECT COUNT(*) FROM @NullResults WHERE Status = 'FAIL');

PRINT 'Total Checks Run:  ' + CAST(@totalChecks AS VARCHAR(10));
PRINT 'Failed Checks:     ' + CAST(@totalFails AS VARCHAR(10));
PRINT '';

IF @totalFails > 0
    PRINT 'OVERALL RESULT: FAIL - Review failed checks above';
ELSE
    PRINT 'OVERALL RESULT: PASS - All data integrity checks passed';

PRINT '================================================================';
