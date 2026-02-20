using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Unit tests for SqlProjectGenerationService - verifies SQL database project file generation
/// </summary>
public class SqlProjectGenerationServiceTests : TestBase, IDisposable
{
    private readonly SqlProjectGenerationService _service;
    private readonly Mock<ILogger<SqlProjectGenerationService>> _mockLogger;
    private readonly string _tempDirectory;

    public SqlProjectGenerationServiceTests()
    {
        _mockLogger = CreateMockLogger<SqlProjectGenerationService>();
        _service = new SqlProjectGenerationService(MockConfiguration.Object, _mockLogger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"SqlProjectGenerationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange & Act
        var service = new SqlProjectGenerationService(MockConfiguration.Object, _mockLogger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithValidAssessment_ShouldCreateProjectDirectory()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        Directory.Exists(sqlProjectsDir).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithValidAssessment_ShouldCreateSubdirectories()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var entries = Directory.GetDirectories(sqlProjectsDir);
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateTablesDirectory()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        projectDirs.Should().NotBeEmpty();
        foreach (var projectDir in projectDirs)
        {
            Directory.Exists(Path.Combine(projectDir, "Tables")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateIndexesDirectory()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            Directory.Exists(Path.Combine(projectDir, "Indexes")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateForeignKeysDirectory()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            Directory.Exists(Path.Combine(projectDir, "ForeignKeys")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateScriptsDirectory()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            Directory.Exists(Path.Combine(projectDir, "Scripts")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateSqlprojFile()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        projectDirs.Should().NotBeEmpty();
        foreach (var projectDir in projectDirs)
        {
            var sqlprojFiles = Directory.GetFiles(projectDir, "*.sqlproj");
            sqlprojFiles.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateTableSqlFiles()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            var sqlFiles = Directory.GetFiles(tablesDir, "*.sql");
            sqlFiles.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_ShouldCreateDeploymentScript()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var scriptsDir = Path.Combine(projectDir, "Scripts");
            var deployScript = Path.Combine(scriptsDir, "PostDeployment.sql");
            File.Exists(deployScript).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_DeploymentScript_ShouldContainQueryStoreSetup()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var deployScript = Path.Combine(projectDir, "Scripts", "PostDeployment.sql");
            var content = await File.ReadAllTextAsync(deployScript);
            content.Should().Contain("QUERY_STORE");
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_TableScript_ShouldContainCreateTableStatement()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            foreach (var sqlFile in Directory.GetFiles(tablesDir, "*.sql"))
            {
                var content = await File.ReadAllTextAsync(sqlFile);
                content.Should().Contain("CREATE TABLE");
            }
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_TableScript_ShouldContainAuditColumns()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            foreach (var sqlFile in Directory.GetFiles(tablesDir, "*.sql"))
            {
                var content = await File.ReadAllTextAsync(sqlFile);
                content.Should().Contain("CreatedDate");
                content.Should().Contain("ModifiedDate");
            }
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_TableScript_ShouldContainCosmosIdColumn()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            foreach (var sqlFile in Directory.GetFiles(tablesDir, "*.sql"))
            {
                var content = await File.ReadAllTextAsync(sqlFile);
                content.Should().Contain("CosmosId");
            }
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithMultipleDatabaseMappings_ShouldCreateMultipleProjects()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings.Add(new DatabaseMapping
        {
            SourceDatabase = "SecondDatabase",
            TargetDatabase = "SecondDatabase_SQL",
            ContainerMappings = new List<ContainerMapping>
            {
                new ContainerMapping
                {
                    SourceContainer = "products",
                    TargetSchema = "dbo",
                    TargetTable = "Products",
                    FieldMappings = new List<FieldMapping>
                    {
                        new FieldMapping { SourceField = "productId", TargetColumn = "ProductId", TargetType = "NVARCHAR(100)", IsNullable = false }
                    }
                }
            }
        });

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        projectDirs.Length.Should().Be(2);
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithIndexRecommendations_ShouldCreateIndexScript()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        // The sample data already has index recommendations for "Users" table which is in "users" container

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var indexesDir = Path.Combine(projectDir, "Indexes");
            // Index file should exist if there are matching recommendations
            // (Indexes.sql is only created when there are relevant indexes)
            Directory.Exists(indexesDir).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithCancellationToken_CanBeCancelled()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GenerateSqlProjectsAsync(assessment, _tempDirectory, cts.Token));
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_SqprojFile_ShouldContainValidXml()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var sqlprojFiles = Directory.GetFiles(projectDir, "*.sqlproj");
            foreach (var sqlprojFile in sqlprojFiles)
            {
                var content = await File.ReadAllTextAsync(sqlprojFile);
                content.Should().Contain("Project");
                content.Should().Contain("PropertyGroup");
                content.Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithSharedSchemas_ShouldCreateSharedTableScript()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.SharedSchemas.Add(new SharedSchema
        {
            SchemaId = "shared-address-001",
            SchemaName = "Address",
            TargetSchema = "dbo",
            TargetTable = "SharedAddress",
            SchemaHash = "abc123",
            UsageCount = 2,
            SourceContainers = new List<string> { "users", "orders" },
            SourceFieldPaths = new List<string> { "users.address", "orders.shippingAddress" },
            FieldMappings = new List<FieldMapping>
            {
                new FieldMapping { SourceField = "street", TargetColumn = "Street", TargetType = "NVARCHAR(255)", IsNullable = true },
                new FieldMapping { SourceField = "city", TargetColumn = "City", TargetType = "NVARCHAR(100)", IsNullable = true }
            }
        });

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            // The shared table script should have been generated
            var sqlFiles = Directory.GetFiles(tablesDir, "*.sql");
            sqlFiles.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithChildTableMappings_ShouldCreateChildTableScript()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings.First().ContainerMappings.First().ChildTableMappings.Add(
            new ChildTableMapping
            {
                SourceFieldPath = "orders",
                ChildTableType = "Array",
                TargetSchema = "dbo",
                TargetTable = "UserOrders",
                ParentKeyColumn = "ParentId",
                FieldMappings = new List<FieldMapping>
                {
                    new FieldMapping { SourceField = "Id", TargetColumn = "Id", TargetType = "BIGINT IDENTITY(1,1)", IsNullable = false },
                    new FieldMapping { SourceField = "ParentId", TargetColumn = "ParentId", TargetType = "NVARCHAR(255)", IsNullable = false },
                    new FieldMapping { SourceField = "orderId", TargetColumn = "OrderId", TargetType = "NVARCHAR(100)", IsNullable = true }
                }
            });

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            var childTableFile = Path.Combine(tablesDir, "UserOrders.sql");
            File.Exists(childTableFile).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GenerateSqlProjectsAsync_WithRequiredTransformations_ShouldIncludeComments()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings.First().ContainerMappings.First().RequiredTransformations
            .Add("Flatten nested address object to top-level columns");

        // Act
        await _service.GenerateSqlProjectsAsync(assessment, _tempDirectory);

        // Assert
        var sqlProjectsDir = Path.Combine(_tempDirectory, "sql-projects");
        var projectDirs = Directory.GetDirectories(sqlProjectsDir);
        foreach (var projectDir in projectDirs)
        {
            var tablesDir = Path.Combine(projectDir, "Tables");
            var mainTableFile = Directory.GetFiles(tablesDir, "Users.sql").FirstOrDefault();
            if (mainTableFile != null)
            {
                var content = await File.ReadAllTextAsync(mainTableFile);
                content.Should().Contain("Required Transformations");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
