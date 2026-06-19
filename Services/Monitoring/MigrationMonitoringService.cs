using System.Runtime.CompilerServices;
using CosmosToSqlAssessment.Models.Monitoring;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Streams migration progress metrics (rows migrated, RU consumption, error rate) to
/// Azure Monitor and yields enriched <see cref="MigrationProgressSnapshot"/>s for live
/// CLI reporting (#225) and anomaly detection (#226).
/// </summary>
/// <remarks>
/// <para>
/// The service consumes an <see cref="IAsyncEnumerable{T}"/> of
/// <see cref="MigrationProgressSample"/> (the streaming convention from #129), derives
/// per-sample custom metrics, publishes them through an
/// <see cref="IMigrationMetricPublisher"/>, and yields a snapshot for each sample.
/// </para>
/// <para>
/// Cumulative totals and rates are accumulated <em>per</em>
/// pipeline/run/activity key, so interleaved samples from concurrent runs never
/// cross-contaminate. A publish failure is logged and swallowed by the publisher so it
/// never aborts the stream.
/// </para>
/// </remarks>
public sealed class MigrationMonitoringService
{
    /// <summary>Metric name for rows migrated in a sample window.</summary>
    public const string MetricRowsMigrated = "MigrationRowsMigrated";

    /// <summary>Metric name for Request Units consumed in a sample window.</summary>
    public const string MetricRequestUnits = "MigrationRequestUnitsConsumed";

    /// <summary>Metric name for the windowed error rate (0-1).</summary>
    public const string MetricErrorRate = "MigrationErrorRate";

    /// <summary>Metric name for the error count in a sample window.</summary>
    public const string MetricErrorCount = "MigrationErrorCount";

    /// <summary>Dimension name carrying the pipeline name.</summary>
    public const string DimensionPipeline = "PipelineName";

    /// <summary>Dimension name carrying the activity name.</summary>
    public const string DimensionActivity = "ActivityName";

    /// <summary>Dimension name carrying the run status.</summary>
    public const string DimensionStatus = "Status";

    /// <summary>Dimension name carrying the run id (only emitted when opted in).</summary>
    public const string DimensionRunId = "RunId";

    private readonly IMigrationMetricPublisher _publisher;
    private readonly AzureMonitorMetricOptions _options;
    private readonly ILogger<MigrationMonitoringService> _logger;

