namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Emits a stand-alone, deployable ARM template containing a
/// <c>Microsoft.Insights/diagnosticSettings</c> resource scoped to the target
/// Data Factory. Sets <c>logAnalyticsDestinationType = "Dedicated"</c> so logs
/// land in the resource-specific <c>ADFPipelineRun</c> / <c>ADFActivityRun</c>
/// tables (matching the bundled KQL queries) rather than the legacy
/// <c>AzureDiagnostics</c> table.
/// </summary>
public sealed class DiagnosticSettingsTemplateBuilder
{
    /// <summary>Azure resource type for the diagnostic settings extension resource deployed against the factory.</summary>
    public const string ResourceType = "Microsoft.Insights/diagnosticSettings";
    /// <summary>Azure API version for <c>Microsoft.Insights/diagnosticSettings</c> resources targeted by the emitted template.</summary>
    public const string ApiVersion = "2021-05-01-preview";

    /// <summary>
    /// Microsoft-documented ADF diagnostic log categories. Unknown values are rejected
    /// at generation time so deployment never fails with an opaque ARM error.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownLogCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "PipelineRuns",
        "ActivityRuns",
        "TriggerRuns",
        "SandboxPipelineRuns",
        "SandboxActivityRuns",
        "SSISPackageEventMessages",
        "SSISPackageExecutableStatistics",
        "SSISPackageEventMessageContext",
        "SSISPackageExecutionComponentPhaseStatistics",
        "SSISPackageExecutionDataStatistics",
        "SSISIntegrationRuntimeLogs",
    };

    /// <summary>
    /// Ordered subset of <see cref="KnownLogCategories"/> enabled by default: <c>PipelineRuns</c>,
    /// <c>ActivityRuns</c>, and <c>TriggerRuns</c>. These three cover the full pipeline-execution
    /// audit trail needed by the bundled KQL cheat-sheet queries.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultLogCategories = new[]
    {
        "PipelineRuns",
        "ActivityRuns",
        "TriggerRuns",
    };

    /// <summary>
    /// Builds the full ARM template (with <c>$schema</c> / <c>contentVersion</c> /
    /// <c>parameters</c> / <c>resources</c>) so the file is deployable as-is via
    /// <c>az deployment group create --template-file …</c>.
    /// </summary>
    public Dictionary<string, object?> Build(MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var categories = options.LogCategories is { Count: > 0 }
            ? options.LogCategories
            : DefaultLogCategories;

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category) || !KnownLogCategories.Contains(category))
            {
                throw new ArgumentException(
                    $"Unknown ADF diagnostic log category '{category}'. Allowed values: {string.Join(", ", KnownLogCategories)}.",
                    nameof(options));
            }
        }

        var logs = categories
            .Select(cat => (object?)new Dictionary<string, object?>
            {
                ["category"] = cat,
                ["enabled"] = true,
            })
            .ToList();

        var metrics = new List<object?>();
        if (options.EmitAllMetrics)
        {
            metrics.Add(new Dictionary<string, object?>
            {
                ["category"] = "AllMetrics",
                ["enabled"] = true,
            });
        }

        var properties = new Dictionary<string, object?>
        {
            ["workspaceId"] = $"[parameters('{ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId}')]",
            // Dedicated mode routes logs to ADFPipelineRun / ADFActivityRun /
            // ADFTriggerRun tables in Log Analytics — required for the bundled KQL.
            ["logAnalyticsDestinationType"] = "Dedicated",
            ["logs"] = logs,
        };
        if (metrics.Count > 0)
        {
            properties["metrics"] = metrics;
        }

        var resource = new Dictionary<string, object?>
        {
            ["type"] = ResourceType,
            ["apiVersion"] = ApiVersion,
            ["name"] = $"[parameters('{ParameterCatalog.MonitoringParamDiagnosticSettingName}')]",
            // Extension resource targeting the factory by ID. resourceId() with a single
            // pair resolves in the deployment's resource group; cross-RG operators can
            // edit the scope expression to use a full resource ID.
            ["scope"] = $"[resourceId('Microsoft.DataFactory/factories', parameters('{ParameterCatalog.MonitoringParamDataFactoryName}'))]",
            // dependsOn is critical when the same template also creates the factory
            // (parent #146 will stitch this resource in alongside the factory resource).
            // Against a pre-existing factory this is a harmless no-op.
            ["dependsOn"] = new List<object?>
            {
                $"[resourceId('Microsoft.DataFactory/factories', parameters('{ParameterCatalog.MonitoringParamDataFactoryName}'))]",
            },
            ["properties"] = properties,
        };

        var parameters = new Dictionary<string, object?>
        {
            [ParameterCatalog.MonitoringParamDataFactoryName] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["description"] = "Name of the Azure Data Factory the diagnostic setting attaches to.",
                },
            },
            [ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["description"] = "Full resource ID of the Log Analytics workspace, e.g. /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.OperationalInsights/workspaces/<ws>.",
                },
            },
            [ParameterCatalog.MonitoringParamDiagnosticSettingName] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["defaultValue"] = options.DiagnosticSettingName,
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["description"] = "Name of the diagnostic setting attached to the factory.",
                },
            },
        };

        return new Dictionary<string, object?>
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = parameters,
            ["resources"] = new List<object?> { resource },
        };
    }

    /// <summary>
    /// Default KQL companion file. Aligned with Dedicated-mode column names
    /// (<c>UserProperties</c> capitalised, parsed as JSON).
    /// </summary>
    public string BuildKqlCheatsheet() =>
