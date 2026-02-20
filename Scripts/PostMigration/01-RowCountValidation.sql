/*
    Post-Migration Validation: Row Count Validation
    ================================================
    Purpose: Validates that all migrated tables contain the expected number of rows
             by comparing expected counts (from Cosmos DB source) against actual SQL row counts.
    
    Usage:
        1. Update the @ExpectedCounts table variable with source container document counts
           from the Cosmos DB assessment report.
        2. Execute this script against the target SQL database.
        3. Review the results: each table shows PASS/FAIL status.
    
    Notes:
        - Expected counts should be populated from the migration assessment report.
        - A tolerance percentage can be configured to allow minor discrepancies.
        - Tables not listed in @ExpectedCounts are reported as warnings.
*/

SET NOCOUNT ON;

-- ============================================================================
-- Configuration
-- ============================================================================
DECLARE @TolerancePercent DECIMAL(5,2) = 0.00; -- Allowable variance (0 = exact match)

-- ============================================================================
-- Expected row counts from Cosmos DB source
-- Update these values from your migration assessment report
-- ============================================================================
DECLARE @ExpectedCounts TABLE (
    SchemaName NVARCHAR(128),
    TableName  NVARCHAR(128),
    ExpectedRowCount BIGINT
);

-- Example entries (replace with actual values from your assessment):
-- INSERT INTO @ExpectedCounts VALUES ('dbo', 'Users', 150000);
-- INSERT INTO @ExpectedCounts VALUES ('dbo', 'Orders', 500000);
-- INSERT INTO @ExpectedCounts VALUES ('dbo', 'Products', 25000);
-- INSERT INTO @ExpectedCounts VALUES ('dbo', 'OrderItems', 1200000);

-- ============================================================================
-- Validation Results
-- ============================================================================
PRINT '================================================================';
PRINT ' Post-Migration Validation: Row Count Comparison';
PRINT ' Database: ' + DB_NAME();
PRINT ' Server:   ' + @@SERVERNAME;
PRINT ' Run Date: ' + CONVERT(VARCHAR(30), GETUTCDATE(), 120) + ' UTC';
PRINT '================================================================';
PRINT '';

-- Create results table
DECLARE @Results TABLE (
    SchemaName       NVARCHAR(128),
    TableName        NVARCHAR(128),
    ExpectedCount    BIGINT NULL,
    ActualCount      BIGINT,
    Difference       BIGINT NULL,
    VariancePercent  DECIMAL(10,4) NULL,
    Status           NVARCHAR(20)
);

-- Get actual row counts for all user tables
INSERT INTO @Results (SchemaName, TableName, ActualCount)
SELECT 
    s.name AS SchemaName,
    t.name AS TableName,
    SUM(p.rows) AS ActualCount
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0
GROUP BY s.name, t.name;

-- Update with expected counts and calculate differences
UPDATE r
SET 
    r.ExpectedCount   = e.ExpectedRowCount,
    r.Difference      = r.ActualCount - e.ExpectedRowCount,
    r.VariancePercent = CASE 
        WHEN e.ExpectedRowCount = 0 AND r.ActualCount = 0 THEN 0
        WHEN e.ExpectedRowCount = 0 THEN 100.00
        ELSE CAST(ABS(r.ActualCount - e.ExpectedRowCount) AS DECIMAL(18,4)) 
             / CAST(e.ExpectedRowCount AS DECIMAL(18,4)) * 100
    END,
    r.Status = CASE
        WHEN r.ActualCount = e.ExpectedRowCount THEN 'PASS'
        WHEN e.ExpectedRowCount = 0 AND r.ActualCount = 0 THEN 'PASS'
        WHEN e.ExpectedRowCount > 0 
             AND CAST(ABS(r.ActualCount - e.ExpectedRowCount) AS DECIMAL(18,4)) 
                 / CAST(e.ExpectedRowCount AS DECIMAL(18,4)) * 100 <= @TolerancePercent 
             THEN 'PASS'
        ELSE 'FAIL'
    END
FROM @Results r
INNER JOIN @ExpectedCounts e 
    ON r.SchemaName = e.SchemaName AND r.TableName = e.TableName;

-- Mark tables without expected counts
UPDATE @Results
SET Status = 'WARNING'
WHERE ExpectedCount IS NULL;

-- ============================================================================
-- Output Results
-- ============================================================================
PRINT '--- Row Count Validation Results ---';
PRINT '';

-- Detailed results
SELECT 
    SchemaName + '.' + TableName AS [Table],
    ISNULL(CAST(ExpectedCount AS NVARCHAR(20)), 'N/A') AS [Expected],
    CAST(ActualCount AS NVARCHAR(20)) AS [Actual],
    ISNULL(CAST(Difference AS NVARCHAR(20)), 'N/A') AS [Difference],
    ISNULL(CAST(VariancePercent AS NVARCHAR(20)) + '%', 'N/A') AS [Variance],
    Status
FROM @Results
ORDER BY 
    CASE Status WHEN 'FAIL' THEN 1 WHEN 'WARNING' THEN 2 WHEN 'PASS' THEN 3 END,
    SchemaName, TableName;

-- Summary
PRINT '';
PRINT '--- Summary ---';

SELECT 
    Status,
    COUNT(*) AS TableCount
FROM @Results
GROUP BY Status
ORDER BY 
    CASE Status WHEN 'FAIL' THEN 1 WHEN 'WARNING' THEN 2 WHEN 'PASS' THEN 3 END;

-- Overall pass/fail
DECLARE @FailCount INT = (SELECT COUNT(*) FROM @Results WHERE Status = 'FAIL');
DECLARE @WarnCount INT = (SELECT COUNT(*) FROM @Results WHERE Status = 'WARNING');
DECLARE @TotalCount INT = (SELECT COUNT(*) FROM @Results);

PRINT '';
IF @FailCount > 0
    PRINT 'OVERALL RESULT: FAIL (' + CAST(@FailCount AS VARCHAR(10)) + ' of ' + CAST(@TotalCount AS VARCHAR(10)) + ' tables failed validation)';
ELSE IF @WarnCount > 0
    PRINT 'OVERALL RESULT: PASS WITH WARNINGS (' + CAST(@WarnCount AS VARCHAR(10)) + ' tables without expected counts)';
ELSE
    PRINT 'OVERALL RESULT: PASS (All ' + CAST(@TotalCount AS VARCHAR(10)) + ' tables validated successfully)';

PRINT '';
PRINT 'Tolerance: ' + CAST(@TolerancePercent AS VARCHAR(10)) + '%';
PRINT '================================================================';