    /// <summary>
    /// Creates the monitoring service.
    /// </summary>
    /// <param name="publisher">Sink that custom metrics are published to.</param>
    /// <param name="options">Metric publishing options (namespace, dimension policy).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public MigrationMonitoringService(
        IMigrationMetricPublisher publisher,
        AzureMonitorMetricOptions options,
        ILogger<MigrationMonitoringService> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Consumes a stream of progress samples, publishes derived custom metrics for each,
    /// and yields an enriched snapshot per sample.
    /// </summary>
    /// <param name="samples">The source stream of progress samples.</param>
    /// <param name="cancellationToken">Token that stops enumeration and publishing.</param>
    /// <returns>A stream of enriched <see cref="MigrationProgressSnapshot"/>s.</returns>
    public async IAsyncEnumerable<MigrationProgressSnapshot> MonitorAsync(
        IAsyncEnumerable<MigrationProgressSample> samples,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var state = new Dictionary<string, RunAccumulator>(StringComparer.Ordinal);

        await foreach (var sample in samples.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (sample is null)
            {
                continue;
            }

            var snapshot = BuildSnapshot(sample, state);

            // Publish out-of-band of the yield: a publish failure must never break the stream.
            try
            {
                await _publisher.PublishAsync(snapshot.Metrics, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Publishing migration metrics failed for pipeline {Pipeline}; continuing.", sample.PipelineName);
            }

            yield return snapshot;
        }
    }

    private MigrationProgressSnapshot BuildSnapshot(
        MigrationProgressSample sample,
        Dictionary<string, RunAccumulator> state)
    {
        var key = BuildKey(sample);
        if (!state.TryGetValue(key, out var acc))
        {
            acc = new RunAccumulator();
            state[key] = acc;
        }

        double? windowSeconds = acc.LastTimestamp is { } last && sample.Timestamp > last
            ? (sample.Timestamp - last).TotalSeconds
            : null;

        acc.RowsMigrated += sample.RowsMigrated;
        acc.RowsRead += sample.RowsRead ?? 0;
        acc.ErrorCount += sample.ErrorCount;
        acc.RequestUnits += sample.RequestUnitsConsumed;
        acc.LastTimestamp = sample.Timestamp;

        var windowDenominator = sample.RowsRead ?? (sample.RowsMigrated + sample.ErrorCount);
        var errorRate = windowDenominator > 0 ? (double)sample.ErrorCount / windowDenominator : 0d;

        var cumulativeDenominator = acc.RowsRead > 0 ? acc.RowsRead : acc.RowsMigrated + acc.ErrorCount;
        var cumulativeErrorRate = cumulativeDenominator > 0 ? (double)acc.ErrorCount / cumulativeDenominator : 0d;

        double? percentComplete = sample.TotalRows is { } total && total > 0
            ? Math.Min(100d, 100d * acc.RowsMigrated / total)
            : null;

        double? throughputRowsPerSecond = windowSeconds is { } secs && secs > 0
            ? sample.RowsMigrated / secs
            : null;
        double? requestUnitsPerSecond = windowSeconds is { } secs2 && secs2 > 0
            ? sample.RequestUnitsConsumed / secs2
            : null;

        var metrics = BuildMetricPoints(sample, errorRate);

        return new MigrationProgressSnapshot
        {
            Sample = sample,
            CumulativeRowsMigrated = acc.RowsMigrated,
            CumulativeRowsRead = acc.RowsRead,
            CumulativeErrorCount = acc.ErrorCount,
            CumulativeRequestUnits = acc.RequestUnits,
            PercentComplete = percentComplete,
            ErrorRate = errorRate,
            CumulativeErrorRate = cumulativeErrorRate,
            ThroughputRowsPerSecond = throughputRowsPerSecond,
            RequestUnitsPerSecond = requestUnitsPerSecond,
            Metrics = metrics,
        };
    }

    private IReadOnlyList<MigrationMetricPoint> BuildMetricPoints(MigrationProgressSample sample, double errorRate)
    {
        var dimensions = BuildDimensions(sample);
        var ns = string.IsNullOrWhiteSpace(_options.MetricNamespace)
            ? AzureMonitorMetricOptions.DefaultMetricNamespace
            : _options.MetricNamespace;

        return new List<MigrationMetricPoint>
        {
            NewPoint(MetricRowsMigrated, ns, sample.RowsMigrated, sample.Timestamp, "Count", dimensions),
            NewPoint(MetricRequestUnits, ns, sample.RequestUnitsConsumed, sample.Timestamp, "Count", dimensions),
            NewPoint(MetricErrorCount, ns, sample.ErrorCount, sample.Timestamp, "Count", dimensions),
            NewPoint(MetricErrorRate, ns, errorRate, sample.Timestamp, "Percent", dimensions),
        };
    }

    private IReadOnlyDictionary<string, string> BuildDimensions(MigrationProgressSample sample)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DimensionPipeline] = sample.PipelineName ?? string.Empty,
            [DimensionStatus] = sample.Status ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(sample.ActivityName))
        {
            dimensions[DimensionActivity] = sample.ActivityName!;
        }
        if (_options.IncludeRunIdDimension && !string.IsNullOrWhiteSpace(sample.RunId))
        {
            dimensions[DimensionRunId] = sample.RunId!;
        }
        return dimensions;
    }

    private static MigrationMetricPoint NewPoint(
        string name,
        string ns,
        double value,
        DateTimeOffset timestamp,
        string unit,
        IReadOnlyDictionary<string, string> dimensions)
        => new()
        {
            Name = name,
            Namespace = ns,
            Value = value,
            Timestamp = timestamp,
            Unit = unit,
            Dimensions = dimensions,
        };

    private static string BuildKey(MigrationProgressSample sample)
        => string.Join('|', sample.PipelineName ?? string.Empty, sample.RunId ?? string.Empty, sample.ActivityName ?? string.Empty);

    private sealed class RunAccumulator
    {
        public long RowsMigrated;
        public long RowsRead;
        public long ErrorCount;
        public double RequestUnits;
        public DateTimeOffset? LastTimestamp;
    }
}
