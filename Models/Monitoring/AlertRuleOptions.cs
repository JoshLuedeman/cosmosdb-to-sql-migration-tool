using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// Strongly-typed options controlling the Azure Monitor alert-rule ARM templates emitted
/// for the migration custom metrics (#223) and the orchestrating Data Factory pipelines
/// (#70). Bound from the <c>AzureMonitor:Alerts</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The templates are deploy-time artifacts: they describe <c>Microsoft.Insights/metricAlerts</c>
/// rules over the <see cref="MetricNamespace"/> custom metrics plus a
/// <c>Microsoft.Insights/scheduledQueryRules</c> log alert over the
/// <c>ADFPipelineRun</c>/<c>ADFActivityRun</c> Log Analytics tables. None of these settings
/// trigger a live Azure call — they only shape the generated JSON.
/// </para>
/// <para>
/// Threshold and severity defaults are conservative starting points an operator is expected
/// to tune; every value is surfaced as an ARM template parameter default so the emitted
/// templates remain deployable and overridable without regeneration.
/// </para>
/// </remarks>
public sealed class AlertRuleOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "AzureMonitor:Alerts";

    /// <summary>
    /// Custom-metric namespace the metric alerts target. Defaults to
    /// <see cref="AzureMonitorMetricOptions.DefaultMetricNamespace"/> so it matches the
    /// metrics published by <see cref="MigrationMonitoringService"/>.
    /// </summary>
    public string MetricNamespace { get; set; } = AzureMonitorMetricOptions.DefaultMetricNamespace;

    /// <summary>
    /// Whether the generated alert rules are created in the <c>enabled</c> state. Defaults to
    /// <c>true</c> so a deployed template begins evaluating immediately.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ISO-8601 duration for how often the metric alerts are evaluated. Defaults to
    /// <c>PT1M</c> (every minute).
    /// </summary>
    public string EvaluationFrequency { get; set; } = "PT1M";

    /// <summary>
    /// ISO-8601 lookback window each metric-alert evaluation aggregates over. Defaults to
    /// <c>PT5M</c>.
    /// </summary>
    public string WindowSize { get; set; } = "PT5M";

    /// <summary>
    /// ISO-8601 lookback window for the stalled-pipeline log alert. Defaults to <c>PT15M</c>,
    /// longer than <see cref="WindowSize"/> because a stalled pipeline is defined by the
    /// <em>absence</em> of progress over a sustained period.
    /// </summary>
    public string StalledWindowSize { get; set; } = "PT15M";

    /// <summary>
    /// KQL relative-time literal (e.g. <c>15m</c>) used inside the stalled-pipeline query's
    /// <c>ago(...)</c> activity-gap filter. Kept distinct from <see cref="StalledWindowSize"/>
    /// because KQL uses Timespan literals, not ISO-8601 durations.
    /// </summary>
    public string StalledActivityGap { get; set; } = "15m";

    /// <summary>Error-rate fraction (0-1) above which the static error-rate alert fires. Defaults to <c>0.05</c>.</summary>
    public double ErrorRateThreshold { get; set; } = 0.05;

    /// <summary>Severity (0 = critical … 4 = verbose) of the error-rate alert. Defaults to <c>2</c>.</summary>
    public int ErrorRateSeverity { get; set; } = 2;

    /// <summary>
    /// Dynamic-threshold sensitivity for the error-spike alert: <c>Low</c>, <c>Medium</c>, or
    /// <c>High</c>. Defaults to <c>Medium</c>.
    /// </summary>
    public string ErrorSpikeSensitivity { get; set; } = "Medium";

    /// <summary>Number of recent evaluation periods the dynamic error-spike alert inspects. Defaults to <c>4</c>.</summary>
    public int ErrorSpikeNumberOfEvaluationPeriods { get; set; } = 4;

    /// <summary>How many of <see cref="ErrorSpikeNumberOfEvaluationPeriods"/> must breach before the error-spike alert fires. Defaults to <c>3</c>.</summary>
    public int ErrorSpikeMinFailingPeriods { get; set; } = 3;

    /// <summary>Severity of the dynamic error-spike alert. Defaults to <c>2</c>.</summary>
    public int ErrorSpikeSeverity { get; set; } = 2;

    /// <summary>
    /// Whether to emit the low/no-throughput metric alert. Defaults to <c>true</c>. Note this
    /// alert fires on observed-but-zero throughput while telemetry is flowing; a truly dead
    /// pipeline that stops emitting is caught by the stalled-pipeline log alert instead.
    /// </summary>
    public bool IncludeLowThroughputAlert { get; set; } = true;

    /// <summary>Severity of the low-throughput alert. Defaults to <c>1</c>.</summary>
    public int LowThroughputSeverity { get; set; } = 1;

    /// <summary>Whether to emit the optional Request-Units ceiling alert. Off by default.</summary>
    public bool IncludeRequestUnitsThresholdAlert { get; set; }

    /// <summary>RU/s value above which the optional Request-Units alert fires (used only when <see cref="IncludeRequestUnitsThresholdAlert"/> is <c>true</c>).</summary>
    public double RequestUnitsThreshold { get; set; }

    /// <summary>Severity of the optional Request-Units alert. Defaults to <c>3</c>.</summary>
    public int RequestUnitsSeverity { get; set; } = 3;

    /// <summary>Severity of the stalled-pipeline log alert. Defaults to <c>1</c>.</summary>
    public int StalledPipelineSeverity { get; set; } = 1;

    /// <summary>
    /// Emits <c>skipMetricValidation = true</c> on each custom-metric criterion so a template
    /// deploys before the metrics have been ingested for the first time. Defaults to <c>true</c>.
    /// </summary>
    public bool SkipMetricValidation { get; set; } = true;

    /// <summary>
    /// Emits <c>skipQueryValidation = true</c> on the scheduled query rule so it deploys even
    /// when the target Log Analytics tables are not yet populated. Defaults to <c>true</c>.
    /// </summary>
    public bool SkipQueryValidation { get; set; } = true;
}
