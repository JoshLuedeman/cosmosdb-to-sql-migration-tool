namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the ADF <c>policy</c> block for an activity (#143). Pure / side-effect free —
/// returns a serialiser-ready dictionary which the orchestrator and copy-activity builder
/// stash on <see cref="Models.DataFactory.PipelineActivity.AdditionalProperties"/>.
/// </summary>
public static class ActivityPolicyBuilder
{
    /// <summary>
    /// Emits the ADF <c>policy</c> block for a Copy activity, resolving the retry count from
    /// <see cref="CopyActivityPolicy.Retry"/> or deriving it from <paramref name="writeBehavior"/>
    /// (0 for non-idempotent <c>Insert</c>, 3 for <c>Upsert</c>).
    /// </summary>
    /// <param name="policy">Copy-activity policy options (timeout, retry interval, secure flags).</param>
    /// <param name="writeBehavior">Sink write behaviour used to derive a safe retry default when <see cref="CopyActivityPolicy.Retry"/> is <c>null</c>.</param>
    /// <returns>A serializer-ready dictionary representing the ADF <c>policy</c> JSON object.</returns>
    public static Dictionary<string, object?> ForCopyActivity(
        CopyActivityPolicy policy,
        SinkWriteBehavior writeBehavior)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Insert is non-idempotent — a partial-write + retry can produce PK collisions or
        // duplicate rows. Default to no retries unless the operator explicitly opts in.
        var resolvedRetry = policy.Retry ?? (writeBehavior == SinkWriteBehavior.Upsert ? 3 : 0);
        return Build(policy.Timeout, resolvedRetry, policy.RetryIntervalInSeconds, policy.SecureInput, policy.SecureOutput);
    }

    /// <summary>
    /// Emits the ADF <c>policy</c> block for an <c>ExecutePipeline</c> activity using the
    /// provided <paramref name="policy"/> without retry-count derivation.
    /// </summary>
    /// <param name="policy">Execute-pipeline policy options (timeout, retry count, retry interval, secure flags).</param>
    /// <returns>A serializer-ready dictionary representing the ADF <c>policy</c> JSON object.</returns>
    public static Dictionary<string, object?> ForExecutePipeline(ExecutePipelinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Build(policy.Timeout, policy.Retry, policy.RetryIntervalInSeconds, policy.SecureInput, policy.SecureOutput);
    }

    /// <summary>
    /// Emits the ADF <c>policy</c> block for a <c>Web</c> (failure-notification) activity.
    /// Defaults to <c>secureInput</c> and <c>secureOutput</c> so the webhook URL never
    /// enters ADF run history.
    /// </summary>
    /// <param name="timeout">Activity timeout in <c>HH:MM:SS</c> format. Default <c>"00:05:00"</c>.</param>
    /// <param name="retry">Retry count. Default <c>0</c> — if the webhook is down, re-throwing is more useful than queuing retries.</param>
    /// <param name="retryIntervalInSeconds">Seconds between retries. Default <c>30</c>.</param>
    /// <param name="secureInput">Redact inputs from run history. Default <c>true</c>.</param>
    /// <param name="secureOutput">Redact outputs from run history. Default <c>true</c>.</param>
    /// <returns>A serializer-ready dictionary representing the ADF <c>policy</c> JSON object.</returns>
    public static Dictionary<string, object?> ForWebActivity(
        string timeout = "00:05:00",
        int retry = 0,
        int retryIntervalInSeconds = 30,
        bool secureInput = true,
        bool secureOutput = true)
    {
        return Build(timeout, retry, retryIntervalInSeconds, secureInput, secureOutput);
    }

    private static Dictionary<string, object?> Build(string timeout, int retry, int retryIntervalInSeconds, bool secureInput, bool secureOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeout);
        if (retry < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retry), retry, "ADF retry must be ≥ 0.");
        }
        if (retryIntervalInSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryIntervalInSeconds), retryIntervalInSeconds, "Retry interval must be ≥ 0.");
        }

        return new Dictionary<string, object?>
        {
            ["timeout"] = timeout,
            ["retry"] = retry,
            ["retryIntervalInSeconds"] = retryIntervalInSeconds,
            ["secureInput"] = secureInput,
            ["secureOutput"] = secureOutput,
        };
    }
}
