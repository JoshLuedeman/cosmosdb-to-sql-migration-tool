using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Agent that owns the Cosmos DB analysis domain. It wraps the existing
/// <see cref="CosmosDbAnalysisService"/> (whose signatures are intentionally unchanged) and commits the
/// resulting <see cref="Models.CosmosDbAnalysis"/> to the shared context for downstream agents.
/// </summary>
/// <remarks>
/// This is the root agent of an assessment run (no dependencies). It delegates to
/// <see cref="CosmosDbAnalysisService.AnalyzeDatabaseAsync(string, CancellationToken)"/> — the same method
/// the single-pass orchestrator uses — which streams documents per container internally and returns
/// aggregated container metadata, so the agentic path stays equivalent to single-pass and introduces no
/// whole-dataset buffering.
/// </remarks>
public sealed class CosmosAnalyzerAgent : AssessmentAgentBase
{
    /// <summary>The stable name of this agent.</summary>
    public const string AgentName = "CosmosAnalyzer";

    private readonly CosmosDbAnalysisService _analysisService;
    private readonly ILogger<CosmosAnalyzerAgent> _logger;

    /// <summary>
    /// Creates a new <see cref="CosmosAnalyzerAgent"/>.
    /// </summary>
    /// <param name="analysisService">The existing Cosmos DB analysis service to wrap.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public CosmosAnalyzerAgent(CosmosDbAnalysisService analysisService, ILogger<CosmosAnalyzerAgent> logger)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Name => AgentName;

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.CosmosAnalysis;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("CosmosAnalyzerAgent analyzing database {DatabaseName}", context.DatabaseName);

        var analysis = await _analysisService
            .AnalyzeDatabaseAsync(context.DatabaseName, cancellationToken)
            .ConfigureAwait(false);

        context.LogInfo(Name, $"Analyzed {analysis.Containers.Count} container(s).");
        if (analysis.MonitoringLimitations.Count > 0)
        {
            context.LogWarning(Name, $"{analysis.MonitoringLimitations.Count} monitoring limitation(s) detected.");
        }

        // Commit is the final action so a later failure never leaves a half-populated context.
        context.SetCosmosAnalysis(Name, analysis);
    }
}
