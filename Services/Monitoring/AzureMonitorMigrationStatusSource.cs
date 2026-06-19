using System.Globalization;
using System.Runtime.CompilerServices;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using CosmosToSqlAssessment.Models.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Default <see cref="IMigrationStatusSource"/> that reads in-flight migration progress from
/// Azure Data Factory Copy-activity telemetry (parent #70) surfaced in Log Analytics
/// (parent #76). Reuses the existing <c>AzureMonitor:WorkspaceId</c> configuration and
/// <see cref="DefaultAzureCredential"/>.
/// </summary>
/// <remarks>
/// <para>
/// When no Log Analytics workspace is configured the source yields nothing (and performs no
/// network call), so offline / CI runs of <c>migration status</c> simply report that no live
/// source is available. ADF activity output reports <em>cumulative</em> rows per activity, so
/// the source diffs successive observations into the per-window deltas the monitoring pipeline
/// (#223) expects.
/// </para>
/// <para>
/// Request-Unit consumption is not present in ADF logs; it flows through the custom-metric
/// publishing path instead, so the samples produced here carry rows/throughput/status only.
/// </para>
/// </remarks>
public sealed class AzureMonitorMigrationStatusSource : IMigrationStatusSource
{
    /// <summary>Default Log Analytics lookback window, in minutes, when none is configured.</summary>
    public const int DefaultLookbackMinutes = 60;

    private const string LookbackConfigKey = "AzureMonitor:Status:LookbackMinutes";
    private const string WorkspaceConfigKey = "AzureMonitor:WorkspaceId";

    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureMonitorMigrationStatusSource> _logger;
    private readonly LogsQueryClient? _logsQueryClient;

