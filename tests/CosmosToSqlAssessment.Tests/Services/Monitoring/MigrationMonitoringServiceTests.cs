using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class MigrationMonitoringServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

    private static MigrationMonitoringService CreateService(
        IMigrationMetricPublisher publisher,
        AzureMonitorMetricOptions? options = null)
        => new(publisher, options ?? new AzureMonitorMetricOptions(), Mock.Of<ILogger<MigrationMonitoringService>>());

    private static async Task<List<MigrationProgressSnapshot>> CollectAsync(
        MigrationMonitoringService service,
        IEnumerable<MigrationProgressSample> samples)
    {
        var result = new List<MigrationProgressSnapshot>();
        await foreach (var snapshot in service.MonitorAsync(samples.ToAsync()))
        {
            result.Add(snapshot);
        }
        return result;
    }

    [Fact]
    public async Task MonitorAsync_EmitsFourMetricPointsPerSample()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 100, RowsRead = 100, RequestUnitsConsumed = 50, Timestamp = T0 },
        });

        snapshots.Should().HaveCount(1);
        snapshots[0].Metrics.Select(m => m.Name).Should().BeEquivalentTo(new[]
        {
            MigrationMonitoringService.MetricRowsMigrated,
            MigrationMonitoringService.MetricRequestUnits,
            MigrationMonitoringService.MetricErrorCount,
            MigrationMonitoringService.MetricErrorRate,
        });
        publisher.Batches.Should().HaveCount(1);
    }

    [Fact]
    public async Task MonitorAsync_AccumulatesCumulativeTotalsPerKey()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RunId = "r1", RowsMigrated = 100, RowsRead = 110, RequestUnitsConsumed = 40, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "P", RunId = "r1", RowsMigrated = 50, RowsRead = 60, RequestUnitsConsumed = 25, Timestamp = T0.AddMinutes(1) },
        });

        snapshots[1].CumulativeRowsMigrated.Should().Be(150);
        snapshots[1].CumulativeRowsRead.Should().Be(170);
        snapshots[1].CumulativeRequestUnits.Should().Be(65);
    }

    [Fact]
    public async Task MonitorAsync_PartitionsAccumulationByPipelineRunActivity()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "A", RowsMigrated = 100, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "B", RowsMigrated = 5, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "A", RowsMigrated = 100, Timestamp = T0.AddMinutes(1) },
        });

        snapshots[2].CumulativeRowsMigrated.Should().Be(200); // A only, not contaminated by B
    }

    [Fact]
    public async Task MonitorAsync_WindowedErrorRate_UsesRowsReadDenominator()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 90, RowsRead = 100, ErrorCount = 10, Timestamp = T0 },
        });

        snapshots[0].ErrorRate.Should().BeApproximately(0.10, 1e-9);
    }

    [Fact]
    public async Task MonitorAsync_WindowedErrorRate_FallsBackWhenRowsReadUnknown()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 8, ErrorCount = 2, Timestamp = T0 },
        });

        // fallback denominator = RowsMigrated + ErrorCount = 10
        snapshots[0].ErrorRate.Should().BeApproximately(0.20, 1e-9);
    }

    [Fact]
    public async Task MonitorAsync_PercentComplete_ComputedFromTotalRows()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 250, TotalRows = 1000, Timestamp = T0 },
        });

        snapshots[0].PercentComplete.Should().BeApproximately(25d, 1e-9);
    }

    [Fact]
    public async Task MonitorAsync_PercentComplete_NullWhenTotalUnknown()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 10, Timestamp = T0 },
        });

        snapshots[0].PercentComplete.Should().BeNull();
    }

    [Fact]
    public async Task MonitorAsync_Throughput_NullOnFirstSampleThenComputed()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 60, RequestUnitsConsumed = 120, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 120, RequestUnitsConsumed = 600, Timestamp = T0.AddSeconds(60) },
        });

        snapshots[0].ThroughputRowsPerSecond.Should().BeNull();
        snapshots[1].ThroughputRowsPerSecond.Should().BeApproximately(2d, 1e-9); // 120 rows / 60s
        snapshots[1].RequestUnitsPerSecond.Should().BeApproximately(10d, 1e-9); // 600 RU / 60s
    }

    [Fact]
    public async Task MonitorAsync_RunIdDimension_ExcludedByDefault()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);

        await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RunId = "run-123", RowsMigrated = 1, Timestamp = T0 },
        });

        publisher.AllPoints.Should().NotBeEmpty();
        publisher.AllPoints.Should().OnlyContain(p => !p.Dimensions.ContainsKey(MigrationMonitoringService.DimensionRunId));
    }

    [Fact]
    public async Task MonitorAsync_RunIdDimension_IncludedWhenOptedIn()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher, new AzureMonitorMetricOptions { IncludeRunIdDimension = true });

        await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RunId = "run-123", RowsMigrated = 1, Timestamp = T0 },
        });

        publisher.AllPoints.Should().OnlyContain(p => p.Dimensions[MigrationMonitoringService.DimensionRunId] == "run-123");
    }

    [Fact]
    public async Task MonitorAsync_PublisherFailure_DoesNotBreakStream()
    {
        var publisher = new ThrowingMetricPublisher();
        var service = CreateService(publisher);

        var snapshots = await CollectAsync(service, new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 1, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 2, Timestamp = T0.AddMinutes(1) },
        });

        snapshots.Should().HaveCount(2);
        publisher.Calls.Should().Be(2);
    }

    [Fact]
    public async Task MonitorAsync_Cancellation_StopsEnumeration()
    {
        var publisher = new RecordingMetricPublisher();
        var service = CreateService(publisher);
        using var cts = new CancellationTokenSource();

        var samples = new[]
        {
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 1, Timestamp = T0 },
            new MigrationProgressSample { PipelineName = "P", RowsMigrated = 2, Timestamp = T0.AddMinutes(1) },
        };

        var seen = 0;
        Func<Task> act = async () =>
        {
            await foreach (var _ in service.MonitorAsync(samples.ToAsync(cts.Token), cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Be(1);
    }
}
