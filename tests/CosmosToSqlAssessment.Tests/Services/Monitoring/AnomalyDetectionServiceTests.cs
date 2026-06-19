using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AnomalyDetectionServiceTests
{
    private static AnomalyDetectionService CreateService(AnomalyDetectionOptions? options = null) =>
        new(options ?? new AnomalyDetectionOptions(), NullLogger<AnomalyDetectionService>.Instance);

    private static MigrationProgressSnapshot RuSnapshot(
        double requestUnitsPerSecond,
        string pipeline = "P",
        string status = "InProgress",
        string? runId = null,
        string? activity = null) =>
        new()
        {
            Sample = new MigrationProgressSample
            {
                PipelineName = pipeline,
                ActivityName = activity,
                RunId = runId,
                Status = status,
                Timestamp = DateTimeOffset.UtcNow,
            },
            RequestUnitsPerSecond = requestUnitsPerSecond,
        };

    private static List<MigrationAnomaly> Feed(AnomalyDetectionService service, IEnumerable<MigrationProgressSnapshot> snapshots)
    {
        var all = new List<MigrationAnomaly>();
        foreach (var snapshot in snapshots)
        {
            all.AddRange(service.Detect(snapshot));
        }

        return all;
    }

    [Fact]
    public void Detect_HighSpike_AfterStableBaseline_IsFlagged()
    {
        var service = CreateService();
        var baseline = new[] { 100d, 101d, 99d, 100d, 100d }.Select(v => RuSnapshot(v));
        Feed(service, baseline).Should().BeEmpty("baseline values are not anomalous");

        var anomalies = service.Detect(RuSnapshot(200d));

        anomalies.Should().ContainSingle();
        var anomaly = anomalies[0];
        anomaly.Direction.Should().Be(AnomalyDirection.High);
        anomaly.MetricName.Should().Be(AnomalyDetectionService.MetricRequestUnitsPerSecond);
        anomaly.ObservedValue.Should().Be(200d);
        anomaly.BaselineMean.Should().BeApproximately(100d, 0.5);
        anomaly.ZScore.Should().BeGreaterThan(3d);
    }

    [Fact]
    public void Detect_LowDrop_AfterStableBaseline_IsFlagged()
    {
        var service = CreateService();
        Feed(service, new[] { 100d, 101d, 99d, 100d, 100d }.Select(v => RuSnapshot(v)));

        var anomalies = service.Detect(RuSnapshot(10d));

        anomalies.Should().ContainSingle();
        anomalies[0].Direction.Should().Be(AnomalyDirection.Low);
        anomalies[0].ZScore.Should().BeLessThan(-3d);
    }

    [Fact]
    public void Detect_DuringWarmup_NeverFlags()
    {
        var service = CreateService(new AnomalyDetectionOptions { MinSamplesForBaseline = 5 });

        // Only 4 prior samples exist when the 5th wild value arrives -> below warmup threshold.
        var anomalies = Feed(service, new[] { 100d, 100d, 100d, 100d, 9999d }.Select(v => RuSnapshot(v)));

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Detect_ConstantBaselineThenTinyNoise_DoesNotFlap()
    {
        var service = CreateService();
        Feed(service, Enumerable.Repeat(100d, 6).Select(v => RuSnapshot(v)));

        // A near-zero deviation has a huge z-score against a constant baseline, but the
        // relative-change guard suppresses it.
        var anomalies = service.Detect(RuSnapshot(100.0001d));

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Detect_ConstantBaselineThenRealDrop_IsFlagged()
    {
        var service = CreateService();
        Feed(service, Enumerable.Repeat(100d, 6).Select(v => RuSnapshot(v)));

        // A genuine drop clears both the z-score and the relative-change guard even though
        // the baseline standard deviation is zero (floored).
        var anomalies = service.Detect(RuSnapshot(40d));

        anomalies.Should().ContainSingle();
        anomalies[0].Direction.Should().Be(AnomalyDirection.Low);
    }

    [Fact]
    public void Detect_ModerateDeviationBelowThreshold_IsNotFlagged()
    {
        var service = CreateService();
        // Wide-spread baseline -> a +30 move yields z < 3.
        Feed(service, new[] { 80d, 120d, 90d, 110d, 100d }.Select(v => RuSnapshot(v)));

        var anomalies = service.Detect(RuSnapshot(130d));

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Detect_KeysAreIsolated()
    {
        var service = CreateService();
        Feed(service, new[] { 100d, 101d, 99d, 100d, 100d }.Select(v => RuSnapshot(v, pipeline: "A")));
        Feed(service, new[] { 10d, 11d, 9d, 10d, 10d }.Select(v => RuSnapshot(v, pipeline: "B")));

        var anomalies = service.Detect(RuSnapshot(200d, pipeline: "A"));

        anomalies.Should().ContainSingle();
        anomalies[0].PipelineName.Should().Be("A");
        anomalies[0].BaselineMean.Should().BeApproximately(100d, 0.5, "B's values must not contaminate A's baseline");
    }

    [Fact]
    public void Detect_NullMetrics_AreSkipped()
    {
        var service = CreateService();
        var snapshots = Enumerable.Range(0, 10).Select(_ => new MigrationProgressSnapshot
        {
            Sample = new MigrationProgressSample { PipelineName = "P" },
            RequestUnitsPerSecond = null,
            ThroughputRowsPerSecond = null,
        });

        Feed(service, snapshots).Should().BeEmpty();
    }

    [Fact]
    public void Detect_LowOnTerminalStatus_IsSuppressed_ButHighIsNot()
    {
        var service = CreateService();
        Feed(service, new[] { 100d, 101d, 99d, 100d, 100d }.Select(v => RuSnapshot(v)));

        var low = service.Detect(RuSnapshot(5d, status: "Succeeded"));
        low.Should().BeEmpty("ramp-down at completion should not warn");

        var high = service.Detect(RuSnapshot(500d, status: "Succeeded"));
        high.Should().ContainSingle();
        high[0].Direction.Should().Be(AnomalyDirection.High);
    }

    [Fact]
    public void Detect_Throughput_IsWatchedIndependently()
    {
        var service = CreateService();
        var baseline = Enumerable.Repeat(50d, 6).Select(v => new MigrationProgressSnapshot
        {
            Sample = new MigrationProgressSample { PipelineName = "P" },
            ThroughputRowsPerSecond = v,
        });
        Feed(service, baseline);

        var anomalies = service.Detect(new MigrationProgressSnapshot
        {
            Sample = new MigrationProgressSample { PipelineName = "P" },
            ThroughputRowsPerSecond = 500d,
        });

        anomalies.Should().ContainSingle();
        anomalies[0].MetricName.Should().Be(AnomalyDetectionService.MetricThroughputRowsPerSecond);
    }

    [Fact]
    public void Detect_WhenDisabled_ReturnsEmpty()
    {
        var service = CreateService(new AnomalyDetectionOptions { Enabled = false });

        Feed(service, new[] { 100d, 100d, 100d, 100d, 100d, 99999d }.Select(v => RuSnapshot(v)))
            .Should().BeEmpty();
    }

    [Fact]
    public void Detect_NullSnapshot_Throws()
    {
        var service = CreateService();

        ((Action)(() => service.Detect(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task DetectAsync_StreamsDetectedAnomalies()
    {
        var service = CreateService();
        var snapshots = new[] { 100d, 101d, 99d, 100d, 100d, 250d }.Select(v => RuSnapshot(v));

        var anomalies = new List<MigrationAnomaly>();
        await foreach (var anomaly in service.DetectAsync(snapshots.ToAsync()))
        {
            anomalies.Add(anomaly);
        }

        anomalies.Should().ContainSingle();
        anomalies[0].Direction.Should().Be(AnomalyDirection.High);
    }

    [Fact]
    public async Task DetectAsync_NullSource_Throws()
    {
        var service = CreateService();

        var act = async () =>
        {
            await foreach (var _ in service.DetectAsync(null!))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0, 5, 3.0, 1e-9, 0.25)]   // WindowSize <= 0
    [InlineData(20, 1, 3.0, 1e-9, 0.25)]  // MinSamplesForBaseline < 2
    [InlineData(3, 5, 3.0, 1e-9, 0.25)]   // WindowSize < MinSamplesForBaseline
    [InlineData(20, 5, 0.0, 1e-9, 0.25)]  // ZScoreThreshold <= 0
    [InlineData(20, 5, 3.0, 0.0, 0.25)]   // MinBaselineStdDev <= 0
    [InlineData(20, 5, 3.0, 1e-9, -0.1)]  // MinRelativeChange < 0
    public void Constructor_InvalidOptions_Throw(
        int windowSize,
        int minSamples,
        double zThreshold,
        double minStdDev,
        double minRelative)
    {
        var options = new AnomalyDetectionOptions
        {
            WindowSize = windowSize,
            MinSamplesForBaseline = minSamples,
            ZScoreThreshold = zThreshold,
            MinBaselineStdDev = minStdDev,
            MinRelativeChange = minRelative,
        };

        ((Func<AnomalyDetectionService>)(() => CreateService(options)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        ((Func<AnomalyDetectionService>)(() => new AnomalyDetectionService(null!, NullLogger<AnomalyDetectionService>.Instance)))
            .Should().Throw<ArgumentNullException>();
        ((Func<AnomalyDetectionService>)(() => new AnomalyDetectionService(new AnomalyDetectionOptions(), null!)))
            .Should().Throw<ArgumentNullException>();
    }
}
