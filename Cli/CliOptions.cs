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
    public bool SkipAutoDiscovery { get; set; }
    public string? AccountEndpoint { get; set; }
    public string? WorkspaceId { get; set; }
    public bool AssessmentOnly { get; set; }
    public bool ProjectOnly { get; set; }
    public bool TestConnection { get; set; }
    public bool Interactive { get; set; }
    public bool Agentic { get; set; }
    public bool EnableFeedback { get; set; }
    public bool DisableFeedback { get; set; }
    public string? ConfigFile { get; set; }
    public string? SaveConfigFile { get; set; }
    public bool ResumeSession { get; set; }

    /// <summary>
    /// When <c>true</c>, the tool runs the <c>migration status</c> subcommand: it reports
    /// live migration progress instead of running an assessment. Set by the
    /// <c>migration status</c> subcommand (#225).
    /// </summary>
    public bool MigrationStatus { get; set; }

    /// <summary>
    /// When <c>true</c> (and <see cref="MigrationStatus"/> is set), the status command keeps
    /// polling and re-rendering progress until cancelled, rather than printing a single snapshot.
    /// </summary>
    public bool Watch { get; set; }

    /// <summary>
    /// Polling interval, in seconds, used by the <c>migration status --watch</c> loop.
    /// Defaults to 10.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 10;
}
