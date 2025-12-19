using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Integration;

public class WorkflowIntegrationTests : TestBase
{
    [Fact]
    public void AllServices_CanBeInstantiatedTogether()
    {
        // Arrange & Act
        var cosmosService = new CosmosDbAnalysisService(MockConfiguration.Object, CreateMockLogger<CosmosDbAnalysisService>().Object);
        var sqlService = new SqlMigrationAssessmentService(MockConfiguration.Object, CreateMockLogger<SqlMigrationAssessmentService>().Object);
        var dataFactoryService = new DataFactoryEstimateService(MockConfiguration.Object, CreateMockLogger<DataFactoryEstimateService>().Object);
        var reportService = new ReportGenerationService(MockConfiguration.Object, CreateMockLogger<ReportGenerationService>().Object);

        // Assert
        cosmosService.Should().NotBeNull();
        sqlService.Should().NotBeNull();
        dataFactoryService.Should().NotBeNull();
        reportService.Should().NotBeNull();
    }

    [Fact]
    public async Task FullWorkflow_CosmosToSqlToDataFactory_ShouldExecute()
    {
        // Arrange
        var sqlService = new SqlMigrationAssessmentService(MockConfiguration.Object, CreateMockLogger<SqlMigrationAssessmentService>().Object);
        var dataFactoryService = new DataFactoryEstimateService(MockConfiguration.Object, CreateMockLogger<DataFactoryEstimateService>().Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var sqlAssessment = await sqlService.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");
        var dataFactoryEstimate = await dataFactoryService.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        sqlAssessment.Should().NotBeNull();
        dataFactoryEstimate.Should().NotBeNull();
        dataFactoryEstimate.TotalDataSizeGB.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CompleteAssessment_ShouldProduceAllComponents()
    {
        // Arrange
        var sqlService = new SqlMigrationAssessmentService(MockConfiguration.Object, CreateMockLogger<SqlMigrationAssessmentService>().Object);
        var dataFactoryService = new DataFactoryEstimateService(MockConfiguration.Object, CreateMockLogger<DataFactoryEstimateService>().Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var sqlAssessment = await sqlService.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");
        var dataFactoryEstimate = await dataFactoryService.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        var assessmentResult = new AssessmentResult
        {
            DatabaseName = "TestDatabase",
            AssessmentDate = DateTime.UtcNow,
            CosmosAnalysis = cosmosAnalysis,
            SqlAssessment = sqlAssessment,
            DataFactoryEstimate = dataFactoryEstimate
        };

        // Assert
        assessmentResult.DatabaseName.Should().Be("TestDatabase");
        assessmentResult.CosmosAnalysis.Should().NotBeNull();
        assessmentResult.SqlAssessment.Should().NotBeNull();
        assessmentResult.DataFactoryEstimate.Should().NotBeNull();
    }

    [Fact]
    public async Task ReportGeneration_AfterAssessment_ShouldProduceFiles()
    {
        // Arrange
        var reportService = new ReportGenerationService(MockConfiguration.Object, CreateMockLogger<ReportGenerationService>().Object);
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        var outputDir = Path.Combine(Path.GetTempPath(), $"CosmosToSqlTests_{Guid.NewGuid()}");

        try
        {
            // Act
            var (excelPaths, wordPath, analysisFolderPath) = await reportService.GenerateAssessmentReportAsync(assessmentResult, outputDir);

            // Assert
            excelPaths.Should().NotBeEmpty();
            File.Exists(wordPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }
}