"""
// Cosmos → SQL migration: Log Analytics KQL cheat-sheet
// -----------------------------------------------------
// Requires the ADF diagnostic setting to use logAnalyticsDestinationType = "Dedicated"
// so logs land in the resource-specific ADFPipelineRun / ADFActivityRun / ADFTriggerRun
// tables. AzureDiagnostics-based queries are NOT covered here.

// 1) Active migration runs in the last 24 h.
ADFPipelineRun
| where TimeGenerated > ago(24h) and PipelineName startswith "Migrate_"
| project Start, End, Status, PipelineName, RunId, Message = ErrorMessage
| order by Start desc

// 2) Slow / failed Copy activities by target table (last 7 days).
ADFActivityRun
| where TimeGenerated > ago(7d) and ActivityType == "Copy"
| extend props = parse_json(UserProperties)
| project Start, End, Status, ActivityName, RunId,
          SourceDatabase  = tostring(props.SourceDatabase),
          SourceContainer = tostring(props.SourceContainer),
          TargetSchema    = tostring(props.TargetSchema),
          TargetTable     = tostring(props.TargetTable),
          DurationInMs, ErrorMessage
| order by DurationInMs desc

// 3) Failure rate per source database (last 24 h).
ADFActivityRun
| where TimeGenerated > ago(24h) and ActivityType == "Copy"
| extend props = parse_json(UserProperties)
| summarize total = count(), failed = countif(Status == "Failed")
            by SourceDatabase = tostring(props.SourceDatabase)
| extend failure_pct = round(100.0 * failed / total, 2)
| order by failure_pct desc

// 4) Rows-read vs rows-written per Copy activity (after diagnosis Output JSON parsing).
ADFActivityRun
| where TimeGenerated > ago(24h) and ActivityType == "Copy"
| extend out = parse_json(Output)
| extend props = parse_json(UserProperties)
| project Start, ActivityName,
          TargetTable = tostring(props.TargetTable),
          rowsRead    = tolong(out.rowsRead),
          rowsCopied  = tolong(out.rowsCopied),
          dataRead    = tolong(out.dataRead),
          dataWritten = tolong(out.dataWritten),
          duration_s  = DurationInMs / 1000
| where isnotnull(rowsRead)
| order by Start desc
""";
}
