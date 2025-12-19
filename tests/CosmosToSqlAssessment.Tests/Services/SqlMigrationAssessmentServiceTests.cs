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
        var result = await service.AssessMigrationAsync(cosmosAnalysis);

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
        var result = await service.AssessMigrationAsync(cosmosAnalysis);

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
        var result = await service.AssessMigrationAsync(cosmosAnalysis);

        // Assert
        result.IndexRecommendations.Should().NotBeNull();
    }
}
