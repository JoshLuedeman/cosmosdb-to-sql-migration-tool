using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Internal agent that produces the Data Factory migration estimate. The estimate is a step derived from the
/// Cosmos analysis and SQL assessment rather than a standalone public agent, so this type is
/// <see langword="internal"/> — but it participates in the dependency graph like any other agent so the
/// orchestrator can schedule it uniformly.
/// </summary>
/// <remarks>
/// Wraps the unchanged <see cref="DataFactoryEstimateService"/>. Depends on
/// <see cref="AgentRole.CosmosAnalysis"/> and <see cref="AgentRole.SqlPlanning"/> and skips if either is
/// absent. Its output is required for a complete assessment.
/// </remarks>
internal sealed class DataFactoryEstimatorAgent : AssessmentAgentBase
{
    /// <summary>The stable name of this agent.</summary>
    public const string AgentName = "DataFactoryEstimator";

    private static readonly IReadOnlyCollection<AgentRole> _dependencies =
        new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning };

    private readonly DataFactoryEstimateService _dataFactoryService;
    private readonly ILogger<DataFactoryEstimatorAgent> _logger;

    /// <summary>
    /// Creates a new <see cref="DataFactoryEstimatorAgent"/>.
    /// </summary>
    /// <param name="dataFactoryService">The existing Data Factory estimate service to wrap.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public DataFactoryEstimatorAgent(DataFactoryEstimateService dataFactoryService, ILogger<DataFactoryEstimatorAgent> logger)
    {
        _dataFactoryService = dataFactoryService ?? throw new ArgumentNullException(nameof(dataFactoryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Name => AgentName;

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DataFactoryEstimation;

    /// <inheritdoc />
    public override IReadOnlyCollection<AgentRole> Dependencies => _dependencies;

    /// <inheritdoc />
    protected override string? GetSkipReason(ISharedAssessmentContext context)
    {
        if (!context.HasCosmosAnalysis)
        {
            return "Cosmos analysis is not available; cannot estimate the Data Factory migration.";
        }

        if (!context.HasSqlAssessment)
        {
            return "SQL assessment is not available; cannot estimate the Data Factory migration.";
        }

        return null;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DataFactoryEstimatorAgent estimating migration for database {DatabaseName}", context.DatabaseName);

        var estimate = await _dataFactoryService
            .EstimateMigrationAsync(context.CosmosAnalysis!, context.SqlAssessment!, cancellationToken)
            .ConfigureAwait(false);

        context.LogInfo(Name,
            $"Estimated duration {estimate.EstimatedDuration:hh\\:mm\\:ss}, " +
            $"{estimate.RecommendedDIUs} DIU(s), ~${estimate.EstimatedCostUSD:F2}.");

        // Commit is the final action so a later failure never leaves a half-populated context.
        context.SetDataFactoryEstimate(Name, estimate);
    }
}
