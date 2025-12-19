using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

public class SqlMigrationAssessmentServiceTests : TestBase
{
    [Fact]
    public void Constructor_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();

        // Act
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_WithValidInput_ShouldReturnAssessment()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.Should().NotBeNull();
        result.RecommendedPlatform.Should().NotBeNullOrEmpty();
        result.RecommendedTier.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldGenerateDatabaseMappings()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldGenerateIndexRecommendations()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.IndexRecommendations.Should().NotBeNull();
    }

    [Fact]
    public async Task AssessMigrationAsync_ShouldPopulateEstimatedRowCount()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
        result.DatabaseMappings.Should().NotBeEmpty();
        
        foreach (var dbMapping in result.DatabaseMappings)
        {
            dbMapping.ContainerMappings.Should().NotBeEmpty();
            
            foreach (var containerMapping in dbMapping.ContainerMappings)
            {
                // EstimatedRowCount should be populated from the container's DocumentCount
                containerMapping.EstimatedRowCount.Should().BeGreaterThanOrEqualTo(0);
                
                // Find the corresponding container in the original analysis
                var sourceContainer = cosmosAnalysis.Containers
                    .FirstOrDefault(c => c.ContainerName == containerMapping.SourceContainer);
                
                if (sourceContainer != null)
                {
                    containerMapping.EstimatedRowCount.Should().Be(sourceContainer.DocumentCount);
                }
            }
        }
    }

    [Fact]
    public async Task AssessMigrationAsync_WithLargeDocumentCount_ShouldPopulateEstimatedRowCount()
    {
        // Arrange
        var logger = CreateMockLogger<SqlMigrationAssessmentService>();
        var service = new SqlMigrationAssessmentService(MockConfiguration.Object, logger.Object);
        
        var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
        // Set a large document count to test thresholds
        cosmosAnalysis.Containers[0].DocumentCount = 15_000_000;

        // Act
        var result = await service.AssessMigrationAsync(cosmosAnalysis, "TestDatabase");

        // Assert
        result.DatabaseMappings.Should().NotBeNull();
        result.DatabaseMappings[0].ContainerMappings.Should().NotBeEmpty();
        result.DatabaseMappings[0].ContainerMappings[0].EstimatedRowCount.Should().Be(15_000_000);
    }
}
