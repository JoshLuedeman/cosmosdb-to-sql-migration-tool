namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// A single custom-metric data point destined for Azure Monitor. The
/// <see cref="CosmosToSqlAssessment.Services.Monitoring.AzureMonitorMetricPayloadBuilder"/>
/// groups points that share a namespace, name, timestamp, and dimension shape into one
/// ingestion payload.
/// </summary>
/// <remarks>
/// <see cref="CosmosToSqlAssessment.Services.Monitoring.AzureMonitorMetricPayloadBuilder"/>
/// lives in <c>CosmosToSqlAssessment.Services.Monitoring</c>.
/// </remarks>
public sealed record MigrationMetricPoint
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDimensions =
        new Dictionary<string, string>(0);

    /// <summary>The metric name (e.g. <c>MigrationRowsMigrated</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The custom-metric namespace the point is published under.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The metric value for this point.</summary>
    public double Value { get; init; }

    /// <summary>UTC timestamp the value was observed at.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The metric unit (e.g. <c>Count</c>, <c>Percent</c>). Informational only.</summary>
    public string Unit { get; init; } = "Count";

    /// <summary>
    /// Dimension name/value pairs attached to the point. Dimension <em>names</em> become the
    /// Azure Monitor <c>dimNames</c> array and the <em>values</em> become a series'
    /// <c>dimValues</c>. Defaults to an empty, read-only dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = EmptyDimensions;
}
