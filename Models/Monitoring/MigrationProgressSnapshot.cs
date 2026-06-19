namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// An enriched, consumer-facing view of a single <see cref="MigrationProgressSample"/>.
/// Carries the windowed values from the sample plus cumulative totals and derived rates
/// for the originating pipeline/activity/run, and the custom-metric points that were
/// emitted for the sample. Produced by the migration monitoring pipeline and consumed by
/// the <c>migration status</c> CLI command (#225) and anomaly detection (#226).
/// </summary>
public sealed record MigrationProgressSnapshot
{
    /// <summary>The original sample this snapshot was derived from.</summary>
    public MigrationProgressSample Sample { get; init; } = new();

    /// <summary>Running total of rows migrated for this pipeline/activity/run.</summary>
    public long CumulativeRowsMigrated { get; init; }

    /// <summary>Running total of rows read for this pipeline/activity/run.</summary>
    public long CumulativeRowsRead { get; init; }

    /// <summary>Running total of errors for this pipeline/activity/run.</summary>
    public long CumulativeErrorCount { get; init; }

    /// <summary>Running total of Request Units consumed for this pipeline/activity/run.</summary>
    public double CumulativeRequestUnits { get; init; }

    /// <summary>
    /// Percent of <see cref="MigrationProgressSample.TotalRows"/> migrated so far (0-100),
    /// or <c>null</c> when the total is unknown.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// Windowed error rate for this sample: errors divided by rows read in the window
    /// (or rows migrated + errors when rows read is unknown). Range 0-1.
    /// </summary>
    public double ErrorRate { get; init; }

    /// <summary>Cumulative error rate for this pipeline/activity/run. Range 0-1.</summary>
    public double CumulativeErrorRate { get; init; }

    /// <summary>
    /// Throughput in rows per second for this window, when an inter-sample duration could
    /// be derived; otherwise <c>null</c> (e.g. the first sample for a key).
    /// </summary>
    public double? ThroughputRowsPerSecond { get; init; }

    /// <summary>
    /// Request Units per second for this window, when an inter-sample duration could be
    /// derived; otherwise <c>null</c>.
    /// </summary>
    public double? RequestUnitsPerSecond { get; init; }

    /// <summary>The custom-metric points emitted to Azure Monitor for this sample.</summary>
    public IReadOnlyList<MigrationMetricPoint> Metrics { get; init; } = Array.Empty<MigrationMetricPoint>();
}
