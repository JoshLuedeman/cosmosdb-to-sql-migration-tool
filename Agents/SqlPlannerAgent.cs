using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Agent that owns the Azure SQL migration-planning domain. It wraps the existing
/// <see cref="SqlMigrationAssessmentService"/> (unchanged) and commits the resulting
/// <see cref="Models.SqlMigrationAssessment"/> to the shared context.
/// </summary>
/// <remarks>
/// Depends on <see cref="AgentRole.CosmosAnalysis"/>: it skips cleanly (recording an
/// <see cref="AgentRunStatus.Skipped"/> result) when no Cosmos analysis is present, which is how an
/// upstream Cosmos failure degrades to a missing SQL plan instead of aborting the whole run. Because
/// <c>SqlAssessment</c> is a required output, the orchestrator/validator treat its absence as an
/// incomplete run.
/// </remarks>
public sealed class SqlPlannerAgent : AssessmentAgentBase
{
    /// <summary>The stable name of this agent.</summary>
    public const string AgentName = "SqlPlanner";

    private static readonly IReadOnlyCollection<AgentRole> _dependencies = new[] { AgentRole.CosmosAnalysis };

    private readonly SqlMigrationAssessmentService _assessmentService;
    private readonly ILogger<SqlPlannerAgent> _logger;

    /// <summary>
    /// Creates a new <see cref="SqlPlannerAgent"/>.
    /// </summary>
    /// <param name="assessmentService">The existing SQL migration assessment service to wrap.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public SqlPlannerAgent(SqlMigrationAssessmentService assessmentService, ILogger<SqlPlannerAgent> logger)
    {
        _assessmentService = assessmentService ?? throw new ArgumentNullException(nameof(assessmentService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Name => AgentName;

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.SqlPlanning;

    /// <inheritdoc />
    public override IReadOnlyCollection<AgentRole> Dependencies => _dependencies;

    /// <inheritdoc />
    protected override string? GetSkipReason(ISharedAssessmentContext context) =>
        context.HasCosmosAnalysis
            ? null
            : "Cosmos analysis is not available; cannot plan the SQL migration.";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
    {
        var cosmosAnalysis = context.CosmosAnalysis!; // guaranteed present past the skip gate
        _logger.LogInformation("SqlPlannerAgent assessing SQL migration for database {DatabaseName}", context.DatabaseName);

        var assessment = await _assessmentService
            .AssessMigrationAsync(cosmosAnalysis, context.DatabaseName, cancellationToken)
            .ConfigureAwait(false);

        context.LogInfo(Name,
            $"Recommended platform {assessment.RecommendedPlatform}; " +
            $"{assessment.IndexRecommendations.Count} index recommendation(s); " +
            $"complexity {assessment.Complexity.OverallComplexity}.");

        // Commit is the final action so a later failure never leaves a half-populated context.
        context.SetSqlAssessment(Name, assessment);
    }
}
