namespace CosmosToSqlAssessment.Tests.Infrastructure;

/// <summary>
/// Base class for all test classes, provides common test infrastructure
/// </summary>
public abstract class TestBase
{
    protected Mock<IConfiguration> MockConfiguration { get; }

    protected TestBase()
    {
        MockConfiguration = new Mock<IConfiguration>();
        SetupDefaultConfiguration();
    }

    private void SetupDefaultConfiguration()
    {
        // Setup IConfiguration indexer to return values
        MockConfiguration.Setup(c => c["Reporting:OutputDirectory"]).Returns("Reports");
        MockConfiguration.Setup(c => c["Azure:SubscriptionId"]).Returns("test-subscription-id");
        MockConfiguration.Setup(c => c["CosmosDb:AccountEndpoint"]).Returns("https://test.documents.azure.com:443/");
        MockConfiguration.Setup(c => c["CosmosDb:DatabaseName"]).Returns("TestDatabase");

        // Setup GetSection for any section returns empty section
        var emptySection = new Mock<IConfigurationSection>();
        emptySection.Setup(s => s.Value).Returns((string)null!);
        emptySection.Setup(s => s.GetChildren()).Returns(Enumerable.Empty<IConfigurationSection>());
        MockConfiguration.Setup(c => c.GetSection(It.IsAny<string>())).Returns(emptySection.Object);
    }

    protected Mock<ILogger<T>> CreateMockLogger<T>()
    {
        return new Mock<ILogger<T>>();
    }

    protected static AssessmentResult CreateSampleAssessmentResult()
    {
        return TestDataFactory.CreateSampleAssessmentResult();
    }
}
