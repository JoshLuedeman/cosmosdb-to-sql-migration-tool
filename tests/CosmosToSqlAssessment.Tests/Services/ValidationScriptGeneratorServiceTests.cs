using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Unit tests for ValidationScriptGeneratorService — verifies the post-migration
/// validation script generator emits the right blocks from an AssessmentResult.
/// </summary>
public class ValidationScriptGeneratorServiceTests : TestBase
{
    private readonly string _testOutputPath;

    public ValidationScriptGeneratorServiceTests()
    {
        _testOutputPath = Path.Combine(Path.GetTempPath(), "ValidationScriptTests", Guid.NewGuid().ToString());
    }

    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        var logger = CreateMockLogger<ValidationScriptGeneratorService>();
        var service = new ValidationScriptGeneratorService(logger.Object);
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAsync_WithMultipleTables_CreatesRowCountScriptWithExpectedBlocks()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTables();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.Should().NotBeNull();
            result.GeneratedFiles.Should().HaveCount(6);
            var rowCountScript = result.GeneratedFiles.Single(f => f.EndsWith("01-RowCountValidation.sql"));
            File.Exists(rowCountScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(rowCountScript);

            // Core scaffolding is preserved.
            sql.Should().Contain("CREATE TABLE dbo.ValidationResults");
            sql.Should().Contain("CREATE TABLE dbo.ValidationExpectedCounts");
            sql.Should().Contain("CREATE OR ALTER PROCEDURE dbo.sp_ValidationRowCountCheck");
            sql.Should().NotContain("{{ExpectedCountsSeed}}", "placeholder must be replaced");
            sql.Should().NotContain("{{TableRowsBlock}}", "placeholder must be replaced");

            // Container mapping with an estimated row count -> MERGE seed.
            sql.Should().Contain("MERGE dbo.ValidationExpectedCounts");
            sql.Should().Contain("N'dbo', N'Users', N'Assessment', CAST(500000 AS BIGINT)");

            // Container mapping with no estimated row count -> INFO baseline-gap row.
            sql.Should().Contain("N'BaselineGap'");
            sql.Should().Contain("N'sales', N'Orders'");

            // Child table mapping -> baseline gap (no count).
            sql.Should().Contain("N'dbo', N'OrderLines'");

            // EXEC call emitted for every table (parent + child).
            sql.Should().Contain("@TableName        = N'Users'");
            sql.Should().Contain("@TableName        = N'Orders'");
            sql.Should().Contain("@TableName        = N'OrderLines'");

            // Custom schema is honored, not hard-coded to dbo.
            sql.Should().Contain("@SchemaName       = N'sales'");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_WithEmptyAssessment_StillCreatesParseableScript()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            var rowCount = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("01-RowCountValidation.sql")));
            rowCount.Should().Contain("-- (no migrated tables in assessment)");
            rowCount.Should().Contain("-- (no migrated tables to validate)");
            rowCount.Should().NotContain("{{");

            var checksum = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("02-DataIntegrityChecks.sql")));
            checksum.Should().Contain("-- (no migrated tables to checksum)");
            checksum.Should().NotContain("{{");

            var sample = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("03-SampleDataComparison.sql")));
            sample.Should().Contain("-- (no migrated tables to sample)");
            sample.Should().NotContain("{{");

            var fk = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("06-ForeignKeyValidation.sql")));
            fk.Should().Contain("-- (no foreign key constraints in assessment)");
            fk.Should().Contain("-- (no migrated tables in assessment; FK scope table is empty)");
            fk.Should().NotContain("{{");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_RejectsMaliciousIdentifiers()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                TargetSchema = "dbo",
                                TargetTable = "Users'; DROP TABLE Users; --",
                                EstimatedRowCount = 1
                            }
                        }
                    }
                }
            }
        };

        var act = async () => await service.GenerateAsync(assessment, _testOutputPath);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not safe to inject*");
    }

    [Fact]
    public async Task GenerateAsync_AlsoCreatesChecksumScript_WithExpectedBlocks()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTablesAndFieldMappings();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.GeneratedFiles.Should().HaveCount(6);
            var checksumScript = result.GeneratedFiles.Single(f => f.EndsWith("02-DataIntegrityChecks.sql"));
            File.Exists(checksumScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(checksumScript);

            sql.Should().Contain("CREATE TABLE dbo.ValidationExpectedChecksums");
            sql.Should().Contain("CREATE OR ALTER PROCEDURE dbo.sp_ValidationChecksum");
            sql.Should().NotContain("{{ChecksumChecksBlock}}");

            // Parent table with field mappings -> EXEC with column list.
            sql.Should().Contain("@TableName  = N'Users'");
            sql.Should().Contain("ISNULL(CONVERT(VARCHAR(MAX), [UserId])"); // string column, no style
            sql.Should().Contain("ISNULL(CONVERT(VARCHAR(MAX), [LastLogin], 121)"); // datetime2 -> style 121

            // Partition-key columns appear in ORDER BY.
            sql.Should().Contain("@OrderBy    = N'[UserId]'");

            // Child table -> ParentId prepended.
            sql.Should().Contain("@TableName  = N'OrderLines'");
            sql.Should().Contain("[ParentId]");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task ChecksumScript_ConvertsVarbinaryWithStyle2_AndExcludesFloatFromOrderBy()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                TargetSchema = "dbo",
                                TargetTable = "Blobs",
                                FieldMappings = new List<FieldMapping>
                                {
                                    new() { TargetColumn = "Payload",     TargetType = "VARBINARY(MAX)" },
                                    new() { TargetColumn = "Weight",      TargetType = "FLOAT" },
                                    new() { TargetColumn = "Created",     TargetType = "DATETIME2" }
                                }
                            }
                        }
                    }
                }
            }
        };

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);
            var sql = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("02-DataIntegrityChecks.sql")));

            sql.Should().Contain("ISNULL(CONVERT(VARCHAR(MAX), [Payload], 2)"); // varbinary hex
            sql.Should().Contain("ISNULL(CONVERT(VARCHAR(MAX), [Created], 121)"); // datetime2

            // ORDER BY must NOT contain [Weight] (float type).
            var orderByMatch = System.Text.RegularExpressions.Regex.Match(sql, "@OrderBy    = N'([^']+)'");
            orderByMatch.Success.Should().BeTrue();
            orderByMatch.Groups[1].Value.Should().NotContain("[Weight]");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_CreatesOutputDirectoryIfMissing()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTables();

        try
        {
            Directory.Exists(_testOutputPath).Should().BeFalse();
            var result = await service.GenerateAsync(assessment, _testOutputPath);
            Directory.Exists(result.OutputDirectory).Should().BeTrue();
            result.OutputDirectory.Should().EndWith(Path.Combine("Scripts", "PostMigration"));
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_AlsoCreatesSampleScript_WithFirstAndLastBlocks()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTablesAndFieldMappings();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.GeneratedFiles.Should().HaveCount(6);
            var sampleScript = result.GeneratedFiles.Single(f => f.EndsWith("03-SampleDataComparison.sql"));
            File.Exists(sampleScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(sampleScript);

            sql.Should().Contain("CREATE TABLE dbo.ValidationSampleRows");
            sql.Should().Contain("CREATE TABLE dbo.ValidationExpectedSampleRows");
            sql.Should().Contain("CREATE OR ALTER PROCEDURE dbo.sp_ValidationSampleCapture");
            sql.Should().NotContain("{{SampleCaptureBlock}}");

            // Per-table EXEC calls.
            sql.Should().Contain("@TableName   = N'Users'");
            sql.Should().Contain("@TableName   = N'Orders'");

            // ORDER BY uses partition keys with explicit direction in both arms.
            sql.Should().Contain("@OrderByAsc  = N'[UserId] ASC'");
            sql.Should().Contain("@OrderByDesc = N'[UserId] DESC'");

            // KeyExpr is CONCAT_WS-based over the PK columns. After the
            // second escape pass (inlining into an outer N'...' literal),
            // each '' becomes '''', so the delimiter literal '|' surfaces
            // as ''''|''''.
            sql.Should().Contain("CONCAT_WS(''''|''''");
            sql.Should().Contain("ISNULL(CONVERT(VARCHAR(MAX), [UserId])");

            // Column list quotes all mapped columns.
            sql.Should().Contain("@ColumnList  = N'[UserId], [Email], [LastLogin]'");

            // FULL OUTER JOIN comparison logic exists.
            sql.Should().Contain("FULL OUTER JOIN expected_cur");
            sql.Should().Contain("OPENJSON(ISNULL(j.ActualJson");

            // Child tables emit a skip INFO row, not an EXEC call.
            sql.Should().Contain("N'Child table sample comparison skipped");
            sql.Should().NotContain("@TableName   = N'OrderLines'");

            // Both First and Last positions are referenced.
            sql.Should().Contain("N''First''");
            sql.Should().Contain("N''Last''");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task SampleScript_EmitsSkipInfo_WhenNoFieldMappingsAvailable()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                TargetSchema = "dbo",
                                TargetTable = "Unknown",
                                FieldMappings = new List<FieldMapping>()
                            }
                        }
                    }
                }
            }
        };

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);
            var sql = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("03-SampleDataComparison.sql")));

            sql.Should().Contain("N'ColumnList'");
            sql.Should().Contain("N'No field mappings recorded in assessment");
            sql.Should().NotContain("EXEC dbo.sp_ValidationSampleCapture");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_AlsoCreatesForeignKeyScript_WithExpectedSeedsAndCursor()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithForeignKeys();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.GeneratedFiles.Should().HaveCount(6);
            var fkScript = result.GeneratedFiles.Single(f => f.EndsWith("06-ForeignKeyValidation.sql"));
            File.Exists(fkScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(fkScript);

            sql.Should().Contain("CREATE TABLE dbo.ValidationExpectedForeignKeys");
            sql.Should().Contain("CREATE TABLE dbo.ValidationFkScopeTables");
            sql.Should().Contain("CREATE OR ALTER PROCEDURE dbo.sp_ValidationForeignKeyOrphans");
            sql.Should().NotContain("{{");

            // FK seed picks up schema from ContainerMappings (sales for Orders).
            sql.Should().Contain("N'FK_OrderLines_Orders'");
            sql.Should().Contain("N'dbo', N'OrderLines', N'OrderId'");
            sql.Should().Contain("N'sales', N'Orders', N'Id'");

            // Cascade actions captured in uppercase.
            sql.Should().Contain("N'CASCADE', N'CASCADE'");

            // Scope-table seed enumerates every container + child table.
            sql.Should().Contain("VALUES (N'sales', N'Orders')");
            sql.Should().Contain("VALUES (N'dbo', N'OrderLines')");
            sql.Should().Contain("VALUES (N'dbo', N'Users')");

            // Cursor + scope filter present.
            sql.Should().Contain("DECLARE fk_cursor CURSOR");
            sql.Should().Contain("INSERT @InScopeFks");
            sql.Should().Contain("dbo.ValidationFkScopeTables");

            // Trust remediation hint uses targeted form (not WITH CHECK CHECK CONSTRAINT ALL).
            sql.Should().Contain("WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(d.FkName)");
            sql.Should().NotContain("CHECK CONSTRAINT ALL");

            // NULL-safe AND predicate in orphan proc.
            sql.Should().Contain("IS NOT NULL', N' AND '");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task ForeignKeyScript_RejectsMaliciousFkName()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                ForeignKeyConstraints = new List<ForeignKeyConstraint>
                {
                    new()
                    {
                        ConstraintName = "FK'; DROP TABLE Users; --",
                        ChildTable = "OrderLines",
                        ChildColumn = "OrderId",
                        ParentTable = "Orders",
                        ParentColumn = "Id"
                    }
                }
            }
        };

        var act = async () => await service.GenerateAsync(assessment, _testOutputPath);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not safe to inject*");
    }

    [Fact]
    public async Task GenerateAsync_AlsoCreatesIndexScript_WithSeedAndDiscovery()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithIndexes();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.GeneratedFiles.Should().HaveCount(6);
            var indexScript = result.GeneratedFiles.Single(f => f.EndsWith("05-IndexValidation.sql"));
            File.Exists(indexScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(indexScript);

            sql.Should().Contain("CREATE TABLE dbo.ValidationExpectedIndexes");
            sql.Should().NotContain("{{");

            // Seed picks up schema from ContainerMappings (sales for Orders).
            sql.Should().Contain("N'sales', N'Orders', N'IX_Orders_CustomerId'");
            sql.Should().Contain("N'NONCLUSTERED', 0, N'CustomerId', N''");

            // Default-schema fallback for tables not in ContainerMappings.
            sql.Should().Contain("N'dbo', N'Users', N'IX_Users_Email'");

            // Unique gets normalized to NONCLUSTERED + IsUnique=1.
            sql.Should().Contain("N'NONCLUSTERED', 1, N'Email', N''");

            // Columnstore is normalized to NONCLUSTERED COLUMNSTORE.
            sql.Should().Contain("N'CSI_Orders'");
            sql.Should().Contain("N'NONCLUSTERED COLUMNSTORE'");

            // Comparison query uses STRING_AGG with key_ordinal / index_column_id ordering.
            sql.Should().Contain("STRING_AGG(CAST(kc.ColumnName AS NVARCHAR(MAX)), N', ')");
            sql.Should().Contain("WITHIN GROUP (ORDER BY kc.key_ordinal)");
            sql.Should().Contain("WITHIN GROUP (ORDER BY ic2.index_column_id)");

            // Case-insensitive collation on column-list comparison.
            sql.Should().Contain("COLLATE Latin1_General_CI_AS");

            // Discovery filters HEAP, hypothetical, and primary keys.
            sql.Should().Contain("type_desc      <> N'HEAP'");
            sql.Should().Contain("is_hypothetical = 0");
            sql.Should().Contain("is_primary_key  = 0");

            // Disabled-index remediation hint.
            sql.Should().Contain("ALTER INDEX");
            sql.Should().Contain("REBUILD");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_WithNoIndexRecommendations_StillEmitsIndexScript()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTables(); // no IndexRecommendations

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            var indexScript = result.GeneratedFiles.Single(f => f.EndsWith("05-IndexValidation.sql"));
            var sql = await File.ReadAllTextAsync(indexScript);

            // Seed comment, no MERGE.
            sql.Should().Contain("-- (no index recommendations in assessment)");
            sql.Should().NotContain("MERGE dbo.ValidationExpectedIndexes");

            // But scope-table block still produced (re-used from FK script).
            sql.Should().Contain("INSERT dbo.ValidationFkScopeTables");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexScript_RejectsMaliciousIndexName()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                IndexRecommendations = new List<IndexRecommendation>
                {
                    new()
                    {
                        IndexName = "IX'; DROP TABLE Users; --",
                        TableName = "Users",
                        IndexType = "NonClustered",
                        Columns = new List<string> { "Email" }
                    }
                }
            }
        };

        var act = async () => await service.GenerateAsync(assessment, _testOutputPath);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not safe to inject*");
    }

    [Fact]
    public async Task GenerateAsync_AlsoCreatesPerformanceBaselineScript_WithExpectedSections()
    {
        var service = new ValidationScriptGeneratorService(CreateMockLogger<ValidationScriptGeneratorService>().Object);
        var assessment = BuildAssessmentWithThreeTables();

        try
        {
            var result = await service.GenerateAsync(assessment, _testOutputPath);

            result.GeneratedFiles.Should().HaveCount(6);
            var perfScript = result.GeneratedFiles.Single(f => f.EndsWith("04-PerformanceBaseline.sql"));
            File.Exists(perfScript).Should().BeTrue();

            var sql = await File.ReadAllTextAsync(perfScript);

            sql.Should().NotContain("{{");

            // Pre-flight permission check.
            sql.Should().Contain("HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW DATABASE STATE')");

            // All six observational categories present.
            sql.Should().Contain("N'TableSize'");
            sql.Should().Contain("N'IndexCount'");
            sql.Should().Contain("N'PlanCacheWarmth'");
            sql.Should().Contain("N'WaitStats'");
            sql.Should().Contain("N'TopCpu'");
            sql.Should().Contain("N'TopDuration'");
            sql.Should().Contain("N'MissingIndex'");
            sql.Should().Contain("N'MissingIndexDmvAge'");
            sql.Should().Contain("N'StatsFreshness'");
            sql.Should().Contain("N'Summary'");

            // DMV reads.
            sql.Should().Contain("sys.dm_db_partition_stats");
            sql.Should().Contain("sys.dm_db_wait_stats");
            sql.Should().Contain("sys.dm_exec_query_stats");
            sql.Should().Contain("sys.dm_db_missing_index_details");
            sql.Should().Contain("sys.dm_os_sys_info");

            // Tunable thresholds.
            sql.Should().Contain(":setvar MissingIndexWarnThreshold");
            sql.Should().Contain(":setvar MaxScopeTablesForPlanCache");

            // Scope-table seed for mapped tables.
            sql.Should().Contain("INSERT dbo.ValidationFkScopeTables");
            sql.Should().Contain("VALUES (N'dbo', N'Users')");
            sql.Should().Contain("VALUES (N'sales', N'Orders')");

            // StatsFreshness filters auto/user-created stats only.
            sql.Should().Contain("s.auto_created = 1 OR s.user_created = 1");

            // Plan-cache scope truncation INFO + parameterized cap.
            sql.Should().Contain("N'ScopeTruncated'");
            sql.Should().Contain("@MaxScopeForCache");
        }
        finally
        {
            if (Directory.Exists(_testOutputPath))
                Directory.Delete(_testOutputPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("Clustered", "CLUSTERED", false)]
    [InlineData("NonClustered", "NONCLUSTERED", false)]
    [InlineData("Unique", "NONCLUSTERED", true)]
    [InlineData("ColumnStore", "NONCLUSTERED COLUMNSTORE", false)]
    [InlineData("", "NONCLUSTERED", false)]
    public void NormalizeIndexType_MapsAssessmentTypesToSeedValues(string input, string expectedType, bool expectedUnique)
    {
        var (type, isUnique) = ValidationScriptGeneratorService.NormalizeIndexType(input);
        type.Should().Be(expectedType);
        isUnique.Should().Be(expectedUnique);
    }

    private static AssessmentResult BuildAssessmentWithIndexes()
    {
        return new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                TargetSchema = "sales",
                                TargetTable = "Orders",
                                EstimatedRowCount = 1000
                            }
                            // Users is intentionally NOT in ContainerMappings -> tests dbo fallback.
                        }
                    }
                },
                IndexRecommendations = new List<IndexRecommendation>
                {
                    new()
                    {
                        IndexName = "IX_Orders_CustomerId",
                        TableName = "Orders",
                        IndexType = "NonClustered",
                        Columns = new List<string> { "CustomerId" }
                    },
                    new()
                    {
                        IndexName = "IX_Users_Email",
                        TableName = "Users",
                        IndexType = "Unique",
                        Columns = new List<string> { "Email" }
                    },
                    new()
                    {
                        IndexName = "CSI_Orders",
                        TableName = "Orders",
                        IndexType = "ColumnStore",
                        Columns = new List<string> { "OrderDate", "Amount" }
                    }
                }
            }
        };
    }

    private static AssessmentResult BuildAssessmentWithForeignKeys()
    {
        return new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                TargetSchema = "dbo",
                                TargetTable = "Users",
                                EstimatedRowCount = 100
                            },
                            new()
                            {
                                TargetSchema = "sales",
                                TargetTable = "Orders",
                                EstimatedRowCount = 200,
                                ChildTableMappings = new List<ChildTableMapping>
                                {
                                    new()
                                    {
                                        TargetSchema = "dbo",
                                        TargetTable = "OrderLines",
                                        ChildTableType = "Array",
                                        ParentKeyColumn = "OrderId"
                                    }
                                }
                            }
                        }
                    }
                },
                ForeignKeyConstraints = new List<ForeignKeyConstraint>
                {
                    new()
                    {
                        ConstraintName = "FK_OrderLines_Orders",
                        ChildTable = "OrderLines",
                        ChildColumn = "OrderId",
                        ParentTable = "Orders",
                        ParentColumn = "Id",
                        OnDeleteAction = "CASCADE",
                        OnUpdateAction = "CASCADE"
                    }
                }
            }
        };
    }

    private static AssessmentResult BuildAssessmentWithThreeTables()
    {
        return new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        SourceDatabase = "Cosmos",
                        TargetDatabase = "TargetDb",
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                SourceContainer = "users",
                                TargetSchema = "dbo",
                                TargetTable = "Users",
                                EstimatedRowCount = 500_000
                            },
                            new()
                            {
                                SourceContainer = "orders",
                                TargetSchema = "sales",
                                TargetTable = "Orders",
                                EstimatedRowCount = 0,
                                ChildTableMappings = new List<ChildTableMapping>
                                {
                                    new()
                                    {
                                        ChildTableType = "Array",
                                        TargetSchema = "dbo",
                                        TargetTable = "OrderLines"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static AssessmentResult BuildAssessmentWithThreeTablesAndFieldMappings()
    {
        return new AssessmentResult
        {
            SqlAssessment = new SqlMigrationAssessment
            {
                DatabaseMappings = new List<DatabaseMapping>
                {
                    new()
                    {
                        SourceDatabase = "Cosmos",
                        TargetDatabase = "TargetDb",
                        ContainerMappings = new List<ContainerMapping>
                        {
                            new()
                            {
                                SourceContainer = "users",
                                TargetSchema = "dbo",
                                TargetTable = "Users",
                                EstimatedRowCount = 500_000,
                                FieldMappings = new List<FieldMapping>
                                {
                                    new() { TargetColumn = "UserId",    TargetType = "NVARCHAR(100)", IsPartitionKey = true },
                                    new() { TargetColumn = "Email",     TargetType = "NVARCHAR(255)" },
                                    new() { TargetColumn = "LastLogin", TargetType = "DATETIME2(3)" }
                                }
                            },
                            new()
                            {
                                SourceContainer = "orders",
                                TargetSchema = "sales",
                                TargetTable = "Orders",
                                EstimatedRowCount = 0,
                                FieldMappings = new List<FieldMapping>
                                {
                                    new() { TargetColumn = "OrderId", TargetType = "NVARCHAR(100)", IsPartitionKey = true },
                                    new() { TargetColumn = "Amount",  TargetType = "DECIMAL(18,2)" }
                                },
                                ChildTableMappings = new List<ChildTableMapping>
                                {
                                    new()
                                    {
                                        ChildTableType = "Array",
                                        TargetSchema = "dbo",
                                        TargetTable = "OrderLines",
                                        ParentKeyColumn = "ParentId",
                                        FieldMappings = new List<FieldMapping>
                                        {
                                            new() { TargetColumn = "ParentId", TargetType = "NVARCHAR(100)" },
                                            new() { TargetColumn = "Sku",      TargetType = "NVARCHAR(50)" },
                                            new() { TargetColumn = "Quantity", TargetType = "INT" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}
