namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Classifies a <see cref="ValidationFinding"/> by the kind of problem it represents, so consumers can
/// reason about which findings affect run completeness versus cross-output consistency versus pure diagnostics.
/// </summary>
public enum ValidationFindingCategory
{
    /// <summary>
    /// A required domain output is missing. Completeness findings drive
    /// <see cref="ValidationReport.IsComplete"/>.
    /// </summary>
    Completeness,

    /// <summary>
    /// Two or more agent outputs disagree (e.g. a Cosmos container has no SQL mapping). An
    /// <see cref="AgentMessageLevel.Error"/> consistency finding makes
    /// <see cref="ValidationReport.IsConsistent"/> false.
    /// </summary>
    Consistency,

    /// <summary>
    /// Informational or non-fatal observation (e.g. an optional agent was skipped or failed). Diagnostic
    /// findings never affect the report's <see cref="ValidationReport.IsAcceptable"/> verdict.
    /// </summary>
    Diagnostic
}

/// <summary>
/// A single observation produced by the <see cref="ValidatorAgent"/> while cross-checking agent outputs.
/// </summary>
/// <remarks>
/// <see cref="Check"/> is a stable, machine-readable identifier (see the <c>Check*</c> constants on
/// <see cref="ValidatorAgent"/>) intended for filtering and display by downstream consumers;
/// <see cref="Detail"/> carries the human-readable explanation.
/// </remarks>
public sealed record ValidationFinding
{
    /// <summary>The category of problem this finding represents.</summary>
    public required ValidationFindingCategory Category { get; init; }

    /// <summary>The severity of the finding.</summary>
    public required AgentMessageLevel Level { get; init; }

    /// <summary>A stable, machine-readable identifier for the check that produced this finding.</summary>
    public required string Check { get; init; }

    /// <summary>A human-readable description of the finding.</summary>
    public required string Detail { get; init; }

    /// <summary>Creates a finding.</summary>
    /// <param name="category">The category of the finding.</param>
    /// <param name="level">The severity of the finding.</param>
    /// <param name="check">The stable check identifier.</param>
    /// <param name="detail">The human-readable detail.</param>
    /// <returns>A new <see cref="ValidationFinding"/>.</returns>
    public static ValidationFinding Create(
        ValidationFindingCategory category, AgentMessageLevel level, string check, string detail) =>
        new() { Category = category, Level = level, Check = check, Detail = detail };
}
