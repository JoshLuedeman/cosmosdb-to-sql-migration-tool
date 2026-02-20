/*
    Post-Migration Validation: Performance Baseline
    ================================================
    Purpose: Establishes performance baselines for the migrated SQL database.
             Captures key metrics that can be compared over time to detect
             performance regressions or optimization opportunities.
    
    Usage:
        1. Execute this script immediately after migration completes.
        2. Save the results as the baseline reference.
        3. Re-run periodically and compare against the baseline.
    
    Notes:
        - Run during representative workload periods for accurate baselines.
        - Some DMV statistics reset on server restart; note the uptime.
        - Index usage stats help identify unused or missing indexes.
*/

SET NOCOUNT ON;

PRINT '================================================================';
PRINT ' Post-Migration Validation: Performance Baseline';
PRINT ' Database: ' + DB_NAME();
PRINT ' Server:   ' + @@SERVERNAME;
PRINT ' Run Date: ' + CONVERT(VARCHAR(30), GETUTCDATE(), 120) + ' UTC';
PRINT '================================================================';
PRINT '';

-- ============================================================================
-- 1. Database Size and Space Usage
-- ============================================================================
PRINT '--- 1. Database Size and Space Usage ---';
PRINT '';

SELECT 
    DB_NAME() AS [Database],
    SUM(CASE WHEN type = 0 THEN size END) * 8 / 1024 AS [Data Size (MB)],
    SUM(CASE WHEN type = 1 THEN size END) * 8 / 1024 AS [Log Size (MB)],
    SUM(size) * 8 / 1024 AS [Total Size (MB)]
FROM sys.database_files;

-- Per-table space usage
SELECT 
    s.name + '.' + t.name AS [Table],
    SUM(p.rows) AS [Row Count],
    SUM(a.total_pages) * 8 / 1024 AS [Total Space (MB)],
    SUM(a.used_pages) * 8 / 1024 AS [Used Space (MB)],
    (SUM(a.total_pages) - SUM(a.used_pages)) * 8 / 1024 AS [Unused Space (MB)]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE t.is_ms_shipped = 0
GROUP BY s.name, t.name
ORDER BY SUM(a.total_pages) DESC;

PRINT '';

-- ============================================================================
-- 2. Index Usage Statistics
-- ============================================================================
PRINT '--- 2. Index Usage Statistics ---';
PRINT '';

SELECT 
    s.name + '.' + t.name AS [Table],
    i.name AS [Index Name],
    i.type_desc AS [Index Type],
    ISNULL(ius.user_seeks, 0) AS [User Seeks],
    ISNULL(ius.user_scans, 0) AS [User Scans],
    ISNULL(ius.user_lookups, 0) AS [User Lookups],
    ISNULL(ius.user_updates, 0) AS [User Updates],
    ISNULL(ius.last_user_seek, '') AS [Last Seek],
    ISNULL(ius.last_user_scan, '') AS [Last Scan]
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
LEFT JOIN sys.dm_db_index_usage_stats ius 
    ON i.object_id = ius.object_id 
    AND i.index_id = ius.index_id 
    AND ius.database_id = DB_ID()
WHERE t.is_ms_shipped = 0
    AND i.name IS NOT NULL
ORDER BY s.name, t.name, i.index_id;

PRINT '';

-- ============================================================================
-- 3. Index Physical Statistics (Fragmentation)
-- ============================================================================
PRINT '--- 3. Index Fragmentation ---';
PRINT '';

SELECT 
    s.name + '.' + t.name AS [Table],
    i.name AS [Index Name],
    ips.avg_fragmentation_in_percent AS [Fragmentation %],
    ips.page_count AS [Page Count],
    ips.avg_page_space_used_in_percent AS [Page Fullness %],
    CASE 
        WHEN ips.avg_fragmentation_in_percent < 10 THEN 'GOOD'
        WHEN ips.avg_fragmentation_in_percent < 30 THEN 'REORGANIZE'
        ELSE 'REBUILD'
    END AS [Recommendation]
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
INNER JOIN sys.tables t ON ips.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
WHERE t.is_ms_shipped = 0
    AND ips.page_count > 100 -- Only report on indexes with meaningful size
    AND i.name IS NOT NULL
