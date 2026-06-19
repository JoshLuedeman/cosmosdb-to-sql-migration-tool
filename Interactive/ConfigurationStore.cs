using System.Text.Json;
using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Abstraction for persisting wizard configuration.
/// </summary>
internal interface IConfigurationStore
{
    /// <summary>Saves configuration to the specified path.</summary>
    void Save(WizardConfiguration config, string path);

    /// <summary>Loads configuration from the specified path, or null if not found.</summary>
    WizardConfiguration? Load(string path);
}

/// <summary>
/// JSON file-based implementation of <see cref="IConfigurationStore"/>.
/// </summary>
internal sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true
    };

    public void Save(WizardConfiguration config, string path)
    {
        var json = JsonSerializer.Serialize(config, _options);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, json);
    }

    public WizardConfiguration? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WizardConfiguration>(json);
    }

    /// <summary>
    /// Converts a <see cref="WizardConfiguration"/> to <see cref="CliOptions"/>.
    /// </summary>
    public static CliOptions ToCliOptions(WizardConfiguration config)
    {
        return new CliOptions
        {
            AccountEndpoint = config.Endpoint,
            AnalyzeAllDatabases = config.AnalyzeAllDatabases,
            DatabaseName = config.DatabaseName,
            WorkspaceId = config.WorkspaceId,
            AutoDiscoverMonitoring = config.AutoDiscover,
            OutputDirectory = config.OutputDirectory,
            AssessmentOnly = config.AssessmentOnly,
            ProjectOnly = config.ProjectOnly,
            Interactive = true
        };
    }

    /// <summary>
    /// Converts <see cref="CliOptions"/> to a <see cref="WizardConfiguration"/>.
    /// </summary>
    public static WizardConfiguration FromCliOptions(CliOptions options)
    {
        return new WizardConfiguration
        {
            Endpoint = options.AccountEndpoint,
            AnalyzeAllDatabases = options.AnalyzeAllDatabases,
            DatabaseName = options.DatabaseName,
            WorkspaceId = options.WorkspaceId,
            AutoDiscover = options.AutoDiscoverMonitoring,
            OutputDirectory = options.OutputDirectory,
            AssessmentOnly = options.AssessmentOnly,
            ProjectOnly = options.ProjectOnly
        };
    }
}
