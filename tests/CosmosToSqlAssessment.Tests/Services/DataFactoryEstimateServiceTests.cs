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

    [Fact]
    public async Task EstimateMigrationAsync_ShouldGeneratePrerequisitesAndRecommendations()
    {
        // Arrange
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.Prerequisites.Should().NotBeNull();
        result.Recommendations.Should().NotBeNull();
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithVerySmallDataset_ShouldRecommendLowDIUs()
    {
        // Arrange – < 1 GB dataset → 2 DIUs
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        foreach (var c in cosmosAnalysis.Containers) c.SizeBytes = 512 * 1024 * 1024; // 512 MB

        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.RecommendedDIUs.Should().BeGreaterThan(0);
        result.RecommendedDIUs.Should().BeLessThanOrEqualTo(4); // small data → 2 DIUs
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithLargeDataset_ShouldRecommendHighDIUs()
    {
        // Arrange – > 1000 GB dataset → 32 DIUs
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        foreach (var c in cosmosAnalysis.Containers) c.SizeBytes = 600L * 1024 * 1024 * 1024; // 600 GB each

        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.RecommendedDIUs.Should().BeGreaterThanOrEqualTo(16);
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithManyContainers_ShouldIncreaseParallelCopies()
    {
        // Arrange – > 10 containers → parallelCopies at higher setting
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        for (int i = 0; i < 12; i++)
        {
            cosmosAnalysis.Containers.Add(new ContainerAnalysis
            {
                ContainerName = $"container{i}",
                DocumentCount = 10000,
                SizeBytes = 100 * 1024 * 1024,
                PartitionKey = "/id",
                ProvisionedRUs = 400,
                DetectedSchemas = new List<DocumentSchema>(),
                IndexingPolicy = new ContainerIndexingPolicy(),
                Performance = new ContainerPerformanceMetrics()
            });
        }

        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        result.RecommendedParallelCopies.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithContainerHavingTransformations_ShouldSetComplexity()
    {
        // Arrange – container mapping has > 5 required transformations → "High" complexity
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
        var containerMapping = sqlAssessment.DatabaseMappings.First().ContainerMappings.First();
        for (int i = 0; i < 6; i++)
        {
            containerMapping.RequiredTransformations.Add($"Transformation {i}");
        }

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        var pipelineEst = result.PipelineEstimates.First();
        pipelineEst.TransformationComplexity.Should().Be("High");
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithMediumTransformations_ShouldSetMediumComplexity()
    {
        // Arrange – 3-5 transformations → "Medium"
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
        var containerMapping = sqlAssessment.DatabaseMappings.First().ContainerMappings.First();
        for (int i = 0; i < 4; i++)
        {
            containerMapping.RequiredTransformations.Add($"Transformation {i}");
        }

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        var pipelineEst = result.PipelineEstimates.First();
        pipelineEst.TransformationComplexity.Should().Be("Medium");
    }

    [Fact]
    public async Task EstimateMigrationAsync_WithNoTransformation_ShouldSetNoneComplexity()
    {
        // Arrange – no required transformations → "None"
        var logger = CreateMockLogger<DataFactoryEstimateService>();
        var service = new DataFactoryEstimateService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
        sqlAssessment.DatabaseMappings.First().ContainerMappings.First().RequiredTransformations.Clear();

        // Act
        var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

        // Assert
        var pipelineEst = result.PipelineEstimates.First();
        pipelineEst.TransformationComplexity.Should().Be("None");
    }
}
