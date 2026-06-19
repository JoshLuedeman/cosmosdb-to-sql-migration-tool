namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Strategy the <c>AgentOrchestrator</c> uses to schedule a set of agents.
/// </summary>
/// <remarks>
/// The mode selects a scheduling policy; actual ordering and gating are derived from each
/// agent's <see cref="IAssessmentAgent.Dependencies"/> so the orchestrator never hardcodes
/// per-agent rules.
/// </remarks>
public enum AgentExecutionMode
{
    /// <summary>Run agents one at a time in dependency order. Deterministic and easiest to debug.</summary>
    Sequential,

    /// <summary>
    /// Run agents whose dependencies are already satisfied concurrently, scheduling more as
    /// dependencies complete. Maximizes throughput for independent work.
    /// </summary>
    Parallel,

    /// <summary>
    /// Run agents in dependency order but skip any agent whose required upstream outputs are
    /// missing (for example because an upstream agent failed), rather than failing the whole run.
    /// </summary>
    Conditional
}
