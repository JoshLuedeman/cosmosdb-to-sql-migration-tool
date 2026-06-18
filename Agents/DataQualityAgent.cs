using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Agent that owns the pre-migration data-quality domain. It wraps the existing
/// <see cref="DataQualityAnalysisService"/> (unchanged) and commits the resulting
/// <see cref="Models.DataQualityAnalysis"/> to the shared context.
/// </summary>
/// <remarks>
/// Depends on <see cref="AgentRole.CosmosAnalysis"/> and skips when it is absent. Its output is
/// <strong>optional</strong>: a skipped or failed data-quality run leaves
/// <see cref="ISharedAssessmentContext.DataQualityAnalysis"/> null and does <em>not</em> make the overall
/// run incomplete, mirroring the single-pass pipeline where data quality is non-fatal.
/// </remarks>
public sealed class DataQualityAgent : AssessmentAgentBase
{
    /// <summary>The stable name of this agent.</summary>
    public const string AgentName = "DataQuality";

    private static readonly IReadOnlyCollection<AgentRole> _dependencies = new[] { AgentRole.CosmosAnalysis };

    private readonly DataQualityAnalysisService _dataQualityService;
    private readonly ILogger<DataQualityAgent> _logger;

    /// <summary>
    /// Creates a new <see cref="DataQualityAgent"/>.
    /// </summary>
    /// <param name="dataQualityService">The existing data-quality analysis service to wrap.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public DataQualityAgent(DataQualityAnalysisService dataQualityService, ILogger<DataQualityAgent> logger)
    {
        _dataQualityService = dataQualityService ?? throw new ArgumentNullException(nameof(dataQualityService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Name => AgentName;

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.DataQuality;

    /// <inheritdoc />
    public override IReadOnlyCollection<AgentRole> Dependencies => _dependencies;

    /// <inheritdoc />
    protected override string? GetSkipReason(ISharedAssessmentContext context) =>
        context.HasCosmosAnalysis
            ? null
            : "Cosmos analysis is not available; cannot analyze data quality.";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
    {
        var cosmosAnalysis = context.CosmosAnalysis!; // guaranteed present past the skip gate
        _logger.LogInformation("DataQualityAgent analyzing data quality for database {DatabaseName}", context.DatabaseName);

        var analysis = await _dataQualityService
            .AnalyzeDataQualityAsync(cosmosAnalysis, context.DatabaseName, cancellationToken)
            .ConfigureAwait(false);

        context.LogInfo(Name,
            $"Analyzed {analysis.TotalDocumentsAnalyzed} document(s); " +
            $"{analysis.CriticalIssuesCount} critical, {analysis.WarningIssuesCount} warning(s); " +
            $"quality score {analysis.Summary.OverallQualityScore:F1}/100 ({analysis.Summary.QualityRating}).");

        // Commit is the final action so a later failure never leaves a half-populated context.
        context.SetDataQualityAnalysis(Name, analysis);
    }
}
