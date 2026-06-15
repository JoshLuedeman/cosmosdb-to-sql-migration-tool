using Azure;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Builder for a mock <see cref="LogsQueryClient"/> that returns canned
/// <see cref="LogsQueryResult"/>s from <c>QueryWorkspaceAsync</c>.
///
/// <para>
/// Production code (see <c>CosmosDbAnalysisService.CollectPerformanceMetricsAsync</c>)
/// reads <c>response.Value.Table.Rows</c> and indexes <c>row[1]</c>, <c>row[2]</c>,
/// <c>row[3]</c>, so the builder always emits at least one table with four columns
/// (TimeGenerated + three numeric metrics). Override via
/// <see cref="WithTable"/> if you need a different shape.
/// </para>
/// </summary>
public sealed class LogsQueryClientMockBuilder
{
    private readonly List<LogsTable> _tables = new();
    private Exception? _exception;

    /// <summary>
    /// Adds a metrics table with one row per <paramref name="rows"/> entry.
    /// Each row is a tuple of (TimeGenerated, AverageRUs, MaxRUs, TotalRUs).
    /// </summary>
    public LogsQueryClientMockBuilder WithMetricsRows(IEnumerable<(DateTimeOffset Time, double Avg, double Max, double Total)> rows)
    {
        var columns = new[]
        {
            MonitorQueryModelFactory.LogsTableColumn("TimeGenerated", LogsColumnType.Datetime),
            MonitorQueryModelFactory.LogsTableColumn("AvgRUs", LogsColumnType.Real),
            MonitorQueryModelFactory.LogsTableColumn("MaxRUs", LogsColumnType.Real),
            MonitorQueryModelFactory.LogsTableColumn("TotalRUs", LogsColumnType.Real)
        };

        var tableRows = rows
            .Select(r => MonitorQueryModelFactory.LogsTableRow(
                columns,
                new object[] { r.Time, r.Avg, r.Max, r.Total }))
            .ToList();

        var table = MonitorQueryModelFactory.LogsTable("PrimaryResult", columns, tableRows);
        _tables.Add(table);
        return this;
    }

    /// <summary>Adds a fully-customised table to the result set.</summary>
    public LogsQueryClientMockBuilder WithTable(LogsTable table)
    {
        _tables.Add(table);
        return this;
    }

    /// <summary>Causes <c>QueryWorkspaceAsync</c> to throw the provided exception.</summary>
    public LogsQueryClientMockBuilder WithError(Exception exception)
    {
        _exception = exception;
        return this;
    }

    /// <summary>Materialises the mock client.</summary>
    public LogsQueryClient Build()
    {
        var mock = new Mock<LogsQueryClient>();

        if (_exception != null)
        {
            mock.Setup(c => c.QueryWorkspaceAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<QueryTimeRange>(),
                    It.IsAny<LogsQueryOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(_exception);
            return mock.Object;
        }

        if (_tables.Count == 0)
        {
            // Default: a single empty table so production code's null checks succeed.
            var emptyColumns = new[]
            {
                MonitorQueryModelFactory.LogsTableColumn("TimeGenerated", LogsColumnType.Datetime),
                MonitorQueryModelFactory.LogsTableColumn("AvgRUs", LogsColumnType.Real),
                MonitorQueryModelFactory.LogsTableColumn("MaxRUs", LogsColumnType.Real),
                MonitorQueryModelFactory.LogsTableColumn("TotalRUs", LogsColumnType.Real)
            };
            _tables.Add(MonitorQueryModelFactory.LogsTable("PrimaryResult", emptyColumns, Array.Empty<LogsTableRow>()));
        }

        var result = MonitorQueryModelFactory.LogsQueryResult(
            allTables: _tables,
            statistics: BinaryData.FromString("{}"),
            visualization: BinaryData.FromString("{}"),
            error: BinaryData.FromString("{}"));

        var response = Response.FromValue(result, Mock.Of<Response>());

        mock.Setup(c => c.QueryWorkspaceAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<QueryTimeRange>(),
                It.IsAny<LogsQueryOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        return mock.Object;
    }
}
