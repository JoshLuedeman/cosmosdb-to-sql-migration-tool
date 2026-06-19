using CosmosToSqlAssessment.Models.Monitoring;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Builds deployable Azure Monitor alert-rule ARM templates for the migration custom metrics
/// (#223) and the orchestrating Data Factory pipelines (#70).
/// </summary>
/// <remarks>
/// <para>
/// Two templates are produced:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     A <c>Microsoft.Insights/metricAlerts</c> template covering threshold breaches
///     (static error-rate), error spikes (a dynamic-threshold criterion), low/no throughput,
///     and an optional Request-Units ceiling, all over the
///     <see cref="MigrationMonitoringService"/> custom metrics.
///     </description>
///   </item>
///   <item>
///     <description>
///     A <c>Microsoft.Insights/scheduledQueryRules</c> log-alert template that detects stalled
///     pipelines from the <c>ADFPipelineRun</c>/<c>ADFActivityRun</c> Log Analytics tables —
///     the reliable dead-pipeline detector, because metric alerts do not fire on
///     <em>missing</em> data.
///     </description>
///   </item>
/// </list>
/// <para>
/// Each <c>Build…</c> method returns a fully-formed ARM template object (<c>$schema</c>,
/// <c>contentVersion</c>, <c>parameters</c>, <c>resources</c>) intended to be serialized with
/// <see cref="DataFactory.AdfJsonSerializer"/>, which preserves dictionary keys such as
/// <c>odata.type</c> verbatim.
/// </para>
/// </remarks>
public sealed class AlertRuleTemplateBuilder
{
    /// <summary>ARM resource type for metric alert rules.</summary>
    public const string MetricAlertResourceType = "Microsoft.Insights/metricAlerts";

    /// <summary>API version used for the metric alert rules.</summary>
    public const string MetricAlertApiVersion = "2018-03-01";

    /// <summary>ARM resource type for scheduled query (log) alert rules.</summary>
    public const string ScheduledQueryRuleResourceType = "Microsoft.Insights/scheduledQueryRules";

    /// <summary>API version used for the scheduled query (log) alert rule.</summary>
    public const string ScheduledQueryRuleApiVersion = "2021-08-01";

    /// <summary><c>odata.type</c> for static-threshold metric criteria.</summary>
    public const string StaticCriteriaODataType = "Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria";

    /// <summary><c>odata.type</c> for dynamic-threshold metric criteria.</summary>
    public const string DynamicCriteriaODataType = "Microsoft.Azure.Monitor.MultipleResourceMultipleMetricCriteria";

    /// <summary>ARM template parameter: prefix applied to every generated alert-rule name.</summary>
    public const string ParamAlertNamePrefix = "alertNamePrefix";

    /// <summary>ARM template parameter: resource ID the metric alerts are scoped to.</summary>
    public const string ParamTargetResourceId = "targetResourceId";

    /// <summary>ARM template parameter: action-group resource ID alerts notify.</summary>
    public const string ParamActionGroupResourceId = "actionGroupResourceId";

    /// <summary>ARM template parameter: Log Analytics workspace resource ID the log alert queries.</summary>
    public const string ParamWorkspaceResourceId = "workspaceResourceId";

    private const string DeploymentSchema =
        "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#";
    private const string ContentVersion = "1.0.0.0";
    private const string DefaultAlertNamePrefix = "cosmos-migration";

    /// <summary>
    /// Builds the metric-alerts ARM template: static error-rate breach, dynamic error-spike,
    /// optional low-throughput, and optional Request-Units ceiling rules.
    /// </summary>
    /// <param name="options">Threshold/severity/window settings driving the generated rules.</param>
    /// <returns>An ARM template object ready to serialize.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public Dictionary<string, object?> BuildMetricAlertsTemplate(AlertRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resources = new List<object?>
        {
            BuildStaticMetricAlertResource(
                options,
                suffix: "error-rate",
                description: "Migration error rate exceeded the configured threshold over the evaluation window.",
                severity: options.ErrorRateSeverity,
                criterionName: "ErrorRateHigh",
                metricName: MigrationMonitoringService.MetricErrorRate,
                @operator: "GreaterThan",
                threshold: options.ErrorRateThreshold,
                timeAggregation: "Average"),
            BuildDynamicMetricAlertResource(
                options,
                suffix: "error-spike",
                description: "Migration error count deviated sharply from its recent baseline (dynamic threshold).",
                severity: options.ErrorSpikeSeverity,
                criterionName: "ErrorCountSpike",
                metricName: MigrationMonitoringService.MetricErrorCount,
                @operator: "GreaterThan",
                timeAggregation: "Total"),
        };