ORDER BY ips.avg_fragmentation_in_percent DESC;

PRINT '';

-- ============================================================================
-- 4. Table Scan Baseline Queries
-- ============================================================================
PRINT '--- 4. Table Scan Performance Baseline ---';
PRINT '';
PRINT 'Running COUNT(*) on each table to establish scan baseline...';
PRINT '';

DECLARE @PerfResults TABLE (
    TableName       NVARCHAR(256),
    RowCount        BIGINT,
    ScanDurationMs  INT
);

DECLARE @pSchema NVARCHAR(128);
DECLARE @pTable NVARCHAR(128);
DECLARE @pSql NVARCHAR(MAX);
DECLARE @pCount BIGINT;
DECLARE @startTime DATETIME2;
DECLARE @endTime DATETIME2;

DECLARE perf_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT s.name, t.name
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;

OPEN perf_cursor;
FETCH NEXT FROM perf_cursor INTO @pSchema, @pTable;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @startTime = SYSDATETIME();
    SET @pSql = N'SELECT @cnt = COUNT(*) FROM ' + QUOTENAME(@pSchema) + '.' + QUOTENAME(@pTable);
    
    BEGIN TRY
        EXEC sp_executesql @pSql, N'@cnt BIGINT OUTPUT', @cnt = @pCount OUTPUT;
        SET @endTime = SYSDATETIME();
        
        INSERT INTO @PerfResults VALUES (
            @pSchema + '.' + @pTable,
            @pCount,
            DATEDIFF(MILLISECOND, @startTime, @endTime)
        );
    END TRY
    BEGIN CATCH
        INSERT INTO @PerfResults VALUES (
            @pSchema + '.' + @pTable, 0, -1
        );
    END CATCH
    
    FETCH NEXT FROM perf_cursor INTO @pSchema, @pTable;
END

CLOSE perf_cursor;
DEALLOCATE perf_cursor;

SELECT 
    TableName AS [Table],
    RowCount AS [Row Count],
    CASE 
        WHEN ScanDurationMs >= 0 THEN CAST(ScanDurationMs AS VARCHAR(10)) + ' ms'
        ELSE 'ERROR'
    END AS [Scan Duration],
    CASE 
        WHEN ScanDurationMs < 0 THEN 'ERROR'
        WHEN ScanDurationMs < 1000 THEN 'GOOD'
        WHEN ScanDurationMs < 5000 THEN 'ACCEPTABLE'
        ELSE 'SLOW'
    END AS [Rating]
FROM @PerfResults
ORDER BY ScanDurationMs DESC;

PRINT '';

-- ============================================================================
-- 5. Wait Statistics Snapshot
-- ============================================================================
PRINT '--- 5. Current Wait Statistics (Top 10) ---';
PRINT '';

SELECT TOP 10
    wait_type AS [Wait Type],
    waiting_tasks_count AS [Wait Count],
    wait_time_ms AS [Total Wait (ms)],
    signal_wait_time_ms AS [Signal Wait (ms)],
    wait_time_ms - signal_wait_time_ms AS [Resource Wait (ms)],
    CASE 
        WHEN waiting_tasks_count > 0 
        THEN wait_time_ms / waiting_tasks_count 
        ELSE 0 
    END AS [Avg Wait (ms)]
