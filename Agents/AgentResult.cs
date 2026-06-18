namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Terminal outcome of a single <see cref="IAssessmentAgent"/> run.
/// </summary>
public enum AgentRunStatus
{
    /// <summary>The agent completed and committed all of its required outputs to the context.</summary>
    Succeeded,

    /// <summary>The agent threw or otherwise failed; its outputs may be absent or partial.</summary>
    Failed,

    /// <summary>The agent did not run because a precondition (e.g. a dependency) was not met.</summary>
    Skipped
}

/// <summary>
/// Immutable record of how a single agent run finished. Recorded on the
/// <see cref="SharedAssessmentContext"/> so the orchestrator can isolate failures and the
/// <c>ValidatorAgent</c> can reason about completeness.
/// </summary>
/// <remarks>
/// Contract: a <see cref="AgentRunStatus.Succeeded"/> result guarantees the agent's required
/// outputs were committed to the context. <see cref="AgentRunStatus.Failed"/> and
/// <see cref="AgentRunStatus.Skipped"/> make no such guarantee — consumers must treat the
/// corresponding outputs as possibly absent.
/// </remarks>
public sealed record AgentResult
{
    /// <summary>The <see cref="IAssessmentAgent.Name"/> of the agent this result describes.</summary>
    public required string AgentName { get; init; }

    /// <summary>The domain role the agent owns.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>The terminal status of the run.</summary>
    public required AgentRunStatus Status { get; init; }

    /// <summary>
    /// The failure message when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>, or the
    /// skip reason when <see cref="AgentRunStatus.Skipped"/>. <see langword="null"/> on success.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock duration of the run. <see cref="TimeSpan.Zero"/> for skipped agents.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="agentName">The agent's name.</param>
    /// <param name="role">The agent's role.</param>
    /// <param name="duration">Wall-clock duration of the run.</param>
    /// <returns>A succeeded <see cref="AgentResult"/>.</returns>
    public static AgentResult Succeeded(string agentName, AgentRole role, TimeSpan duration) =>
        new() { AgentName = agentName, Role = role, Status = AgentRunStatus.Succeeded, Duration = duration };

    /// <summary>Creates a failed result.</summary>
    /// <param name="agentName">The agent's name.</param>
    /// <param name="role">The agent's role.</param>
    /// <param name="error">The failure message.</param>
    /// <param name="duration">Wall-clock duration before failure.</param>
    /// <returns>A failed <see cref="AgentResult"/>.</returns>
    public static AgentResult Failed(string agentName, AgentRole role, string error, TimeSpan duration) =>
        new() { AgentName = agentName, Role = role, Status = AgentRunStatus.Failed, Error = error, Duration = duration };

    /// <summary>Creates a skipped result.</summary>
    /// <param name="agentName">The agent's name.</param>
    /// <param name="role">The agent's role.</param>
    /// <param name="reason">Why the agent was skipped (e.g. an unmet dependency).</param>
    /// <returns>A skipped <see cref="AgentResult"/>.</returns>
    public static AgentResult Skipped(string agentName, AgentRole role, string reason) =>
        new() { AgentName = agentName, Role = role, Status = AgentRunStatus.Skipped, Error = reason, Duration = TimeSpan.Zero };
}
