namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// A single-responsibility participant in an agentic assessment run. Each agent owns one
/// <see cref="AgentRole"/>, reads any upstream outputs it needs from the shared context, performs its
/// work, and commits its own output back to the context.
/// </summary>
/// <remarks>
/// This is the stable abstraction the orchestrator (#215) and later epics (#132/#133/#69) compose
/// against. Implementations must:
/// <list type="bullet">
///   <item>be safe to construct via dependency injection and to run independently in isolation;</item>
///   <item>never throw for a recoverable condition — return a <see cref="AgentResult.Failed"/> or
///   <see cref="AgentResult.Skipped"/> result and log a message instead, so a single agent's problem
///   does not abort the whole run;</item>
///   <item>commit their domain output exactly once via the context's <c>Set*</c> methods on success.</item>
/// </list>
/// </remarks>
public interface IAssessmentAgent
{
    /// <summary>A stable, unique, human-readable identifier for this agent (e.g. <c>CosmosAnalyzer</c>).</summary>
    string Name { get; }

    /// <summary>The assessment domain this agent owns and contributes output for.</summary>
    AgentRole Role { get; }

    /// <summary>
    /// The roles whose outputs this agent requires before it can run. The orchestrator uses this to
    /// order sequential runs, gate conditional runs, and schedule parallel runs. An empty collection
    /// means the agent has no upstream dependencies.
    /// </summary>
    IReadOnlyCollection<AgentRole> Dependencies { get; }

    /// <summary>
    /// Executes the agent against the shared context and returns its terminal result. Implementations
    /// should catch their own recoverable errors and report them through the returned
    /// <see cref="AgentResult"/> rather than throwing.
    /// </summary>
    /// <param name="context">The shared blackboard to read upstream outputs from and write this agent's output to.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>The terminal <see cref="AgentResult"/> describing how the run finished.</returns>
    Task<AgentResult> RunAsync(ISharedAssessmentContext context, CancellationToken cancellationToken = default);
}
