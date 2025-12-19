using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services;

public class CosmosDbAnalysisServiceTests : TestBase
{
    [Fact]
    public void Constructor_WithValidConfiguration_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<CosmosDbAnalysisService>();

        // Act
        var service = new CosmosDbAnalysisService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithoutCosmosEndpoint_ShouldThrowArgumentException()
    {
        // Arrange
        var badConfig = new Mock<IConfiguration>();
        var logger = CreateMockLogger<CosmosDbAnalysisService>();

        // Act & Assert
        Action act = () => new CosmosDbAnalysisService(badConfig.Object, logger.Object);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_WithNullDatabaseName_ShouldThrowArgumentException()
    {
        // Arrange
        var logger = CreateMockLogger<CosmosDbAnalysisService>();
        var service = new CosmosDbAnalysisService(MockConfiguration.Object, logger.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeDatabaseAsync(null!));
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_WithEmptyDatabaseName_ShouldThrowArgumentException()
    {
        // Arrange
        var logger = CreateMockLogger<CosmosDbAnalysisService>();
        var service = new CosmosDbAnalysisService(MockConfiguration.Object, logger.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeDatabaseAsync(""));
    }
}