        if (options.IncludeLowThroughputAlert)
        {
            resources.Add(BuildStaticMetricAlertResource(
                options,
                suffix: "low-throughput",
                description: "Migration reported zero rows migrated while telemetry was flowing — a truly dead pipeline is caught by the stalled-pipeline log alert.",
                severity: options.LowThroughputSeverity,
                criterionName: "RowsMigratedLowOrZero",
                metricName: MigrationMonitoringService.MetricRowsMigrated,
                @operator: "LessThanOrEqual",
                threshold: 0,
                timeAggregation: "Total"));
        }

        if (options.IncludeRequestUnitsThresholdAlert)
        {
            resources.Add(BuildStaticMetricAlertResource(
                options,
                suffix: "request-units-high",
                description: "Migration Request-Units consumption exceeded the configured ceiling over the evaluation window.",
                severity: options.RequestUnitsSeverity,
                criterionName: "RequestUnitsHigh",
                metricName: MigrationMonitoringService.MetricRequestUnits,
                @operator: "GreaterThan",
                threshold: options.RequestUnitsThreshold,
                timeAggregation: "Total"));
        }

        var parameters = new Dictionary<string, object?>
        {
            [ParamAlertNamePrefix] = StringParameter(
                DefaultAlertNamePrefix,
                "Prefix applied to every generated alert-rule name."),
            [ParamTargetResourceId] = RequiredStringParameter(
                "Full resource ID the custom metrics are emitted against (e.g. the Data Factory or Cosmos account)."),
            [ParamActionGroupResourceId] = RequiredStringParameter(
                "Full resource ID of the action group notified when an alert fires."),
        };

