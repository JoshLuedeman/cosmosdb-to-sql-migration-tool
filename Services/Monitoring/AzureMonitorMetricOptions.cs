namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Strongly-typed options for publishing migration progress metrics to Azure Monitor as
/// custom metrics. Bound from the <c>AzureMonitor:Metrics</c> configuration section.
/// </summary>
/// <remarks>
/// Metric ingestion is a distinct write surface from the Azure Monitor Query SDK used for
/// reading metrics (#76); it shares the same credential and the <c>AzureMonitor</c>
/// configuration root but uses these write-specific settings. <see cref="Enabled"/>
/// defaults to <c>false</c> so offline / CI runs never attempt a live call.
/// </remarks>
public sealed class AzureMonitorMetricOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "AzureMonitor:Metrics";

    /// <summary>Default custom-metric namespace when none is configured.</summary>
    public const string DefaultMetricNamespace = "CosmosToSqlMigration";

    /// <summary>
    /// When <c>true</c>, the publisher POSTs metrics to Azure Monitor. When <c>false</c>
    /// (the default) the publisher is a no-op, which keeps offline and CI runs side-effect free.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Azure region of the target resource (e.g. <c>eastus</c>). Combined with
    /// <see cref="ResourceId"/> to form the regional ingestion endpoint
    /// <c>https://&lt;region&gt;.monitoring.azure.com&lt;resourceId&gt;/metrics</c>. Required when enabled.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Full ARM resource ID of the resource the custom metrics are emitted against
    /// (e.g. the Data Factory or Cosmos account), beginning with <c>/subscriptions/</c>. Required when enabled.
    /// </summary>
    public string? ResourceId { get; set; }

    /// <summary>Custom-metric namespace metrics are published under. Defaults to <see cref="DefaultMetricNamespace"/>.</summary>
    public string MetricNamespace { get; set; } = DefaultMetricNamespace;

    /// <summary>
    /// When <c>true</c>, the ADF run id is included as a metric dimension. Off by default to
    /// avoid high-cardinality metric series; the run id always remains available on the snapshot and logs.
    /// </summary>
    public bool IncludeRunIdDimension { get; set; }
}
