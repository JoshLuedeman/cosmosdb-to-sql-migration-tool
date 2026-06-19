using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Coordinates the assessment agents over a single <see cref="SharedAssessmentContext"/>, scheduling them
/// according to their declared <see cref="IAssessmentAgent.Dependencies"/> and the requested
/// <see cref="AgentExecutionMode"/>, isolating per-agent failures, and finishing with the validation pass.
/// </summary>
/// <remarks>
/// <para>
/// This is the stable public entry point that downstream agentic features build on. Producers (every agent
/// except the validator) run first; the validator runs last so it observes every result. The Data Factory
/// estimate is produced by an internal agent and therefore schedules uniformly through the dependency graph.
/// </para>
/// <para>
/// The orchestrator validates the agent graph up front (see the constructor) and never throws for a
/// recoverable agent failure — a failed or timed-out agent is recorded and the run continues, mirroring the
/// single-pass pipeline's tolerance while still flagging missing required outputs through the validator.
/// </para>
/// </remarks>
public sealed class AgentOrchestrator
{
    private readonly IReadOnlyList<IAssessmentAgent> _producers;
    private readonly IReadOnlyList<IAssessmentAgent> _validators;
    private readonly ILogger<AgentOrchestrator> _logger;

    /// <summary>
    /// Creates a new <see cref="AgentOrchestrator"/> over the supplied agents.
    /// </summary>
    /// <param name="agents">
    /// The agents to coordinate. Exactly one agent must own each of the required roles
    /// (<see cref="AgentRole.CosmosAnalysis"/>, <see cref="AgentRole.SqlPlanning"/>,
    /// <see cref="AgentRole.DataFactoryEstimation"/>, <see cref="AgentRole.Validation"/>); data quality is
    /// optional. No two agents may share a role or a name, and every dependency role must be produced by a
    /// registered agent.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="agents"/> or <paramref name="logger"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the agent graph violates the invariants above.</exception>
    public AgentOrchestrator(IEnumerable<IAssessmentAgent> agents, ILogger<AgentOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(agents);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var all = agents.ToList();
        ValidateGraph(all);

        _validators = all.Where(a => a.Role == AgentRole.Validation).ToList();
        _producers = all.Where(a => a.Role != AgentRole.Validation)
            .OrderBy(a => RolePriority(a.Role))
            .ToList();
    }

    /// <summary>
    /// Runs the agentic assessment for a single database and returns an immutable result.
    /// </summary>
    /// <param name="databaseName">The Cosmos DB database to assess.</param>
    /// <param name="cosmosAccountName">Friendly name of the Cosmos DB account (used in the projected result).</param>
    /// <param name="options">Scheduling options; defaults to sequential with no timeout.</param>
    /// <param name="cancellationToken">Token used to cancel the whole run (propagates; does not become a failed result).</param>
    /// <returns>The immutable <see cref="AgentOrchestrationResult"/> for the run.</returns>
    public async Task<AgentOrchestrationResult> RunAsync(
        string databaseName,
        string cosmosAccountName,
        AgentOrchestrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= AgentOrchestrationOptions.Default;
        var context = new SharedAssessmentContext(databaseName, cosmosAccountName);

        _logger.LogInformation(
            "AgentOrchestrator starting for database {DatabaseName} in {Mode} mode with {ProducerCount} producer agent(s)",
            databaseName, options.Mode, _producers.Count);

        await RunProducersAsync(context, options, cancellationToken).ConfigureAwait(false);

        // Validators always run last, sequentially, so they observe every producer result.
        foreach (var validator in _validators)
        {
            await RunAgentAsync(validator, context, options, cancellationToken).ConfigureAwait(false);
        }

        var validation = context.ValidationReport;
        var result = new AgentOrchestrationResult
        {
            AssessmentResult = context.ToAssessmentResult(),
            Validation = validation,
            AgentResults = context.Results,
            Messages = context.Messages,
            IsAcceptable = validation?.IsAcceptable ?? false,
            Mode = options.Mode
        };

        _logger.LogInformation(
            "AgentOrchestrator finished for database {DatabaseName}: acceptable={IsAcceptable}, {ResultCount} agent result(s)",
            databaseName, result.IsAcceptable, result.AgentResults.Count);

        return result;
    }

