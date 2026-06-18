using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the <c>Web</c> + <c>Fail</c> activity pair that fires when an upstream activity
/// fails (#143). The Web activity POSTs to a parameterised webhook (Logic App / Teams /
/// PagerDuty / etc) with the failed activity's error message, and a subsequent <c>Fail</c>
/// activity re-throws so the pipeline still reports failure to its caller. The Web activity
/// has <c>policy.secureInput/secureOutput</c> set so the webhook URL never enters run history.
/// </summary>
public sealed class FailureNotificationBuilder
{
    /// <summary>
    /// Emit (webActivity, failActivity) for a single upstream activity. Caller appends both
    /// to the pipeline's activity list; the orchestrator does not need to worry about ordering
    /// because <c>dependsOn</c> drives execution order in ADF.
    /// </summary>
    public NotificationPair Build(
        PipelineActivity upstream,
        string pipelineRole,
        AdfNameRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(registry);

        var webName = registry.Allocate(
            $"Notify_{upstream.Name}_Failed",
            $"activity|notify|{upstream.Name}");
        var failName = registry.Allocate(
            $"Fail_{upstream.Name}",
            $"activity|fail|{upstream.Name}");

        // ADF body must be a valid JSON value or an Expression resolving to one. We pass an
        // Expression that calls @json(@concat(...)) so error.message escaping is left to ADF.
        var bodyExpr =
            "@json(concat('{\"pipeline\":\"', pipeline().Pipeline, '\",\"runId\":\"', pipeline().RunId, '\",\"activity\":\"" +
            upstream.Name + "\",\"role\":\"" + pipelineRole +
            "\",\"errorMessage\":', json(concat('\"', replace(coalesce(activity('" + upstream.Name +
            "').error.message, ''), '\"', '\\\\\"'), '\"')), '}'))";

        var web = new PipelineActivity
        {
            Name = webName,
            Type = "WebActivity",
            TypeProperties =
            {
                ["url"] = "@pipeline().parameters." + ParameterCatalog.PipelineParamFailureNotificationWebhookUrl,
                ["method"] = "POST",
                ["body"] = new Dictionary<string, object?>
                {
                    ["value"] = bodyExpr,
                    ["type"] = "Expression",
                },
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Content-Type"] = "application/json",
                },
                ["httpRequestTimeout"] = "00:01:00",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["policy"] = ActivityPolicyBuilder.ForWebActivity(),
                ["dependsOn"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["activity"] = upstream.Name,
                        ["dependencyConditions"] = new[] { "Failed" },
                    },
                },
            },
            Annotations = new List<string>
            {
                $"Failure notification webhook for '{upstream.Name}'.",
            },
        };

        // The Fail activity re-throws so the parent pipeline reports the failure correctly,
        // rather than being marked Succeeded after the webhook fires.
        var fail = new PipelineActivity
        {
            Name = failName,
            Type = "Fail",
            TypeProperties =
            {
                ["message"] = new Dictionary<string, object?>
                {
                    ["value"] = "@concat('Activity " + upstream.Name + " failed: ', coalesce(activity('" + upstream.Name + "').error.message, ''))",
                    ["type"] = "Expression",
                },
                ["errorCode"] = $"NotifiedFailure_{upstream.Name}",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["dependsOn"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["activity"] = webName,
                        // Run Fail whether the webhook itself succeeded or failed — the
                        // important thing is to surface the original failure to the caller.
                        ["dependencyConditions"] = new[] { "Completed" },
                    },
                },
            },
            Annotations = new List<string>
            {
                $"Re-throws upstream failure of '{upstream.Name}' after notification.",
            },
        };

        return new NotificationPair(web, fail);
    }

    /// <summary>
    /// Pairs the <c>Web</c> (webhook POST) and <c>Fail</c> (re-throw) activities emitted
    /// by <see cref="FailureNotificationBuilder.Build"/> for a single upstream activity failure.
    /// </summary>
    public readonly record struct NotificationPair(PipelineActivity Web, PipelineActivity Fail);
}
