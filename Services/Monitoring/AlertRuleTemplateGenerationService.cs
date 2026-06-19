using System.Text;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.DataFactory;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Result of an <see cref="AlertRuleTemplateGenerationService.GenerateAsync"/> call: the
/// absolute paths of every artifact written, plus any non-fatal warnings.
/// </summary>
public sealed class AlertRuleGenerationResult
{
    /// <summary>Absolute path of the emitted metric-alerts ARM template.</summary>
    public required string MetricAlertsTemplatePath { get; init; }

    /// <summary>Absolute path of the emitted stalled-pipeline log-alert ARM template.</summary>
    public required string StalledPipelineTemplatePath { get; init; }

    /// <summary>Absolute path of the emitted README describing the alert rules and how to deploy them.</summary>
    public required string ReadmePath { get; init; }

    /// <summary>Non-fatal warnings raised while generating the templates.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Writes the Azure Monitor alert-rule ARM templates (built by
/// <see cref="AlertRuleTemplateBuilder"/>) and a companion README into a
/// <c>Monitoring/AlertRules</c> folder under a caller-supplied output directory.
/// </summary>
/// <remarks>
/// Templates are serialized with <see cref="AdfJsonSerializer"/> so they are deterministic,
/// indented, and preserve dictionary keys such as <c>odata.type</c>. Generation is purely
/// local file I/O — it performs no Azure calls.
/// </remarks>
public sealed class AlertRuleTemplateGenerationService
{
    /// <summary>Sub-folder (under the output directory) the alert-rule artifacts are written to.</summary>
    public const string AlertRulesFolder = "Monitoring/AlertRules";

    /// <summary>File name of the metric-alerts ARM template.</summary>
    public const string MetricAlertsTemplateFileName = "metric-alerts.template.json";

    /// <summary>File name of the stalled-pipeline log-alert ARM template.</summary>
    public const string StalledPipelineTemplateFileName = "stalled-pipeline-log-alert.template.json";

    /// <summary>File name of the README describing the generated alert rules.</summary>
    public const string ReadmeFileName = "README.md";

    private readonly AlertRuleTemplateBuilder _builder;
    private readonly AlertRuleOptions _options;
    private readonly ILogger<AlertRuleTemplateGenerationService> _logger;

