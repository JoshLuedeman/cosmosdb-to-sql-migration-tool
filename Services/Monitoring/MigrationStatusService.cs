using System.Globalization;
using CosmosToSqlAssessment.Models.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Renders live migration progress for the <c>migration status</c> CLI command (#225).
/// </summary>
/// <remarks>
/// <para>
/// Pulls progress samples from an <see cref="IMigrationStatusSource"/> (backed by ADF run
/// telemetry from parents #70/#76), enriches them through the same
/// <see cref="MigrationMonitoringService"/> derivation used by the publishing path (#223),
/// and writes a concise per-update line plus a closing summary to a caller-supplied
/// <see cref="TextWriter"/>.
/// </para>
/// <para>
/// Status reporting is strictly read-only: it derives snapshots with a
/// <see cref="NullMigrationMetricPublisher"/> so viewing progress never re-publishes custom
/// metrics to Azure Monitor.
/// </para>
/// </remarks>
public sealed class MigrationStatusService
{
    private readonly IMigrationStatusSource _source;
    private readonly MigrationMonitoringService _monitoring;
    private readonly ILogger<MigrationStatusService> _logger;

    /// <summary>
    /// Creates the status service.
    /// </summary>
    /// <param name="source">Source of migration progress samples.</param>
    /// <param name="metricOptions">Metric options reused for snapshot derivation (namespace/dimensions).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public MigrationStatusService(
        IMigrationStatusSource source,
        AzureMonitorMetricOptions metricOptions,
        ILogger<MigrationStatusService> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(metricOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Read-only derivation: a null publisher guarantees status never re-publishes metrics.
        _monitoring = new MigrationMonitoringService(
            new NullMigrationMetricPublisher(),
            metricOptions,
            NullLogger<MigrationMonitoringService>.Instance);
    }

    /// <summary>
    /// Renders migration progress to <paramref name="writer"/> and returns a process exit code
    /// (0 on success).
    /// </summary>
    /// <param name="options">Watch/poll settings for the report.</param>
    /// <param name="writer">Destination for the rendered output.</param>
    /// <param name="cancellationToken">Token that stops a watch loop and ends the report gracefully.</param>
    /// <returns>0 on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="writer"/> is <c>null</c>.</exception>
    public async Task<int> RunAsync(
        MigrationStatusReportOptions options,
        TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("Migration status");
        writer.WriteLine(options.Watch
            ? $"Watching live progress (poll every {options.PollIntervalSeconds}s; press Ctrl+C to stop)…"
            : "Live progress snapshot:");
        writer.WriteLine();

        var latestByKey = new Dictionary<string, MigrationProgressSnapshot>(StringComparer.Ordinal);
        var updateCount = 0;

        try
        {
            await foreach (var snapshot in _monitoring
                .MonitorAsync(_source.ReadSamplesAsync(options, cancellationToken), cancellationToken)
                .ConfigureAwait(false))
            {
                updateCount++;
                latestByKey[BuildKey(snapshot)] = snapshot;
                writer.WriteLine(FormatLine(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            // Watch mode stop (Ctrl+C) is an expected, graceful exit — fall through to summary.
            writer.WriteLine();
            writer.WriteLine("Stopped watching.");
        }

        writer.WriteLine();
        if (updateCount == 0)
        {
            writer.WriteLine("No active migration progress found.");
            writer.WriteLine("Ensure migration telemetry is being emitted and that AzureMonitor:WorkspaceId is configured.");
            return 0;
        }

        WriteSummary(writer, latestByKey.Values, updateCount);
        return 0;
    }

    private static string FormatLine(MigrationProgressSnapshot snapshot)
    {
        var sample = snapshot.Sample;
        var activity = string.IsNullOrEmpty(sample.ActivityName) ? string.Empty : $"/{sample.ActivityName}";
        var percent = snapshot.PercentComplete is { } pct
            ? string.Create(CultureInfo.InvariantCulture, $"{pct:0.0}%")
            : "—";
        var throughput = snapshot.ThroughputRowsPerSecond is { } thr
            ? string.Create(CultureInfo.InvariantCulture, $"{thr:0.0} rows/s")
            : "—";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{sample.Timestamp:HH:mm:ss}] {sample.PipelineName}{activity} {sample.Status} | " +
            $"rows={snapshot.CumulativeRowsMigrated} ({percent}) | " +
            $"RU={snapshot.CumulativeRequestUnits:0.##} | " +
            $"errRate={snapshot.CumulativeErrorRate * 100:0.00}% | " +
            $"thr={throughput}");
    }

    private static void WriteSummary(
        TextWriter writer,
        IEnumerable<MigrationProgressSnapshot> latest,
        int updateCount)
    {
        long totalRows = 0;
        long totalRead = 0;
        long totalErrors = 0;
        double totalRu = 0;
        var pipelines = 0;

        foreach (var snapshot in latest)
        {
            pipelines++;
            totalRows += snapshot.CumulativeRowsMigrated;
            totalRead += snapshot.CumulativeRowsRead;
            totalErrors += snapshot.CumulativeErrorCount;
            totalRu += snapshot.CumulativeRequestUnits;
        }

        var denominator = totalRead > 0 ? totalRead : totalRows + totalErrors;
        var overallErrorRate = denominator > 0 ? (double)totalErrors / denominator : 0d;

        writer.WriteLine("── Summary ──");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Updates rendered:     {updateCount}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Pipelines/activities: {pipelines}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Total rows migrated:  {totalRows}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Total RU consumed:    {totalRu:0.##}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Overall error rate:   {overallErrorRate * 100:0.00}%"));
    }

    private static string BuildKey(MigrationProgressSnapshot snapshot)
    {
        var sample = snapshot.Sample;
        return $"{sample.PipelineName}|{sample.RunId}|{sample.ActivityName}";
    }
}
