namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// Direction of a detected metric anomaly relative to its rolling baseline.
/// </summary>
public enum AnomalyDirection
{
    /// <summary>The observed value was anomalously high (above the baseline mean).</summary>
    High,

    /// <summary>The observed value was anomalously low (below the baseline mean).</summary>
    Low,
}

/// <summary>
/// A single detected anomaly in a migration metric (Request-Units/sec or throughput) relative
/// to a rolling-window statistical baseline. Produced by the anomaly detection service (#226)
/// and surfaced as a warning by the <c>migration status</c> command (#225).
/// </summary>
public sealed record MigrationAnomaly
{
    /// <summary>UTC timestamp of the observation that triggered the anomaly.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Name of the ADF pipeline the anomaly belongs to.</summary>
    public string PipelineName { get; init; } = string.Empty;

    /// <summary>Optional activity name within the pipeline.</summary>
    public string? ActivityName { get; init; }

    /// <summary>Optional ADF run identifier.</summary>
    public string? RunId { get; init; }

    /// <summary>Name of the metric that breached its baseline (e.g. <c>RequestUnitsPerSecond</c>).</summary>
    public string MetricName { get; init; } = string.Empty;

    /// <summary>The observed metric value.</summary>
    public double ObservedValue { get; init; }

    /// <summary>Mean of the prior rolling-window values that formed the baseline.</summary>
    public double BaselineMean { get; init; }

    /// <summary>Sample standard deviation of the prior rolling-window values.</summary>
    public double BaselineStdDev { get; init; }

    /// <summary>Signed z-score of the observation against the baseline.</summary>
    public double ZScore { get; init; }

    /// <summary>Whether the observation was anomalously high or low.</summary>
    public AnomalyDirection Direction { get; init; }
}
