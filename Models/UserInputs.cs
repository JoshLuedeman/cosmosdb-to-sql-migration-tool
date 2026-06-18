namespace CosmosToSqlAssessment.Models;

/// <summary>
/// User inputs derived from a combination of command-line arguments and
/// configuration, materialized by <see cref="Orchestration.AssessmentOrchestrator"/>
/// before the per-database analysis loop begins.
/// </summary>
public class UserInputs
{
    /// <summary>
    /// One or more Cosmos DB database names to include in the assessment run.
    /// Populated from the <c>--databases</c> command-line argument or the
    /// <c>CosmosDb:DatabaseNames</c> configuration key.
    /// </summary>
    public List<string> DatabaseNames { get; set; } = new();

    /// <summary>
    /// Root directory under which per-database report folders and SQL project
    /// output are written. Defaults to the current working directory when not
    /// supplied by the caller.
    /// </summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos DB account endpoint URI (e.g. <c>https://&lt;account&gt;.documents.azure.com:443/</c>).
    /// Read from the <c>CosmosDb:AccountEndpoint</c> configuration key.
    /// </summary>
    public string AccountEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional Azure Monitor workspace configuration used to retrieve
    /// six-month performance metrics for each container. When <see langword="null"/>
    /// the assessment skips historical RU and latency analysis.
    /// </summary>
    public MonitoringConfiguration? MonitoringConfig { get; set; }
}

/// <summary>
/// Optional Azure Monitor configuration discovered (or supplied) at runtime.
/// </summary>
public class MonitoringConfiguration
{
    /// <summary>
    /// Log Analytics workspace ID used to query Azure Monitor for Cosmos DB
    /// diagnostic metrics (RU consumption, latency, throttling).
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Azure subscription ID that contains the monitored Cosmos DB account.
    /// Required when constructing Log Analytics resource scope filters.
    /// </summary>
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// Azure resource group name that contains the monitored Cosmos DB account.
    /// Used together with <see cref="SubscriptionId"/> to scope metric queries.
    /// </summary>
    public string? ResourceGroupName { get; set; }
}
