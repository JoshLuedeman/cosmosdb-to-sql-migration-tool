using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests;

public class ProgramTests : TestBase
{
    [Fact]
    public void CommandLineOptions_TestConnection_ShouldDefaultToFalse()
    {
        // Arrange & Act
        var options = new CommandLineOptions();

        // Assert
        options.TestConnection.Should().BeFalse();
    }

    [Fact]
    public void CommandLineOptions_TestConnection_CanBeSetToTrue()
    {
        // Arrange & Act
        var options = new CommandLineOptions
        {
            TestConnection = true
        };

        // Assert
        options.TestConnection.Should().BeTrue();
    }

    [Fact]
    public void CommandLineOptions_AllPropertiesInitialized()
    {
        // Arrange & Act
        var options = new CommandLineOptions
        {
            AnalyzeAllDatabases = true,
            DatabaseName = "TestDB",
            OutputDirectory = "/test/output",
            AutoDiscoverMonitoring = true,
            AccountEndpoint = "https://test.documents.azure.com:443/",
            WorkspaceId = "12345678-1234-1234-1234-123456789012",
            AssessmentOnly = true,
            ProjectOnly = false,
            TestConnection = true
        };

        // Assert
        options.AnalyzeAllDatabases.Should().BeTrue();
        options.DatabaseName.Should().Be("TestDB");
        options.OutputDirectory.Should().Be("/test/output");
        options.AutoDiscoverMonitoring.Should().BeTrue();
        options.AccountEndpoint.Should().Be("https://test.documents.azure.com:443/");
        options.WorkspaceId.Should().Be("12345678-1234-1234-1234-123456789012");
        options.AssessmentOnly.Should().BeTrue();
        options.ProjectOnly.Should().BeFalse();
        options.TestConnection.Should().BeTrue();
    }

    [Fact]
    public void CommandLineOptions_TestConnectionAndAssessmentOnly_CanBothBeTrue()
    {
        // Arrange & Act
        var options = new CommandLineOptions
        {
            TestConnection = true,
            AssessmentOnly = true
        };

        // Assert - These are not conflicting flags
        options.TestConnection.Should().BeTrue();
        options.AssessmentOnly.Should().BeTrue();
    }

    [Fact]
    public void CommandLineOptions_TestConnectionAndProjectOnly_CanBothBeTrue()
    {
        // Arrange & Act
        var options = new CommandLineOptions
        {
            TestConnection = true,
            ProjectOnly = true
        };

        // Assert - These are not conflicting flags
        options.TestConnection.Should().BeTrue();
        options.ProjectOnly.Should().BeTrue();
    }

    [Fact]
    public void UserInputs_ShouldInitializeWithDefaults()
    {
        // Act
        var inputs = new UserInputs();

        // Assert
        inputs.DatabaseNames.Should().NotBeNull();
        inputs.DatabaseNames.Should().BeEmpty();
        inputs.OutputDirectory.Should().BeEmpty();
        inputs.AccountEndpoint.Should().BeEmpty();
        inputs.MonitoringConfig.Should().BeNull();
    }

    [Fact]
    public void UserInputs_CanSetAllProperties()
    {
        // Arrange
        var monitoringConfig = new MonitoringConfiguration
        {
            WorkspaceId = "ws-123",
            SubscriptionId = "sub-456",
            ResourceGroupName = "rg-test"
        };

        // Act
        var inputs = new UserInputs
        {
            DatabaseNames = new List<string> { "DB1", "DB2" },
            OutputDirectory = "/output",
            AccountEndpoint = "https://test.documents.azure.com:443/",
            MonitoringConfig = monitoringConfig
        };

        // Assert
        inputs.DatabaseNames.Should().HaveCount(2);
        inputs.OutputDirectory.Should().Be("/output");
        inputs.AccountEndpoint.Should().Contain("documents.azure.com");
        inputs.MonitoringConfig.Should().NotBeNull();
        inputs.MonitoringConfig!.WorkspaceId.Should().Be("ws-123");
        inputs.MonitoringConfig!.SubscriptionId.Should().Be("sub-456");
        inputs.MonitoringConfig!.ResourceGroupName.Should().Be("rg-test");
    }
}
