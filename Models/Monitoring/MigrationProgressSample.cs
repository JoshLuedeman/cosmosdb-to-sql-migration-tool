namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// A single point-in-time observation of an in-flight migration, typically derived
/// from an Azure Data Factory pipeline/activity run (parent #70). Each sample carries
/// the <em>delta</em> work completed since the previous sample for the same
/// pipeline/activity/run so the monitoring pipeline can derive both windowed rates and
/// cumulative totals.
/// </summary>
public sealed record MigrationProgressSample
{
    /// <summary>UTC timestamp at which this sample was observed. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Name of the ADF pipeline the sample belongs to. Used as a metric dimension and accumulation key.</summary>
    public string PipelineName { get; init; } = string.Empty;

    /// <summary>Optional name of the specific copy/activity within the pipeline.</summary>
    public string? ActivityName { get; init; }

    /// <summary>Optional ADF run identifier. Kept on the snapshot and logs; only emitted as a metric dimension when opted in.</summary>
    public string? RunId { get; init; }

    /// <summary>Rows successfully migrated during this sample window (a delta, not a running total).</summary>
    public long RowsMigrated { get; init; }

    /// <summary>
    /// Rows read from the source during this sample window, when known. Used as the
    /// denominator for the windowed error rate. When <c>null</c>, the rate falls back to
    /// <c>ErrorCount / (RowsMigrated + ErrorCount)</c>.
    /// </summary>
    public long? RowsRead { get; init; }

    /// <summary>Total rows expected for the activity/run, when known. Drives percent-complete on the snapshot.</summary>
    public long? TotalRows { get; init; }

    /// <summary>Request Units consumed during this sample window (a delta).</summary>
    public double RequestUnitsConsumed { get; init; }

    /// <summary>Errors encountered during this sample window (a delta).</summary>
    public long ErrorCount { get; init; }

    /// <summary>Free-form status string for the run/activity (e.g. <c>InProgress</c>, <c>Succeeded</c>, <c>Failed</c>).</summary>
    public string Status { get; init; } = "InProgress";
}
