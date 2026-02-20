using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Tests for SqlProjectIntegrationService – verifies end-to-end project generation orchestration
/// </summary>
public class SqlProjectIntegrationServiceTests : TestBase, IDisposable
{
    private readonly SqlProjectIntegrationService _service;
    private readonly SqlDatabaseProjectService _sqlDatabaseProjectService;
    private readonly Mock<ILogger<SqlProjectIntegrationService>> _mockLogger;
    private readonly string _tempDirectory;

    public SqlProjectIntegrationServiceTests()
    {
        _mockLogger = CreateMockLogger<SqlProjectIntegrationService>();
        var sqlDbLogger = CreateMockLogger<SqlDatabaseProjectService>();
        _sqlDatabaseProjectService = new SqlDatabaseProjectService(MockConfiguration.Object, sqlDbLogger.Object);
        _service = new SqlProjectIntegrationService(_sqlDatabaseProjectService, MockConfiguration.Object, _mockLogger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"SqlIntegrationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<SqlProjectIntegrationService>();
        var sqlLogger = CreateMockLogger<SqlDatabaseProjectService>();
        var sqlDbService = new SqlDatabaseProjectService(MockConfiguration.Object, sqlLogger.Object);

        // Act
        var service = new SqlProjectIntegrationService(sqlDbService, MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithValidAssessment_ShouldReturnSuccessResult()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "TestIntegrationProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Project.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithValidAssessment_ShouldSetProjectName()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "MyCustomProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Project!.ProjectName.Should().Be("MyCustomProject");
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithEmptyProjectName_ShouldUseDatabaseName()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = string.Empty, // Intentionally empty to trigger auto-generation
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Project!.ProjectName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_ShouldPopulateDuration()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "DurationTestProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        result.StartTime.Should().BeBefore(result.EndTime);
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_ShouldPreserveOptions()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "OptionsTestProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false,
            TargetCompatibilityLevel = "160"
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Options.Should().NotBeNull();
        result.Options.TargetCompatibilityLevel.Should().Be("160");
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_NullAssessment_ShouldThrow()
    {
        // Arrange
        var options = new SqlProjectOptions
        {
            ProjectName = "NullTest",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.GenerateSqlProjectAsync(null!, options));
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_AssessmentWithNoDatabaseMappings_ShouldThrow()
    {
        // Arrange
        var assessment = new SqlMigrationAssessment
        {
            RecommendedPlatform = "Azure SQL Database",
            RecommendedTier = "General Purpose"
            // Intentionally no DatabaseMappings
        };
        var options = new SqlProjectOptions
        {
            ProjectName = "NoDatabaseMappings",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GenerateSqlProjectAsync(assessment, options));
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_AssessmentWithNoContainerMappings_ShouldThrow()
    {
        // Arrange
        var assessment = new SqlMigrationAssessment
        {
            RecommendedPlatform = "Azure SQL Database",
            RecommendedTier = "General Purpose",
            DatabaseMappings = new List<DatabaseMapping>
            {
                new DatabaseMapping
                {
                    SourceDatabase = "TestDatabase",
                    TargetDatabase = "TestDatabase_SQL"
                    // No ContainerMappings
                }
            }
        };
        var options = new SqlProjectOptions
        {
            ProjectName = "NoContainerMappings",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GenerateSqlProjectAsync(assessment, options));
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithDeploymentArtifacts_ShouldGeneratePublishProfile()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "DeployArtifactsProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = true,
            GenerateAzureDevOpsPipeline = false,
            GeneratePowerShellDeployment = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        // Verify the project output path exists
        Directory.Exists(result.Project!.OutputPath).Should().BeTrue();
        // Publish profile should have been created
        var profileFile = Path.Combine(result.Project.OutputPath, $"{result.Project.ProjectName}.publish.xml");
        File.Exists(profileFile).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithAzureDevOpsPipeline_ShouldGeneratePipelineFile()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "PipelineProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = true,
            GenerateAzureDevOpsPipeline = true,
            GeneratePowerShellDeployment = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        var pipelineFile = Path.Combine(result.Project!.OutputPath, "azure-pipelines.yml");
        File.Exists(pipelineFile).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithPowerShellDeployment_ShouldGenerateScript()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "PowerShellProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = true,
            GenerateAzureDevOpsPipeline = false,
            GeneratePowerShellDeployment = true,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        var psScript = Path.Combine(result.Project!.OutputPath, "Deploy.ps1");
        File.Exists(psScript).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithDocumentation_ShouldGenerateReadme()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "DocumentationProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = true
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        var readmeFile = Path.Combine(result.Project!.OutputPath, "README.md");
        File.Exists(readmeFile).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithDocumentation_ShouldGenerateDeploymentGuide()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "GuideProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = true
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        var guideFile = Path.Combine(result.Project!.OutputPath, "DeploymentGuide.md");
        File.Exists(guideFile).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithDocumentation_ShouldGenerateSchemaDoc()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "SchemaDocProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = true
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        var schemaFile = Path.Combine(result.Project!.OutputPath, "SchemaDocumentation.md");
        File.Exists(schemaFile).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithComplexTransformations_ShouldAddWarnings()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        // Add complex transformations to trigger warning logic
        for (int i = 0; i < 3; i++)
        {
            assessment.TransformationRules.Add(new TransformationRule
            {
                RuleName = $"SplitTransformation{i}",
                TransformationType = "Split",
                Logic = "Split arrays",
                AffectedTables = new List<string> { $"Table{i}" },
                SourcePattern = "array[]",
                TargetPattern = "child_table"
            });
        }
        var options = new SqlProjectOptions
        {
            ProjectName = "WarningsProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Project!.Metadata.GenerationWarnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithLargeRowCount_ShouldAddHighPriorityWarning()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        // Set a very large estimated row count to trigger the critical warning
        assessment.DatabaseMappings.First().ContainerMappings.First().EstimatedRowCount = 200_000_000;

        var options = new SqlProjectOptions
        {
            ProjectName = "LargeRowCountProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Project!.Metadata.GenerationWarnings.Should().Contain(w => w.Contains("CRITICAL") || w.Contains("large"));
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithCancellationToken_CanBeCancelled()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "CancelledProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GenerateSqlProjectAsync(assessment, options, cts.Token));
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithDefaultOutputPath_ShouldUseConfiguredPath()
    {
        // Arrange
        MockConfiguration.Setup(c => c["SqlProject:OutputPath"]).Returns(_tempDirectory);
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        var options = new SqlProjectOptions
        {
            ProjectName = "DefaultPathProject",
            OutputPath = string.Empty, // Empty - should use configured path
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Project!.OutputPath.Should().Contain(_tempDirectory);
    }

    [Fact]
    public async Task GenerateSqlProjectAsync_WithStoredProcedures_ShouldAddManualInterventionNote()
    {
        // Arrange
        var assessment = TestDataFactory.CreateSampleSqlAssessment();
        assessment.TransformationRules.Add(new TransformationRule
        {
            RuleName = "FlattenAddresses",
            TransformationType = "Flatten",
            Logic = "Flatten address object",
            AffectedTables = new List<string> { "Users" },
            SourcePattern = "address.*",
            TargetPattern = "address_*"
        });
        var options = new SqlProjectOptions
        {
            ProjectName = "ManualInterventionProject",
            OutputPath = _tempDirectory,
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false
        };

        // Act
        var result = await _service.GenerateSqlProjectAsync(assessment, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Project!.Metadata.ManualInterventionRequired.Should().NotBeEmpty();
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
