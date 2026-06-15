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
            result.GeneratedFiles.Should().HaveCount(4);
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

            result.GeneratedFiles.Should().HaveCount(4);
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

            result.GeneratedFiles.Should().HaveCount(4);
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

            result.GeneratedFiles.Should().HaveCount(4);
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
