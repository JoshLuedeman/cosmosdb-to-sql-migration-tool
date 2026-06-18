using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.Interactive;

namespace CosmosToSqlAssessment.Tests.Interactive;

public class ConfigurationStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wizard-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store = new JsonConfigurationStore();
        var path = Path.Combine(_tempDir, "config.json");

        var config = new WizardConfiguration
        {
            Endpoint = "https://myaccount.documents.azure.com:443/",
            AnalyzeAllDatabases = true,
            WorkspaceId = "12345678-1234-1234-1234-123456789012",
            AutoDiscover = true,
            OutputDirectory = "./output",
            AssessmentOnly = false,
            ProjectOnly = false
        };

        store.Save(config, path);
        var loaded = store.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Endpoint.Should().Be(config.Endpoint);
        loaded.AnalyzeAllDatabases.Should().Be(config.AnalyzeAllDatabases);
        loaded.WorkspaceId.Should().Be(config.WorkspaceId);
        loaded.AutoDiscover.Should().Be(config.AutoDiscover);
        loaded.OutputDirectory.Should().Be(config.OutputDirectory);
    }

    [Fact]
    public void Load_NonexistentFile_ReturnsNull()
    {
        var store = new JsonConfigurationStore();
        var result = store.Load(Path.Combine(_tempDir, "nonexistent.json"));
        result.Should().BeNull();
    }

    [Fact]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var store = new JsonConfigurationStore();
        var path = Path.Combine(_tempDir, "sub", "deep", "config.json");

        store.Save(new WizardConfiguration { Endpoint = "https://test.com/" }, path);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void ToCliOptions_MapsCorrectly()
    {
        var config = new WizardConfiguration
        {
            Endpoint = "https://test.documents.azure.com:443/",
            AnalyzeAllDatabases = false,
            DatabaseName = "MyDb",
            WorkspaceId = "00000000-0000-0000-0000-000000000000",
            AutoDiscover = true,
            OutputDirectory = "C:\\out",
            AssessmentOnly = true,
            ProjectOnly = false
        };

        var options = JsonConfigurationStore.ToCliOptions(config);

        options.AccountEndpoint.Should().Be(config.Endpoint);
        options.AnalyzeAllDatabases.Should().BeFalse();
        options.DatabaseName.Should().Be("MyDb");
        options.WorkspaceId.Should().Be(config.WorkspaceId);
        options.AutoDiscoverMonitoring.Should().BeTrue();
        options.OutputDirectory.Should().Be("C:\\out");
        options.AssessmentOnly.Should().BeTrue();
        options.ProjectOnly.Should().BeFalse();
        options.Interactive.Should().BeTrue();
    }

    [Fact]
    public void FromCliOptions_MapsCorrectly()
    {
        var options = new CliOptions
        {
            AccountEndpoint = "https://test.documents.azure.com:443/",
            AnalyzeAllDatabases = true,
            WorkspaceId = "00000000-0000-0000-0000-000000000000",
            AutoDiscoverMonitoring = false,
            OutputDirectory = "./reports",
            AssessmentOnly = false,
            ProjectOnly = true
        };

        var config = JsonConfigurationStore.FromCliOptions(options);

        config.Endpoint.Should().Be(options.AccountEndpoint);
        config.AnalyzeAllDatabases.Should().BeTrue();
        config.WorkspaceId.Should().Be(options.WorkspaceId);
        config.AutoDiscover.Should().BeFalse();
        config.OutputDirectory.Should().Be("./reports");
        config.AssessmentOnly.Should().BeFalse();
        config.ProjectOnly.Should().BeTrue();
    }
}