    /// <summary>
    /// Production constructor. Builds a <see cref="LogsQueryClient"/> with
    /// <see cref="DefaultAzureCredential"/> when a workspace is configured.
    /// </summary>
    /// <param name="configuration">Application configuration carrying the Azure Monitor settings.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public AzureMonitorMigrationStatusSource(
        IConfiguration configuration,
        ILogger<AzureMonitorMigrationStatusSource> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var workspaceId = _configuration[WorkspaceConfigKey];
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            try
            {
                _logsQueryClient = new LogsQueryClient(new DefaultAzureCredential());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize Azure Monitor logs client for migration status.");
            }
        }
    }

    /// <summary>
    /// Test-friendly constructor accepting a pre-built <see cref="LogsQueryClient"/>.
    /// </summary>
    /// <param name="configuration">Application configuration carrying the Azure Monitor settings.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="logsQueryClient">Pre-built logs query client (or <c>null</c> for the no-source path).</param>
    internal AzureMonitorMigrationStatusSource(
        IConfiguration configuration,
        ILogger<AzureMonitorMigrationStatusSource> logger,
        LogsQueryClient? logsQueryClient)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logsQueryClient = logsQueryClient;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MigrationProgressSample> ReadSamplesAsync(
        MigrationStatusReportOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workspaceId = _configuration[WorkspaceConfigKey];
        if (_logsQueryClient is null || string.IsNullOrWhiteSpace(workspaceId))
        {
            _logger.LogWarning(
                "Azure Monitor workspace not configured ({Key}); migration status has no live source.",
                WorkspaceConfigKey);
            yield break;
        }

        var lookbackMinutes = GetLookbackMinutes();
        var lastRowsByKey = new Dictionary<string, long>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observations = await QueryObservationsAsync(workspaceId!, lookbackMinutes, cancellationToken)
                .ConfigureAwait(false);

            foreach (var observation in observations)
            {
                var previous = lastRowsByKey.TryGetValue(observation.Key, out var p) ? p : 0L;
                var delta = observation.CumulativeRowsCopied - previous;
                if (delta < 0)
                {
                    // Activity restarted / counter reset — treat the current value as the delta.
                    delta = observation.CumulativeRowsCopied;
                }

                lastRowsByKey[observation.Key] = observation.CumulativeRowsCopied;

                yield return new MigrationProgressSample
                {
                    Timestamp = observation.Timestamp,
                    PipelineName = observation.PipelineName,
                    ActivityName = observation.ActivityName,
                    RunId = observation.RunId,
                    Status = observation.Status,
                    RowsMigrated = delta,
                };
            }

            if (!options.Watch)
            {
                yield break;
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private int GetLookbackMinutes()
    {
        var raw = _configuration[LookbackConfigKey];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? minutes
            : DefaultLookbackMinutes;
    }

    private async Task<IReadOnlyList<ActivityObservation>> QueryObservationsAsync(
        string workspaceId,
        int lookbackMinutes,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(lookbackMinutes);
        var endTime = DateTimeOffset.UtcNow;
        var startTime = endTime.AddMinutes(-lookbackMinutes);

        try
        {
            var response = await _logsQueryClient!.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(startTime, endTime),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var table = response.Value?.Table;
            if (table is null || table.Rows.Count == 0)
            {
                return Array.Empty<ActivityObservation>();
            }

            var observations = new List<ActivityObservation>(table.Rows.Count);
            foreach (var row in table.Rows)
            {
                observations.Add(MapRow(row));
            }

            return observations;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Querying Azure Monitor for migration status failed; returning no updates this cycle.");
            return Array.Empty<ActivityObservation>();
        }
    }

    /// <summary>
    /// KQL that projects the latest Copy-activity progress per activity run from the
    /// Dedicated-mode <c>ADFActivityRun</c> table.
    /// </summary>
    /// <param name="lookbackMinutes">How far back, in minutes, to scan.</param>
    /// <returns>The KQL query text.</returns>
    internal static string BuildQuery(int lookbackMinutes) =>
$$"""
ADFActivityRun
| where TimeGenerated > ago({{lookbackMinutes}}m)
| where ActivityType == "Copy"
| extend out = parse_json(Output)
| summarize arg_max(TimeGenerated, Status, RowsCopied = tolong(out.rowsCopied), RowsRead = tolong(out.rowsRead))
    by PipelineName, ActivityName, RunId
| project TimeGenerated, PipelineName, ActivityName, RunId, Status, RowsCopied, RowsRead
| order by TimeGenerated asc
""";

    /// <summary>
    /// Maps a projected <c>ADFActivityRun</c> row into an <see cref="ActivityObservation"/>.
    /// Column order matches <see cref="BuildQuery"/>.
    /// </summary>
    /// <param name="row">The Log Analytics row to map.</param>
    /// <returns>The mapped observation.</returns>
    internal static ActivityObservation MapRow(LogsTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ActivityObservation(
            Timestamp: ReadDateTime(row, 0),
            PipelineName: ReadString(row, 1),
            ActivityName: ReadNullableString(row, 2),
            RunId: ReadNullableString(row, 3),
            Status: ReadString(row, 4, fallback: "InProgress"),
            CumulativeRowsCopied: ReadLong(row, 5),
            CumulativeRowsRead: ReadLong(row, 6));
    }

    private static DateTimeOffset ReadDateTime(LogsTableRow row, int index)
    {
        var value = row[index];
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            _ => DateTimeOffset.UtcNow,
        };
    }

    private static string ReadString(LogsTableRow row, int index, string fallback = "")
    {
        var value = row[index];
        return value is null ? fallback : value.ToString() ?? fallback;
    }

    private static string? ReadNullableString(LogsTableRow row, int index)
    {
        var value = row[index];
        var text = value?.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static long ReadLong(LogsTableRow row, int index)
    {
        var value = row[index];
        return value switch
        {
            null => 0L,
            long l => l,
            int i => i,
            _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L,
        };
    }

    /// <summary>
    /// A single latest-progress observation for one ADF Copy activity run.
    /// </summary>
    /// <param name="Timestamp">When the observation was generated.</param>
    /// <param name="PipelineName">Owning pipeline name.</param>
    /// <param name="ActivityName">Copy activity name, if present.</param>
    /// <param name="RunId">ADF run identifier, if present.</param>
    /// <param name="Status">Activity status (e.g. InProgress, Succeeded, Failed).</param>
    /// <param name="CumulativeRowsCopied">Cumulative rows copied reported by ADF.</param>
    /// <param name="CumulativeRowsRead">Cumulative rows read reported by ADF.</param>
    internal sealed record ActivityObservation(
        DateTimeOffset Timestamp,
        string PipelineName,
        string? ActivityName,
        string? RunId,
        string Status,
        long CumulativeRowsCopied,
        long CumulativeRowsRead)
    {
        /// <summary>Stable accumulation key for diffing successive observations.</summary>
        public string Key => $"{PipelineName}|{RunId}|{ActivityName}";
    }
}
