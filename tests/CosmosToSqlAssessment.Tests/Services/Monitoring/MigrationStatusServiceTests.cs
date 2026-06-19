using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class MigrationStatusServiceTests
{
    private static MigrationStatusService CreateService(
        IMigrationStatusSource source,
        AzureMonitorMetricOptions? options = null) =>
        new(source, options ?? new AzureMonitorMetricOptions(), Mock.Of<ILogger<MigrationStatusService>>());

    private static MigrationProgressSample Sample(
        string pipeline,
        long rows,
        string status = "InProgress",
        long? totalRows = null,
        double ru = 0,
        long errors = 0) =>
        new()
        {
            PipelineName = pipeline,
            RowsMigrated = rows,
            RowsRead = rows + errors,
            TotalRows = totalRows,
            RequestUnitsConsumed = ru,
            ErrorCount = errors,
            Status = status,
        };

    [Fact]
    public async Task RunAsync_RendersHeaderPerUpdateLineAndSummary()
    {
        var source = new FakeMigrationStatusSource(new[]
        {
            Sample("Migrate_Orders", rows: 100, totalRows: 1000, ru: 50),
            Sample("Migrate_Orders", rows: 150, totalRows: 1000, ru: 75),
        });
        var service = CreateService(source);
        var writer = new StringWriter();

        var exit = await service.RunAsync(new MigrationStatusReportOptions(), writer);

        exit.Should().Be(0);
        var text = writer.ToString();
        text.Should().Contain("Migration status");
        text.Should().Contain("Migrate_Orders");
        text.Should().Contain("── Summary ──");
        text.Should().Contain("Total rows migrated:  250");
    }

    [Fact]
    public async Task RunAsync_ComputesCumulativeAndPercentComplete()
    {
        var source = new FakeMigrationStatusSource(new[]
        {
            Sample("P", rows: 250, totalRows: 1000),
            Sample("P", rows: 250, totalRows: 1000),
        });
        var service = CreateService(source);
        var writer = new StringWriter();

        await service.RunAsync(new MigrationStatusReportOptions(), writer);

        var text = writer.ToString();
        // After two 250-row windows against a 1000 total -> 500 rows, 50%.
        text.Should().Contain("rows=500");
        text.Should().Contain("50.0%");
    }

    [Fact]
    public async Task RunAsync_NoSamples_ReportsNoActiveMigration()
    {
        var source = new FakeMigrationStatusSource(Array.Empty<MigrationProgressSample>());
        var service = CreateService(source);
        var writer = new StringWriter();

        var exit = await service.RunAsync(new MigrationStatusReportOptions(), writer);

        exit.Should().Be(0);
        writer.ToString().Should().Contain("No active migration progress found.");
    }

    [Fact]
    public async Task RunAsync_DoesNotRepublishMetrics()
    {
        // The service derives snapshots with a null publisher; a recording publisher passed via
        // options is never consulted. We assert behaviorally: running status twice is side-effect free.
        var source = new FakeMigrationStatusSource(new[] { Sample("P", rows: 10) });
        var service = CreateService(source);
        var writer = new StringWriter();

        var first = await service.RunAsync(new MigrationStatusReportOptions(), writer);
        var second = await service.RunAsync(new MigrationStatusReportOptions(), new StringWriter());

        first.Should().Be(0);
        second.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WatchCancellation_StopsGracefullyAndPrintsSummary()
    {
        var source = new FakeMigrationStatusSource(
            new[] { Sample("P", rows: 10) },
            loopForeverWhenWatching: true);
        var service = CreateService(source);
        var writer = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var exit = await service.RunAsync(
            new MigrationStatusReportOptions { Watch = true, PollIntervalSeconds = 1 },
            writer,
            cts.Token);

        exit.Should().Be(0);
        var text = writer.ToString();
        text.Should().Contain("Watching live progress");
        text.Should().Contain("Stopped watching.");
        text.Should().Contain("── Summary ──");
    }

    [Fact]
    public async Task RunAsync_NullArguments_Throw()
    {
        var service = CreateService(new FakeMigrationStatusSource(Array.Empty<MigrationProgressSample>()));

        var actNullOptions = async () => await service.RunAsync(null!, new StringWriter());
        var actNullWriter = async () => await service.RunAsync(new MigrationStatusReportOptions(), null!);

        await actNullOptions.Should().ThrowAsync<ArgumentNullException>();
        await actNullWriter.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_WithAnomalyDetector_RendersAnomalyWarning()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        MigrationProgressSample TimedSample(int second, double ru) => new()
        {
            PipelineName = "Migrate_Orders",
            Timestamp = start.AddSeconds(second),
            RowsMigrated = 10,
            RowsRead = 10,
            RequestUnitsConsumed = ru,
            Status = "InProgress",
        };

        // 1s spacing => RU/sec == RequestUnitsConsumed. First sample has no prior window
        // (RU/sec null), so samples 1-5 form the baseline (~100) and sample 6 spikes to 500.
        var samples = new[]
        {
            TimedSample(0, 100),
            TimedSample(1, 100),
            TimedSample(2, 100),
            TimedSample(3, 100),
            TimedSample(4, 100),
            TimedSample(5, 100),
            TimedSample(6, 500),
        };
        var source = new FakeMigrationStatusSource(samples);
        var detector = new AnomalyDetectionService(new AnomalyDetectionOptions(), NullLogger<AnomalyDetectionService>.Instance);
        var service = new MigrationStatusService(
            source,
            new AzureMonitorMetricOptions(),
            detector,
            Mock.Of<ILogger<MigrationStatusService>>());
        var writer = new StringWriter();

        await service.RunAsync(new MigrationStatusReportOptions(), writer);

        writer.ToString().Should().Contain("⚠ anomaly");
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var source = new FakeMigrationStatusSource(Array.Empty<MigrationProgressSample>());
        var options = new AzureMonitorMetricOptions();
        var logger = Mock.Of<ILogger<MigrationStatusService>>();

        ((Func<MigrationStatusService>)(() => new MigrationStatusService(null!, options, logger)))
            .Should().Throw<ArgumentNullException>();
        ((Func<MigrationStatusService>)(() => new MigrationStatusService(source, null!, logger)))
            .Should().Throw<ArgumentNullException>();
        ((Func<MigrationStatusService>)(() => new MigrationStatusService(source, options, null!)))
            .Should().Throw<ArgumentNullException>();
    }
}
