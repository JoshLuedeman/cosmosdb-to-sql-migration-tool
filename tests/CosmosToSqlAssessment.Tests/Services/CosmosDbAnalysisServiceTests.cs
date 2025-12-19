using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Models;
using System.Text.Json;

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

    [Fact]
    public void ChildTableSchema_ShouldCorrectlyStoreChildTableType()
    {
        // Arrange & Act - Test Array type
        var arrayChildTable = new ChildTableSchema
        {
            TableName = "Orders",
            SourceFieldPath = "orders",
            ChildTableType = "Array",
            Fields = new Dictionary<string, FieldInfo>(),
            SampleCount = 10,
            ParentKeyField = "ParentId"
        };

        // Assert
        arrayChildTable.ChildTableType.Should().Be("Array");

        // Arrange & Act - Test NestedObject type
        var nestedObjectChildTable = new ChildTableSchema
        {
            TableName = "Address",
            SourceFieldPath = "address",
            ChildTableType = "NestedObject",
            Fields = new Dictionary<string, FieldInfo>(),
            SampleCount = 5,
            ParentKeyField = "ParentId"
        };

        // Assert
        nestedObjectChildTable.ChildTableType.Should().Be("NestedObject");
    }

    [Fact]
    public void ChildTableSchema_ShouldSupportManyToManyType()
    {
        // Arrange & Act
        var manyToManyChildTable = new ChildTableSchema
        {
            TableName = "ProductTags",
            SourceFieldPath = "tags",
            ChildTableType = "ManyToMany",
            Fields = new Dictionary<string, FieldInfo>(),
            SampleCount = 20,
            ParentKeyField = "ParentId"
        };

        // Assert
        manyToManyChildTable.ChildTableType.Should().Be("ManyToMany");
    }
}