    private async Task RunProducersAsync(
        SharedAssessmentContext context, AgentOrchestrationOptions options, CancellationToken cancellationToken)
    {
        var remaining = _producers.ToList();
        var completedRoles = new HashSet<AgentRole>();

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(a => a.Dependencies.All(completedRoles.Contains)).ToList();
            if (ready.Count == 0)
            {
                // Deadlock guard: dependencies can never be satisfied (e.g. an unmet/derived role). Run the
                // rest in priority order; each agent self-skips when its required upstream output is missing.
                ready = remaining;
            }

            if (options.Mode == AgentExecutionMode.Parallel)
            {
                // Run the whole ready wave concurrently.
                await Task.WhenAll(ready.Select(a => RunAgentAsync(a, context, options, cancellationToken)))
                    .ConfigureAwait(false);

                foreach (var agent in ready)
                {
                    completedRoles.Add(agent.Role);
                    remaining.Remove(agent);
                }
            }
            else
            {
                // Sequential / Conditional: take the single highest-priority ready agent.
                var agent = ready[0];

                if (options.Mode == AgentExecutionMode.Conditional && !DependenciesSucceeded(agent, context))
                {
                    RecordConditionalSkip(agent, context);
                }
                else
                {
                    await RunAgentAsync(agent, context, options, cancellationToken).ConfigureAwait(false);
                }

                completedRoles.Add(agent.Role);
                remaining.Remove(agent);
            }
        }
    }

    private bool DependenciesSucceeded(IAssessmentAgent agent, ISharedAssessmentContext context) =>
        agent.Dependencies.All(context.HasSucceeded);

    private void RecordConditionalSkip(IAssessmentAgent agent, ISharedAssessmentContext context)
    {
        var unmet = agent.Dependencies.Where(r => !context.HasSucceeded(r)).ToArray();
        var reason = $"Conditional skip: dependency role(s) [{string.Join(", ", unmet)}] did not succeed.";
        context.LogWarning(agent.Name, $"{agent.Name} skipped by orchestrator. {reason}");
        context.RecordResult(AgentResult.Skipped(agent.Name, agent.Role, reason));
    }

    private async Task RunAgentAsync(
        IAssessmentAgent agent, SharedAssessmentContext context, AgentOrchestrationOptions options, CancellationToken cancellationToken)
    {
        if (options.PerAgentTimeout is not TimeSpan timeout)
        {
            await agent.RunAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await agent.RunAsync(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-agent timeout (not a global cancellation): record as failed and let the run continue.
            var message = $"Timed out after {timeout.TotalSeconds:F0}s.";
            context.LogError(agent.Name, $"{agent.Name} timed out: {message}");
            context.RecordResult(AgentResult.Failed(agent.Name, agent.Role, message, timeout));
        }
    }

    private static int RolePriority(AgentRole role) => role switch
    {
        AgentRole.CosmosAnalysis => 0,
        AgentRole.SqlPlanning => 1,
        AgentRole.DataQuality => 2,
        AgentRole.DataFactoryEstimation => 3,
        AgentRole.Validation => 4,
        AgentRole.Orchestration => 5,
        _ => 100
    };

    private static void ValidateGraph(IReadOnlyList<IAssessmentAgent> agents)
    {
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("At least one agent must be supplied to the orchestrator.");
        }

        var duplicateNames = agents
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate agent name(s): {string.Join(", ", duplicateNames)}. Agent names must be unique.");
        }

        var duplicateRoles = agents
            .GroupBy(a => a.Role)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicateRoles.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple agents share role(s): {string.Join(", ", duplicateRoles)}. " +
                "Each role's output is write-once, so at most one agent may own a role.");
        }

        var roles = agents.Select(a => a.Role).ToHashSet();
        var requiredRoles = new[]
        {
            AgentRole.CosmosAnalysis,
            AgentRole.SqlPlanning,
            AgentRole.DataFactoryEstimation,
            AgentRole.Validation
        };
        var missingRoles = requiredRoles.Where(r => !roles.Contains(r)).ToArray();
        if (missingRoles.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required agent role(s): {string.Join(", ", missingRoles)}.");
        }

        foreach (var agent in agents)
        {
            var dangling = agent.Dependencies.Where(d => !roles.Contains(d)).ToArray();
            if (dangling.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Agent '{agent.Name}' depends on role(s) [{string.Join(", ", dangling)}] " +
                    "that no registered agent produces.");
            }
        }
    }
}
