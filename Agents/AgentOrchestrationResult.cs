using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// The immutable outcome of an <see cref="AgentOrchestrator"/> run. It exposes point-in-time snapshots only;
/// the live, mutable <see cref="SharedAssessmentContext"/> is intentionally not surfaced so callers cannot
/// retroactively change the projected result.
/// </summary>
/// <remarks>
/// <strong>Consumption contract:</strong> <see cref="AssessmentResult"/> is a best-effort projection that is
/// always non-null but is only safe to drive downstream report / SQL-project / Data Factory generation when
/// <see cref="IsAcceptable"/> is <see langword="true"/>. When <see cref="IsAcceptable"/> is
/// <see langword="false"/> (a required output is missing or outputs are inconsistent), the result may contain
/// empty defaults and consumers should surface the failure instead of generating artifacts.
/// </remarks>
public sealed record AgentOrchestrationResult
{
    /// <summary>
    /// The projected assessment, equivalent to single-pass output when the run is acceptable. Only safe for
    /// downstream generation when <see cref="IsAcceptable"/> is <see langword="true"/>.
    /// </summary>
    public required AssessmentResult AssessmentResult { get; init; }

    /// <summary>The validator's verdict, or <see langword="null"/> if validation did not produce a report.</summary>
    public ValidationReport? Validation { get; init; }

    /// <summary>A snapshot of every agent result recorded during the run, in execution order.</summary>
    public IReadOnlyList<AgentResult> AgentResults { get; init; } = Array.Empty<AgentResult>();

    /// <summary>A snapshot of every message logged during the run, in order.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; init; } = Array.Empty<AgentMessage>();

    /// <summary>
    /// Whether the run is acceptable (complete and consistent). Derived from
    /// <see cref="ValidationReport.IsAcceptable"/>; <see langword="false"/> when no report was produced.
    /// </summary>
    public bool IsAcceptable { get; init; }

    /// <summary>The execution mode the run was scheduled with.</summary>
    public AgentExecutionMode Mode { get; init; }
}
