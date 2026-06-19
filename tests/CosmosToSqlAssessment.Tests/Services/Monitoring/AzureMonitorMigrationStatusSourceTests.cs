using Azure.Monitor.Query.Models;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;
using CosmosToSqlAssessment.Tests.Mocks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AzureMonitorMigrationStatusSourceTests
{
    private static IConfiguration Config(string? workspaceId = null, string? lookback = null)
    {
        var values = new Dictionary<string, string?>();
        if (workspaceId is not null)
        {
            values["AzureMonitor:WorkspaceId"] = workspaceId;
        }

        if (lookback is not null)
        {
            values["AzureMonitor:Status:LookbackMinutes"] = lookback;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ILogger<AzureMonitorMigrationStatusSource> Logger() =>
        Mock.Of<ILogger<AzureMonitorMigrationStatusSource>>();

    private static LogsTableRow ActivityRow(
        DateTimeOffset time,
        string pipeline,
        string activity,
        string runId,
        string status,
        long rowsCopied,
        long rowsRead)
    {
        var columns = new[]
        {
            MonitorQueryModelFactory.LogsTableColumn("TimeGenerated", LogsColumnType.Datetime),
            MonitorQueryModelFactory.LogsTableColumn("PipelineName", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("ActivityName", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("RunId", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("Status", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("RowsCopied", LogsColumnType.Long),
            MonitorQueryModelFactory.LogsTableColumn("RowsRead", LogsColumnType.Long),
        };

        return MonitorQueryModelFactory.LogsTableRow(
            columns,
            new object[] { time, pipeline, activity, runId, status, rowsCopied, rowsRead });
    }

    private static LogsTable ActivityTable(params LogsTableRow[] rows)
    {
        var columns = new[]
        {
            MonitorQueryModelFactory.LogsTableColumn("TimeGenerated", LogsColumnType.Datetime),
            MonitorQueryModelFactory.LogsTableColumn("PipelineName", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("ActivityName", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("RunId", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("Status", LogsColumnType.String),
            MonitorQueryModelFactory.LogsTableColumn("RowsCopied", LogsColumnType.Long),
            MonitorQueryModelFactory.LogsTableColumn("RowsRead", LogsColumnType.Long),
        };

        return MonitorQueryModelFactory.LogsTable("PrimaryResult", columns, rows);
    }

    [Fact]
    public void BuildQuery_TargetsAdfActivityRunWithLookback()
    {
        var query = AzureMonitorMigrationStatusSource.BuildQuery(60);

        query.Should().Contain("ADFActivityRun");
        query.Should().Contain("ago(60m)");
        query.Should().Contain("Copy");
    }

    [Fact]
    public void MapRow_MapsAllProjectedColumns()
    {
        var time = DateTimeOffset.UtcNow;
        var row = ActivityRow(time, "Migrate_Orders", "CopyOrders", "run-1", "InProgress", 500, 520);

        var observation = AzureMonitorMigrationStatusSource.MapRow(row);

        observation.Timestamp.Should().Be(time);
        observation.PipelineName.Should().Be("Migrate_Orders");
        observation.ActivityName.Should().Be("CopyOrders");
        observation.RunId.Should().Be("run-1");
        observation.Status.Should().Be("InProgress");
        observation.CumulativeRowsCopied.Should().Be(500);
        observation.CumulativeRowsRead.Should().Be(520);
        observation.Key.Should().Be("Migrate_Orders|run-1|CopyOrders");
    }

    [Fact]
    public async Task ReadSamplesAsync_WorkspaceNotConfigured_YieldsNothing()
    {
        var source = new AzureMonitorMigrationStatusSource(Config(workspaceId: null), Logger(), logsQueryClient: null);

        var samples = new List<MigrationProgressSample>();
        await foreach (var sample in source.ReadSamplesAsync(new MigrationStatusReportOptions()))
        {
            samples.Add(sample);
        }

        samples.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadSamplesAsync_ClientPresentButNoWorkspaceConfig_YieldsNothing()
    {
        var client = new LogsQueryClientMockBuilder().WithTable(ActivityTable()).Build();
        var source = new AzureMonitorMigrationStatusSource(Config(workspaceId: null), Logger(), client);

        var samples = new List<MigrationProgressSample>();
        await foreach (var sample in source.ReadSamplesAsync(new MigrationStatusReportOptions()))
        {
            samples.Add(sample);
        }

        samples.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadSamplesAsync_OneShot_MapsRowsToSamplesWithFirstObservationDelta()
    {
        var time = DateTimeOffset.UtcNow;
        var table = ActivityTable(
            ActivityRow(time, "Migrate_Orders", "CopyOrders", "run-1", "InProgress", 500, 520));
        var client = new LogsQueryClientMockBuilder().WithTable(table).Build();
        var source = new AzureMonitorMigrationStatusSource(Config(workspaceId: "ws-id"), Logger(), client);

        var samples = new List<MigrationProgressSample>();
        await foreach (var sample in source.ReadSamplesAsync(new MigrationStatusReportOptions()))
        {
            samples.Add(sample);
        }

        samples.Should().ContainSingle();
        var only = samples[0];
        only.PipelineName.Should().Be("Migrate_Orders");
        only.ActivityName.Should().Be("CopyOrders");
        only.RunId.Should().Be("run-1");
        only.Status.Should().Be("InProgress");
        only.RowsMigrated.Should().Be(500); // first observation: delta == cumulative
    }

    [Fact]
    public async Task ReadSamplesAsync_QueryError_YieldsNothingGracefully()
    {
        var client = new LogsQueryClientMockBuilder()
            .WithError(new InvalidOperationException("kusto exploded"))
            .Build();
        var source = new AzureMonitorMigrationStatusSource(Config(workspaceId: "ws-id"), Logger(), client);

        var samples = new List<MigrationProgressSample>();
        await foreach (var sample in source.ReadSamplesAsync(new MigrationStatusReportOptions()))
        {
            samples.Add(sample);
        }

        samples.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadSamplesAsync_NullOptions_Throws()
    {
        var source = new AzureMonitorMigrationStatusSource(Config(workspaceId: "ws-id"), Logger(), logsQueryClient: null);

        var act = async () =>
        {
            await foreach (var _ in source.ReadSamplesAsync(null!))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
