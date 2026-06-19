using System.Diagnostics;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Base class for assessment agents that centralises run timing, structured logging, and the
/// failure-isolation contract so concrete agents only implement their domain logic.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> never throws for a recoverable agent error: any exception (other than
/// cancellation) is converted into an <see cref="AgentResult.Failed(string, AgentRole, string, TimeSpan)"/>
/// result and recorded on the context, so one agent's failure cannot abort the rest of the run.
/// </para>
/// <para>
/// <strong>Cancellation convention.</strong> <see cref="OperationCanceledException"/> is deliberately
/// re-thrown rather than recorded, so a host-level cancellation (Ctrl+C) can propagate and map to the
/// process's exit-code-130 handling. A <em>per-agent timeout</em> is therefore the orchestrator's
/// responsibility: it should run each agent under a linked <see cref="CancellationTokenSource"/> and catch
/// <c>OperationCanceledException when (!globalToken.IsCancellationRequested)</c> to record a failed/skipped
/// timeout result while still letting global cancellation bubble up.
/// </para>
/// <para>
/// Concrete agents should commit their domain output (via the context's <c>Set*</c> methods) as the last
/// meaningful action in <see cref="ExecuteAsync"/>, so a later failure never leaves the context partially
/// populated.
/// </para>
/// </remarks>
public abstract class AssessmentAgentBase : IAssessmentAgent
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract AgentRole Role { get; }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<AgentRole> Dependencies => Array.Empty<AgentRole>();

    /// <inheritdoc />
    public async Task<AgentResult> RunAsync(ISharedAssessmentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var skipReason = GetSkipReason(context);
        if (skipReason is not null)
        {
            context.LogWarning(Name, $"{Name} skipped: {skipReason}");
            var skipped = AgentResult.Skipped(Name, Role, skipReason);
            context.RecordResult(skipped);
            return skipped;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            context.LogInfo(Name, $"{Name} starting.");
            await ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var result = AgentResult.Succeeded(Name, Role, stopwatch.Elapsed);
            context.RecordResult(result);
            context.LogInfo(Name, $"{Name} completed in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var message = $"{ex.GetType().Name}: {ex.Message}";
            context.LogError(Name, $"{Name} failed: {message}");
            var result = AgentResult.Failed(Name, Role, message, stopwatch.Elapsed);
            context.RecordResult(result);
            return result;
        }
    }

    /// <summary>
    /// Returns a non-<see langword="null"/> reason to skip this agent (e.g. a required upstream output is
    /// missing), or <see langword="null"/> to run normally. Evaluated before <see cref="ExecuteAsync"/>.
    /// The default never skips.
    /// </summary>
    /// <param name="context">The shared context, used to inspect upstream outputs.</param>
    /// <returns>A skip reason, or <see langword="null"/> to proceed.</returns>
    protected virtual string? GetSkipReason(ISharedAssessmentContext context) => null;

    /// <summary>
    /// Performs the agent's domain work. Implementations should compute locally and commit their single
    /// domain output to <paramref name="context"/> as the final action. Throwing here is converted into a
    /// failed result by <see cref="RunAsync"/>; <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    /// <param name="context">The shared blackboard to read upstream outputs from and write this agent's output to.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>A task that completes when the agent's work is done.</returns>
    protected abstract Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken);
}
