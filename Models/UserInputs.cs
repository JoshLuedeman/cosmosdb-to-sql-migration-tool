namespace CosmosToSqlAssessment.Models;

/// <summary>
/// User inputs derived from a combination of command-line arguments and
/// configuration, materialized by <see cref="Orchestration.AssessmentOrchestrator"/>
/// before the per-database analysis loop begins.
/// </summary>
public class UserInputs
{
    public List<string> DatabaseNames { get; set; } = new();
    public string OutputDirectory { get; set; } = string.Empty;
    public string AccountEndpoint { get; set; } = string.Empty;
    public MonitoringConfiguration? MonitoringConfig { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the per-database assessment is computed through the multi-agent
    /// orchestration layer (<c>--agentic</c>) instead of the inline single-pass sequence. The produced
    /// assessment is equivalent; only the execution path differs.
    /// </summary>
    public bool UseAgentic { get; set; }
}

/// <summary>
/// Optional Azure Monitor configuration discovered (or supplied) at runtime.
/// </summary>
public class MonitoringConfiguration
{
    public string? WorkspaceId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? ResourceGroupName { get; set; }
}
