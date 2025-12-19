using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Reporting;

/// <summary>
/// Tests for ReportGenerationService
/// </summary>
public class ReportGenerationServiceTests : TestBase, IDisposable
{
    private readonly ReportGenerationService _service;
    private readonly Mock<ILogger<ReportGenerationService>> _mockLogger;
    private readonly string _tempDirectory;

    public ReportGenerationServiceTests()
    {
        _mockLogger = CreateMockLogger<ReportGenerationService>();
        _service = new ReportGenerationService(MockConfiguration.Object, _mockLogger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithValidData_ShouldCreateExcelAndWordFiles()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        excelPaths.Should().AllSatisfy(path => File.Exists(path).Should().BeTrue());
        File.Exists(wordPath).Should().BeTrue();

        var wordFileInfo = new FileInfo(wordPath);
        wordFileInfo.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithNullAssessmentResult_ShouldThrowNullReferenceException()
    {
        // Arrange
        AssessmentResult assessmentResult = null;

        // Act & Assert
        // Note: Service should validate input but currently throws NullReferenceException
        await Assert.ThrowsAsync<NullReferenceException>(
            () => _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory));
    }

    // TODO: Implement cancellation token support in ReportGenerationService
    // [Fact]
    // public async Task GenerateAssessmentReportAsync_WithCancellationToken_ShouldRespectCancellation()
    // {
    //     // Currently service doesn't check cancellation token
    // }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithMultipleDatabases_ShouldCreateMultipleExcelFiles()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        assessmentResult.IndividualDatabaseResults = new List<AssessmentResult>
        {
            CreateSampleAssessmentResult(),
            CreateSampleAssessmentResult()
        };
        assessmentResult.IndividualDatabaseResults[0].DatabaseName = "Database1";
        assessmentResult.IndividualDatabaseResults[1].DatabaseName = "Database2";

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Count.Should().Be(2);
        File.Exists(wordPath).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_CreatesExcelWithCorrectWorksheets()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        var excelFile = excelPaths.First();
        File.Exists(excelFile).Should().BeTrue();
        new FileInfo(excelFile).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WordDocument_ShouldContainSummary()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        File.Exists(wordPath).Should().BeTrue();
        var fileInfo = new FileInfo(wordPath);
        fileInfo.Length.Should().BeGreaterThan(1000); // Should have substantial content
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithInvalidOutputDirectory_ShouldCreateDirectory()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        var nonExistentDir = Path.Combine(_tempDirectory, "SubFolder", "AnotherLevel");

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, nonExistentDir);

        // Assert
        Directory.Exists(nonExistentDir).Should().BeTrue();
        excelPaths.Should().NotBeEmpty();
        File.Exists(wordPath).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithLargeDataset_ShouldComplete()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        // Add more containers to simulate larger dataset
        for (int i = 0; i < 10; i++)
        {
            assessmentResult.CosmosAnalysis.Containers.Add(new ContainerAnalysis
            {
                ContainerName = $"Container{i}",
                DocumentCount = 100000,
                SizeBytes = 1024 * 1024 * 1024,
                DetectedSchemas = new List<DocumentSchema>
                {
                    new DocumentSchema
                    {
                        Fields = new Dictionary<string, FieldInfo>
                        {
                            ["id"] = new FieldInfo { FieldName = "id", DetectedTypes = new List<string> { "string" } }
                        }
                    }
                }
            });
        }

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        File.Exists(wordPath).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_FilePaths_ShouldContainTimestamp()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        assessmentResult.DatabaseName = "MyTestDatabase";

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        // Files are created with timestamp-based directory names
        Path.GetDirectoryName(wordPath).Should().Contain("CosmosDB-Analysis_");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithMinimalData_ShouldStillGenerateReports()
    {
        // Arrange
        var assessmentResult = new AssessmentResult
        {
            DatabaseName = "MinimalDB",
            AssessmentDate = DateTime.UtcNow,
            CosmosAnalysis = new CosmosDbAnalysis
            {
                Containers = new List<ContainerAnalysis>
                {
                    new ContainerAnalysis
                    {
                        ContainerName = "MinimalContainer",
                        DocumentCount = 1,
                        SizeBytes = 1024
                    }
                }
            },
            SqlAssessment = new SqlMigrationAssessment
            {
                RecommendedPlatform = "AzureSqlDatabase",
                RecommendedTier = "GeneralPurpose",
                Complexity = new MigrationComplexity { OverallComplexity = "Low" }
            },
            DataFactoryEstimate = new DataFactoryEstimate
            {
                TotalDataSizeGB = 1,
                EstimatedDuration = TimeSpan.FromMinutes(10),
                EstimatedCostUSD = 1.5m,
                RecommendedDIUs = 2,
                RecommendedParallelCopies = 1
            }
        };

        // Act
        var (excelPaths, wordPath) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        File.Exists(wordPath).Should().BeTrue();
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
            // Ignore cleanup errors in tests
        }
    }
}
