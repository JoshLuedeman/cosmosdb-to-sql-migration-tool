using CosmosToSqlAssessment.Agents;

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

    /// <summary>
    /// Scheduling mode for the multi-agent orchestration layer (#248), honoured only when
    /// <see cref="Agentic"/> is set. Defaults to <see cref="AgentExecutionMode.Sequential"/> so the
    /// single-pass-equivalence guarantee holds out of the box. Set by <c>--agentic-mode</c>.
    /// </summary>
    public AgentExecutionMode AgenticMode { get; set; } = AgentExecutionMode.Sequential;

    /// <summary>
    /// <c>true</c> when <c>--agentic-mode</c> was explicitly supplied. Used to reject specifying a
    /// mode without also enabling <c>--agentic</c> (which would silently have no effect).
    /// </summary>
    public bool AgenticModeSpecified { get; set; }

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

    /// <summary>
    /// When <c>true</c>, the tool runs the additive <c>migration generate-alerts</c> subcommand (#256):
    /// it writes the Azure Monitor alert-rule ARM templates to <see cref="OutputDirectory"/> instead of
    /// running an assessment.
    /// </summary>
    public bool GenerateAlerts { get; set; }

    /// <summary>
    /// When <c>true</c>, the tool runs the additive <c>migration publish-metrics</c> subcommand (#257):
    /// it streams ADF activity progress through the live custom-metric publisher instead of running an
    /// assessment. Honours <see cref="Watch"/> / <see cref="PollIntervalSeconds"/>.
    /// </summary>
    public bool PublishMetrics { get; set; }

    /// <summary>
    /// When <c>true</c>, the tool runs the additive <c>feedback record</c> subcommand (#259): it imports a
    /// serialized <see cref="Models.MigrationOutcome"/> from <see cref="ImportOutcomeFile"/> into the local
    /// feedback store, gated by the existing opt-in consent.
    /// </summary>
    public bool FeedbackRecord { get; set; }

    /// <summary>
    /// Path to a JSON file holding a serialized anonymized <see cref="Models.MigrationOutcome"/> to import
    /// via <c>feedback record --import-outcome</c> (#259).
    /// </summary>
    public string? ImportOutcomeFile { get; set; }
}
