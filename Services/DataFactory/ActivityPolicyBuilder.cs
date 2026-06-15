namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the ADF <c>policy</c> block for an activity (#143). Pure / side-effect free —
/// returns a serialiser-ready dictionary which the orchestrator and copy-activity builder
/// stash on <see cref="Models.DataFactory.PipelineActivity.AdditionalProperties"/>.
/// </summary>
public static class ActivityPolicyBuilder
{
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

    public static Dictionary<string, object?> ForExecutePipeline(ExecutePipelinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Build(policy.Timeout, policy.Retry, policy.RetryIntervalInSeconds, policy.SecureInput, policy.SecureOutput);
    }

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
