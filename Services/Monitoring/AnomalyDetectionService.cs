using System.Runtime.CompilerServices;
using CosmosToSqlAssessment.Models.Monitoring;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Detects anomalies in migration Request-Units/sec and throughput using a simple
/// rolling-window z-score baseline (#226). Feeds from the enriched
/// <see cref="MigrationProgressSnapshot"/> stream produced by <see cref="MigrationMonitoringService"/>
/// and is surfaced as warnings by the <c>migration status</c> command (#225).
/// </summary>
/// <remarks>
/// <para>
/// The detector keeps a fixed-length window of recent observed values per
/// (pipeline, run, activity, metric) key. For each new observation it evaluates the value
/// against the baseline formed by the <em>prior</em> values in the window (so a value never
/// contaminates its own baseline), then appends it.
/// </para>
/// <para>
/// <b>This type is stateful and not thread-safe.</b> It is intended to be driven by a single
/// streaming loop (the scoped CLI run). Do not share one instance across concurrent consumers.
/// The fixed sample-count window is a heuristic and does not model elapsed time or migration
/// phase changes; tune <see cref="AnomalyDetectionOptions.ZScoreThreshold"/> and
/// <see cref="AnomalyDetectionOptions.MinRelativeChange"/> if false positives occur.
/// </para>
/// </remarks>
public sealed class AnomalyDetectionService
{
    /// <summary>Metric name for the Request-Units-per-second series.</summary>
    public const string MetricRequestUnitsPerSecond = "RequestUnitsPerSecond";

    /// <summary>Metric name for the throughput (rows-per-second) series.</summary>
    public const string MetricThroughputRowsPerSecond = "ThroughputRowsPerSecond";

    private static readonly IReadOnlySet<string> TerminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Succeeded",
        "Failed",
        "Cancelled",
        "Canceled",
        "Completed",
    };

    private readonly AnomalyDetectionOptions _options;
    private readonly ILogger<AnomalyDetectionService> _logger;
    private readonly Dictionary<AnomalyKey, Queue<double>> _windows = new();

    /// <summary>
    /// Creates the anomaly detection service.
    /// </summary>
    /// <param name="options">Detector configuration.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> contains an invalid combination.</exception>
    public AnomalyDetectionService(AnomalyDetectionOptions options, ILogger<AnomalyDetectionService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_options.WindowSize <= 0)
        {
            throw new ArgumentException("WindowSize must be greater than 0.", nameof(options));
        }

        if (_options.MinSamplesForBaseline < 2)
        {
            throw new ArgumentException("MinSamplesForBaseline must be at least 2.", nameof(options));
        }

        if (_options.WindowSize < _options.MinSamplesForBaseline)
        {
            throw new ArgumentException("WindowSize must be >= MinSamplesForBaseline.", nameof(options));
        }

        if (_options.ZScoreThreshold <= 0)
        {
            throw new ArgumentException("ZScoreThreshold must be greater than 0.", nameof(options));
        }

        if (_options.MinBaselineStdDev <= 0)
        {
            throw new ArgumentException("MinBaselineStdDev must be greater than 0.", nameof(options));
        }

        if (_options.MinRelativeChange < 0)
        {
            throw new ArgumentException("MinRelativeChange must be >= 0.", nameof(options));
        }
    }

    /// <summary>
    /// Evaluates a single snapshot against the rolling baselines and returns any anomalies.
    /// Updates internal per-key window state as a side effect.
    /// </summary>
    /// <param name="snapshot">The progress snapshot to evaluate.</param>
    /// <returns>The anomalies detected for this snapshot (possibly empty).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
    public IReadOnlyList<MigrationAnomaly> Detect(MigrationProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_options.Enabled)
        {
            return Array.Empty<MigrationAnomaly>();
        }

        var anomalies = new List<MigrationAnomaly>();

        if (_options.WatchRequestUnits && snapshot.RequestUnitsPerSecond is { } ru)
        {
            EvaluateMetric(snapshot, MetricRequestUnitsPerSecond, ru, anomalies);
        }

        if (_options.WatchThroughput && snapshot.ThroughputRowsPerSecond is { } throughput)
        {
            EvaluateMetric(snapshot, MetricThroughputRowsPerSecond, throughput, anomalies);
        }

        return anomalies;
    }

    /// <summary>
    /// Streams anomalies detected across a snapshot stream.
    /// </summary>
    /// <param name="snapshots">The source snapshot stream.</param>
    /// <param name="cancellationToken">Token that stops enumeration.</param>
    /// <returns>A stream of detected anomalies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshots"/> is <c>null</c>.</exception>
    public async IAsyncEnumerable<MigrationAnomaly> DetectAsync(
        IAsyncEnumerable<MigrationProgressSnapshot> snapshots,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        await foreach (var snapshot in snapshots.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var anomaly in Detect(snapshot))
            {
                yield return anomaly;
            }
        }
    }

    private void EvaluateMetric(
        MigrationProgressSnapshot snapshot,
        string metricName,
        double value,
        List<MigrationAnomaly> anomalies)
    {
        var sample = snapshot.Sample;
        var key = new AnomalyKey(sample.PipelineName, sample.RunId, sample.ActivityName, metricName);

        if (!_windows.TryGetValue(key, out var window))
        {
            window = new Queue<double>(_options.WindowSize);
            _windows[key] = window;
        }

        if (window.Count >= _options.MinSamplesForBaseline)
        {
            var (mean, stdDev) = MeanAndSampleStdDev(window);
            var effectiveStdDev = Math.Max(stdDev, _options.MinBaselineStdDev);
            var zScore = (value - mean) / effectiveStdDev;
            var relativeChange = Math.Abs(value - mean) / Math.Max(Math.Abs(mean), _options.MinBaselineStdDev);

            if (Math.Abs(zScore) >= _options.ZScoreThreshold && relativeChange >= _options.MinRelativeChange)
            {
                var direction = value >= mean ? AnomalyDirection.High : AnomalyDirection.Low;
                var suppress = direction == AnomalyDirection.Low
                    && _options.SuppressLowOnTerminalStatus
                    && TerminalStatuses.Contains(sample.Status);

                if (!suppress)
                {
                    anomalies.Add(new MigrationAnomaly
                    {
                        Timestamp = sample.Timestamp,
                        PipelineName = sample.PipelineName,
                        ActivityName = sample.ActivityName,
                        RunId = sample.RunId,
                        MetricName = metricName,
                        ObservedValue = value,
                        BaselineMean = mean,
                        BaselineStdDev = stdDev,
                        ZScore = zScore,
                        Direction = direction,
                    });

                    _logger.LogInformation(
                        "Anomaly detected for {Pipeline}/{Activity} {Metric}: value={Value} z={Z} (mean={Mean}, stddev={StdDev}).",
                        sample.PipelineName,
                        sample.ActivityName,
                        metricName,
                        value,
                        zScore,
                        mean,
                        stdDev);
                }
            }
        }

        window.Enqueue(value);
        while (window.Count > _options.WindowSize)
        {
            window.Dequeue();
        }
    }

    private static (double Mean, double StdDev) MeanAndSampleStdDev(IReadOnlyCollection<double> values)
    {
        var count = values.Count;
        var mean = values.Sum() / count;
        var sumSquaredDeviations = values.Sum(v => (v - mean) * (v - mean));
        var variance = sumSquaredDeviations / (count - 1);
        return (mean, Math.Sqrt(variance));
    }

    private readonly record struct AnomalyKey(
        string PipelineName,
        string? RunId,
        string? ActivityName,
        string MetricName);
}
