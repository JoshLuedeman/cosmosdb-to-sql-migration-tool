namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// The typed verdict produced by the <see cref="ValidatorAgent"/> after cross-checking the outputs of the
/// other agents. It is the stable, structured surface downstream consumers (and the orchestrator/CLI) use
/// to decide whether an agentic run succeeded.
/// </summary>
/// <remarks>
/// <para>
/// This is <strong>run metadata, not a domain assessment output</strong>: it is not listed by
/// <see cref="ISharedAssessmentContext.GetMissingRequiredOutputs"/> and it does not feed
/// <see cref="ISharedAssessmentContext.ToAssessmentResult"/>. A missing report
/// (<see cref="ISharedAssessmentContext.HasValidationReport"/> is <see langword="false"/>) means validation
/// never ran or crashed before it could emit a report — consumers should treat that as an infrastructure
/// failure distinct from an unacceptable-but-reported run.
/// </para>
/// <para>
/// Consumers should prefer the typed <see cref="IsAcceptable"/>/<see cref="IsComplete"/> verdict over parsing
/// <see cref="ISharedAssessmentContext.Messages"/>.
/// </para>
/// </remarks>
public sealed record ValidationReport
{
    /// <summary>
    /// Whether every required output (Cosmos analysis, SQL assessment, Data Factory estimate) is present.
    /// Driven by <see cref="MissingRequiredOutputs"/>; data quality is optional and never affects this.
    /// </summary>
    public required bool IsComplete { get; init; }

    /// <summary>
    /// Whether the agent outputs are mutually consistent — i.e. there are no
    /// <see cref="AgentMessageLevel.Error"/>-level <see cref="ValidationFindingCategory.Consistency"/> findings.
    /// </summary>
    public required bool IsConsistent { get; init; }

    /// <summary>
    /// The overall verdict: the run is acceptable when it is both complete and consistent. The orchestrator
    /// and CLI derive the process exit code from this.
    /// </summary>
    public bool IsAcceptable => IsComplete && IsConsistent;

    /// <summary>The names of required outputs that were missing at validation time; empty when complete.</summary>
    public IReadOnlyList<string> MissingRequiredOutputs { get; init; } = Array.Empty<string>();

    /// <summary>The names of agents that recorded a failed result; diagnostic only, never affects the verdict.</summary>
    public IReadOnlyList<string> FailedAgents { get; init; } = Array.Empty<string>();

    /// <summary>All findings produced during validation, in the order they were discovered.</summary>
    public IReadOnlyList<ValidationFinding> Findings { get; init; } = Array.Empty<ValidationFinding>();

    /// <summary>Whether optional data-quality analysis was available at validation time.</summary>
    public bool DataQualityAvailable { get; init; }
}