FROM sys.dm_os_wait_stats
WHERE wait_type NOT IN (
    'CLR_SEMAPHORE', 'LAZYWRITER_SLEEP', 'RESOURCE_QUEUE',
    'SLEEP_TASK', 'SLEEP_SYSTEMTASK', 'SQLTRACE_BUFFER_FLUSH',
    'WAITFOR', 'LOGMGR_QUEUE', 'CHECKPOINT_QUEUE',
    'REQUEST_FOR_DEADLOCK_SEARCH', 'XE_TIMER_EVENT',
    'BROKER_TO_FLUSH', 'BROKER_TASK_STOP', 'CLR_MANUAL_EVENT',
    'CLR_AUTO_EVENT', 'DISPATCHER_QUEUE_SEMAPHORE',
    'FT_IFTS_SCHEDULER_IDLE_WAIT', 'XE_DISPATCHER_WAIT',
    'XE_DISPATCHER_JOIN', 'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
    'ONDEMAND_TASK_QUEUE', 'BROKER_EVENTHANDLER',
    'SLEEP_BPOOL_FLUSH', 'DIRTY_PAGE_POLL',
    'HADR_FILESTREAM_IOMGR_IOCOMPLETION'
)
AND waiting_tasks_count > 0
ORDER BY wait_time_ms DESC;

PRINT '';

-- ============================================================================
-- 6. Missing Index Suggestions
-- ============================================================================
PRINT '--- 6. Missing Index Suggestions ---';
PRINT '';

SELECT TOP 20
    CONVERT(DECIMAL(18,2), migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans)) AS [Improvement Measure],
    mid.statement AS [Table],
    ISNULL(mid.equality_columns, '') AS [Equality Columns],
    ISNULL(mid.inequality_columns, '') AS [Inequality Columns],
    ISNULL(mid.included_columns, '') AS [Included Columns],
    migs.user_seeks AS [User Seeks],
    migs.user_scans AS [User Scans]
FROM sys.dm_db_missing_index_groups mig
INNER JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle = migs.group_handle
INNER JOIN sys.dm_db_missing_index_details mid ON mig.index_handle = mid.index_handle
WHERE mid.database_id = DB_ID()
ORDER BY migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans) DESC;

PRINT '';

-- ============================================================================
-- Summary
-- ============================================================================
PRINT '================================================================';
PRINT ' Performance Baseline Summary';
PRINT '================================================================';

DECLARE @dbSizeMB INT = (SELECT SUM(size) * 8 / 1024 FROM sys.database_files);
DECLARE @tableCount INT = (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0);
DECLARE @indexCount INT = (SELECT COUNT(*) FROM sys.indexes i INNER JOIN sys.tables t ON i.object_id = t.object_id WHERE t.is_ms_shipped = 0 AND i.name IS NOT NULL);
DECLARE @totalRows BIGINT = (SELECT SUM(RowCount) FROM @PerfResults);
DECLARE @slowTables INT = (SELECT COUNT(*) FROM @PerfResults WHERE ScanDurationMs >= 5000);
DECLARE @fragIndexes INT;

SELECT @fragIndexes = COUNT(*)
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
INNER JOIN sys.tables t ON ips.object_id = t.object_id
WHERE t.is_ms_shipped = 0
    AND ips.page_count > 100
    AND ips.avg_fragmentation_in_percent > 30;

PRINT 'Database Size:       ' + CAST(@dbSizeMB AS VARCHAR(20)) + ' MB';
PRINT 'Total Tables:        ' + CAST(@tableCount AS VARCHAR(10));
PRINT 'Total Indexes:       ' + CAST(@indexCount AS VARCHAR(10));
PRINT 'Total Rows:          ' + CAST(@totalRows AS VARCHAR(20));
PRINT 'Slow Tables (>5s):   ' + CAST(@slowTables AS VARCHAR(10));
PRINT 'Fragmented Indexes:  ' + CAST(ISNULL(@fragIndexes, 0) AS VARCHAR(10));
PRINT '';
PRINT 'Save these results as your post-migration performance baseline.';
PRINT 'Re-run this script periodically to track performance changes.';
PRINT '================================================================';
