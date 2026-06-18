namespace CosmosToSqlAssessment.Cli;

/// <summary>
/// POCO holding parsed command-line options for the Cosmos DB to SQL Migration Assessment Tool.
/// Populated by <see cref="CliArgumentParser"/>.
/// </summary>
internal sealed class CliOptions
{
    public bool AnalyzeAllDatabases { get; set; }
    public string? DatabaseName { get; set; }
    public string? OutputDirectory { get; set; }
    public bool AutoDiscoverMonitoring { get; set; }
    public string? AccountEndpoint { get; set; }
    public string? WorkspaceId { get; set; }
    public bool AssessmentOnly { get; set; }
    public bool ProjectOnly { get; set; }
    public bool TestConnection { get; set; }
    public bool Interactive { get; set; }
    public string? ConfigFile { get; set; }
    public string? SaveConfigFile { get; set; }
}
