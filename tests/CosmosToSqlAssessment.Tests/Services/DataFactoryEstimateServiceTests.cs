using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

public class DataFactoryEstimateServiceTests : TestBase
{
    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<DataFactoryEstimateService>();

        // Act
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithValidInput_ShouldReturnEstimate()
    {
        // Arrange
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.Should().NotBeNull();
        result.TotalDataSizeGB.Should().BeGreaterThan(0);
        result.EstimatedDuration.Should().BeGreaterThan(TimeSpan.Zero);
        result.EstimatedCostUSD.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EstimateMigrationAsync_ShouldCalculateDIURecommendations()
    {
        // Arrange
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.RecommendedDIUs.Should().BeGreaterThan(0);
        result.RecommendedParallelCopies.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EstimateMigrationAsync_ShouldGeneratePipelineEstimates()
    {
        // Arrange
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.PipelineEstimates.Should().NotBeNull();
        result.PipelineEstimates.Should().NotBeEmpty();
    }
}
