namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Options that control how the <see cref="AgentOrchestrator"/> schedules and bounds an agentic run.
/// </summary>
public sealed record AgentOrchestrationOptions
{
    /// <summary>
    /// How producer agents are scheduled. Defaults to <see cref="AgentExecutionMode.Sequential"/>, which
    /// reproduces the single-pass order (Cosmos → SQL → data quality → Data Factory).
    /// </summary>
    public AgentExecutionMode Mode { get; init; } = AgentExecutionMode.Sequential;

    /// <summary>
    /// Optional per-agent timeout. <strong>Cooperative</strong>: it cancels the token passed to each agent,
    /// so it only bounds agents (and the services they wrap) that observe cancellation. A timed-out agent is
    /// recorded as a failed result and the run continues. <see langword="null"/> (default) means no timeout.
    /// </summary>
    public TimeSpan? PerAgentTimeout { get; init; }

    /// <summary>A shared default instance using sequential scheduling and no timeout.</summary>
    public static AgentOrchestrationOptions Default { get; } = new();
}
