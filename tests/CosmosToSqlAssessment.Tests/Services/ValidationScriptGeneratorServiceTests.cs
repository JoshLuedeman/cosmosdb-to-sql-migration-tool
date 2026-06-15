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
            result.GeneratedFiles.Should().HaveCount(1);
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

            var sql = await File.ReadAllTextAsync(result.GeneratedFiles.Single());
            sql.Should().Contain("-- (no migrated tables in assessment)");
            sql.Should().Contain("-- (no migrated tables to validate)");
            sql.Should().NotContain("{{");
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
}