        return new Dictionary<string, object?>
        {
            ["$schema"] = DeploymentSchema,
            ["contentVersion"] = ContentVersion,
            ["parameters"] = parameters,
            ["resources"] = resources,
        };
    }

    /// <summary>
    /// Builds the stalled-pipeline log-alert ARM template (a <c>scheduledQueryRules</c> rule
    /// over the ADF Log Analytics tables).
    /// </summary>
    /// <param name="options">Window/severity settings driving the generated rule.</param>
    /// <returns>An ARM template object ready to serialize.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public Dictionary<string, object?> BuildStalledPipelineLogAlertTemplate(AlertRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var criterion = new Dictionary<string, object?>
        {
            ["query"] = BuildStalledPipelineQuery(options.StalledActivityGap),
            ["timeAggregation"] = "Count",
            ["operator"] = "GreaterThan",
            ["threshold"] = 0,
            ["failingPeriods"] = new Dictionary<string, object?>
            {
                ["numberOfEvaluationPeriods"] = 1,
                ["minFailingPeriodsToAlert"] = 1,
            },
            ["dimensions"] = new List<object?>(),
        };

        var properties = new Dictionary<string, object?>
        {
            ["description"] = "An in-progress migration pipeline has shown no activity progress within the stall window — likely stalled or stuck.",
            ["severity"] = options.StalledPipelineSeverity,
            ["enabled"] = options.Enabled,
            ["evaluationFrequency"] = options.EvaluationFrequency,
            ["windowSize"] = options.StalledWindowSize,
            ["scopes"] = new List<object?> { $"[parameters('{ParamWorkspaceResourceId}')]" },
            ["criteria"] = new Dictionary<string, object?>
            {
                ["allOf"] = new List<object?> { criterion },
            },
            // scheduledQueryRules expects actionGroups as an array of resource-id STRINGS.
            ["actions"] = new Dictionary<string, object?>
            {
                ["actionGroups"] = new List<object?> { $"[parameters('{ParamActionGroupResourceId}')]" },
            },
            ["autoMitigate"] = true,
            ["skipQueryValidation"] = options.SkipQueryValidation,
        };

        var resource = new Dictionary<string, object?>
        {
            ["type"] = ScheduledQueryRuleResourceType,
            ["apiVersion"] = ScheduledQueryRuleApiVersion,
            ["name"] = $"[concat(parameters('{ParamAlertNamePrefix}'), '-stalled-pipeline')]",
            ["location"] = "[resourceGroup().location]",
            ["kind"] = "LogAlert",
            ["properties"] = properties,
        };

        var parameters = new Dictionary<string, object?>
        {
            [ParamAlertNamePrefix] = StringParameter(
                DefaultAlertNamePrefix,
                "Prefix applied to the generated alert-rule name."),
            [ParamWorkspaceResourceId] = RequiredStringParameter(
                "Full resource ID of the Log Analytics workspace receiving ADF diagnostic logs."),
            [ParamActionGroupResourceId] = RequiredStringParameter(
                "Full resource ID of the action group notified when the alert fires."),
        };

        return new Dictionary<string, object?>
        {
            ["$schema"] = DeploymentSchema,
            ["contentVersion"] = ContentVersion,
            ["parameters"] = parameters,
            ["resources"] = new List<object?> { resource },
        };
    }

    private static Dictionary<string, object?> BuildStaticMetricAlertResource(
        AlertRuleOptions options,
        string suffix,
        string description,
        int severity,
        string criterionName,
        string metricName,
        string @operator,
        double threshold,
        string timeAggregation)
    {
        var criterion = new Dictionary<string, object?>
        {
            ["name"] = criterionName,
            ["metricName"] = metricName,
            ["metricNamespace"] = options.MetricNamespace,
            ["operator"] = @operator,
            ["threshold"] = threshold,
            ["timeAggregation"] = timeAggregation,
            ["criterionType"] = "StaticThresholdCriterion",
            ["dimensions"] = new List<object?>(),
            ["skipMetricValidation"] = options.SkipMetricValidation,
        };

        var criteria = new Dictionary<string, object?>
        {
            ["odata.type"] = StaticCriteriaODataType,
            ["allOf"] = new List<object?> { criterion },
        };

        return BuildMetricAlertResource(options, suffix, description, severity, criteria);
    }

    private static Dictionary<string, object?> BuildDynamicMetricAlertResource(
        AlertRuleOptions options,
        string suffix,
        string description,
        int severity,
        string criterionName,
        string metricName,
        string @operator,
        string timeAggregation)
    {
        var criterion = new Dictionary<string, object?>
        {
            ["name"] = criterionName,
            ["metricName"] = metricName,
            ["metricNamespace"] = options.MetricNamespace,
            ["operator"] = @operator,
            ["alertSensitivity"] = options.ErrorSpikeSensitivity,
            ["failingPeriods"] = new Dictionary<string, object?>
            {
                ["numberOfEvaluationPeriods"] = options.ErrorSpikeNumberOfEvaluationPeriods,
                ["minFailingPeriodsToAlert"] = options.ErrorSpikeMinFailingPeriods,
            },
            ["timeAggregation"] = timeAggregation,
            ["criterionType"] = "DynamicThresholdCriterion",
            ["dimensions"] = new List<object?>(),
            ["skipMetricValidation"] = options.SkipMetricValidation,
        };

        var criteria = new Dictionary<string, object?>
        {
            ["odata.type"] = DynamicCriteriaODataType,
            ["allOf"] = new List<object?> { criterion },
        };

        return BuildMetricAlertResource(options, suffix, description, severity, criteria);
    }

    private static Dictionary<string, object?> BuildMetricAlertResource(
        AlertRuleOptions options,
        string suffix,
        string description,
        int severity,
        Dictionary<string, object?> criteria)
    {
        var properties = new Dictionary<string, object?>
        {
            ["description"] = description,
            ["severity"] = severity,
            ["enabled"] = options.Enabled,
            ["scopes"] = new List<object?> { $"[parameters('{ParamTargetResourceId}')]" },
            ["evaluationFrequency"] = options.EvaluationFrequency,
            ["windowSize"] = options.WindowSize,
            ["criteria"] = criteria,
            ["autoMitigate"] = true,
            ["actions"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["actionGroupId"] = $"[parameters('{ParamActionGroupResourceId}')]",
                },
            },
        };

        return new Dictionary<string, object?>
        {
            ["type"] = MetricAlertResourceType,
            ["apiVersion"] = MetricAlertApiVersion,
            ["name"] = $"[concat(parameters('{ParamAlertNamePrefix}'), '-{suffix}')]",
            ["location"] = "global",
            ["properties"] = properties,
        };
    }

    private static string BuildStalledPipelineQuery(string activityGap) =>
$$"""
// Migration pipelines still running but with no activity progress in the last {{activityGap}}.
ADFPipelineRun
| where Status == "InProgress"
| join kind=leftouter (
    ADFActivityRun
    | where Status in ("Succeeded", "InProgress")
    | summarize LastActivity = max(TimeGenerated) by RunId
  ) on RunId
| extend LastActivity = coalesce(LastActivity, Start)
| where LastActivity < ago({{activityGap}})
| project PipelineName, RunId, Status, LastActivity
""";

    private static Dictionary<string, object?> StringParameter(string defaultValue, string description) =>
        new()
        {
            ["type"] = "string",
            ["defaultValue"] = defaultValue,
            ["metadata"] = new Dictionary<string, object?> { ["description"] = description },
        };

    private static Dictionary<string, object?> RequiredStringParameter(string description) =>
        new()
        {
            ["type"] = "string",
            ["metadata"] = new Dictionary<string, object?> { ["description"] = description },
        };
}