    /// <summary>
    /// Creates the alert-rule template generation service.
    /// </summary>
    /// <param name="builder">Builder that produces the ARM template objects.</param>
    /// <param name="options">Threshold/severity/window settings driving the templates.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public AlertRuleTemplateGenerationService(
        AlertRuleTemplateBuilder builder,
        AlertRuleOptions options,
        ILogger<AlertRuleTemplateGenerationService> logger)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates the alert-rule templates and README under
    /// <paramref name="outputDirectory"/>/<see cref="AlertRulesFolder"/>.
    /// </summary>
    /// <param name="outputDirectory">Root output directory; the alert-rules sub-folder is created beneath it.</param>
    /// <param name="cancellationToken">Token to cancel the file writes.</param>
    /// <returns>The paths of the written artifacts plus any warnings.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is null or whitespace.</exception>
    public async Task<AlertRuleGenerationResult> GenerateAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory must be provided.", nameof(outputDirectory));
        }

        var warnings = new List<string>();
        if (_options.IncludeRequestUnitsThresholdAlert && _options.RequestUnitsThreshold <= 0)
        {
            warnings.Add(
                "Request-Units alert is enabled but RequestUnitsThreshold is <= 0; the generated rule would fire continuously. Set a positive threshold before deploying.");
        }

        var targetDir = Path.Combine(outputDirectory, AlertRulesFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(targetDir);

        var metricAlertsTemplate = _builder.BuildMetricAlertsTemplate(_options);
        var stalledTemplate = _builder.BuildStalledPipelineLogAlertTemplate(_options);

        var metricAlertsPath = Path.Combine(targetDir, MetricAlertsTemplateFileName);
        var stalledPath = Path.Combine(targetDir, StalledPipelineTemplateFileName);
        var readmePath = Path.Combine(targetDir, ReadmeFileName);

        await File.WriteAllTextAsync(
            metricAlertsPath,
            AdfJsonSerializer.Serialize(metricAlertsTemplate),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            stalledPath,
            AdfJsonSerializer.Serialize(stalledTemplate),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            readmePath,
            BuildReadme(),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Generated Azure Monitor alert-rule templates in {Directory} ({WarningCount} warning(s)).",
            targetDir,
            warnings.Count);

        return new AlertRuleGenerationResult
        {
            MetricAlertsTemplatePath = metricAlertsPath,
            StalledPipelineTemplatePath = stalledPath,
            ReadmePath = readmePath,
            Warnings = warnings,
        };
    }

    private string BuildReadme()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Migration alert rules");
        sb.AppendLine();
        sb.AppendLine("Deployable Azure Monitor alert rules for the Cosmos DB → SQL migration. The metric");
        sb.AppendLine($"alerts target the `{_options.MetricNamespace}` custom-metric namespace emitted by the");
        sb.AppendLine("monitoring service; the log alert reads the ADF diagnostic tables in Log Analytics.");
        sb.AppendLine();
        sb.AppendLine($"## `{MetricAlertsTemplateFileName}`");
        sb.AppendLine();
        sb.AppendLine("`Microsoft.Insights/metricAlerts` rules:");
        sb.AppendLine();
        sb.AppendLine($"- **Error rate high** (static, severity {_options.ErrorRateSeverity}) — `{MigrationMonitoringService.MetricErrorRate}` average `> {_options.ErrorRateThreshold}`.");
        sb.AppendLine($"- **Error spike** (dynamic, severity {_options.ErrorSpikeSeverity}) — `{MigrationMonitoringService.MetricErrorCount}` deviates from baseline (`{_options.ErrorSpikeSensitivity}` sensitivity, {_options.ErrorSpikeMinFailingPeriods}/{_options.ErrorSpikeNumberOfEvaluationPeriods} periods).");
        if (_options.IncludeLowThroughputAlert)
        {
            sb.AppendLine($"- **Low throughput** (static, severity {_options.LowThroughputSeverity}) — `{MigrationMonitoringService.MetricRowsMigrated}` total `<= 0` while telemetry is flowing.");
        }

        if (_options.IncludeRequestUnitsThresholdAlert)
        {
            sb.AppendLine($"- **Request Units high** (static, severity {_options.RequestUnitsSeverity}) — `{MigrationMonitoringService.MetricRequestUnits}` total `> {_options.RequestUnitsThreshold}`.");
        }

        sb.AppendLine();
        sb.AppendLine("> Metric alerts do **not** fire on *missing* data, so a pipeline that stops emitting");
        sb.AppendLine("> entirely is detected by the stalled-pipeline log alert below, not by the");
        sb.AppendLine("> low-throughput metric alert. `skipMetricValidation` is set so the template deploys");
        sb.AppendLine("> before the custom metrics are first ingested.");
        sb.AppendLine();
        sb.AppendLine("Deploy:");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine($"az deployment group create --resource-group <rg> --template-file {MetricAlertsTemplateFileName} \\");
        sb.AppendLine($"  --parameters {AlertRuleTemplateBuilder.ParamTargetResourceId}=<resourceId> {AlertRuleTemplateBuilder.ParamActionGroupResourceId}=<actionGroupId>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"## `{StalledPipelineTemplateFileName}`");
        sb.AppendLine();
        sb.AppendLine($"`Microsoft.Insights/scheduledQueryRules` log alert (severity {_options.StalledPipelineSeverity}) that flags in-progress");
        sb.AppendLine($"pipelines with no activity progress in the last `{_options.StalledActivityGap}`. This is the reliable");
        sb.AppendLine("dead-pipeline detector. `skipQueryValidation` is set so it deploys before the ADF");
        sb.AppendLine("tables are populated.");
        sb.AppendLine();
        sb.AppendLine("Deploy:");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine($"az deployment group create --resource-group <rg> --template-file {StalledPipelineTemplateFileName} \\");
        sb.AppendLine($"  --parameters {AlertRuleTemplateBuilder.ParamWorkspaceResourceId}=<workspaceId> {AlertRuleTemplateBuilder.ParamActionGroupResourceId}=<actionGroupId>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Adjust thresholds, severities, and evaluation windows via the `parameters` block or by");
        sb.AppendLine("re-running generation with a tuned `AzureMonitor:Alerts` configuration section.");
        return sb.ToString();
    }
}
