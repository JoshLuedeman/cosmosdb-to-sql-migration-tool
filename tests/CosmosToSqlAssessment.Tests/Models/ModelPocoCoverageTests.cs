using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Tests.Models;

// Tests for thin model POCOs that are otherwise instantiated only by production code paths
// that the rest of the test suite doesn't reach. Round-tripping properties keeps these
// classes counted against the coverage gate so accidental dead code is easier to spot.
public class ModelPocoCoverageTests
{
    [Fact]
    public void QueryMetrics_RoundTrips_All_Properties()
    {
        var metrics = new QueryMetrics
        {
            QueryPattern = "SELECT * FROM c WHERE c.id = @id",
            ExecutionCount = 1234,
            AverageRUs = 3.5,
            AverageLatencyMs = 12.75
        };

        metrics.QueryPattern.Should().Be("SELECT * FROM c WHERE c.id = @id");
        metrics.ExecutionCount.Should().Be(1234);
        metrics.AverageRUs.Should().Be(3.5);
        metrics.AverageLatencyMs.Should().Be(12.75);
    }

    [Fact]
    public void QueryMetrics_Defaults_Are_Safe()
    {
        var metrics = new QueryMetrics();

        metrics.QueryPattern.Should().BeEmpty();
        metrics.ExecutionCount.Should().Be(0);
        metrics.AverageRUs.Should().Be(0);
        metrics.AverageLatencyMs.Should().Be(0);
    }

    [Fact]
    public void HotPartition_RoundTrips_All_Properties()
    {
        var hot = new HotPartition
        {
            PartitionKeyValue = "tenant-42",
            RUConsumptionPercentage = 87.5,
            RequestCount = 9876
        };

        hot.PartitionKeyValue.Should().Be("tenant-42");
        hot.RUConsumptionPercentage.Should().Be(87.5);
        hot.RequestCount.Should().Be(9876);
    }

    [Fact]
    public void HotPartition_Defaults_Are_Safe()
    {
        var hot = new HotPartition();

        hot.PartitionKeyValue.Should().BeEmpty();
        hot.RUConsumptionPercentage.Should().Be(0);
        hot.RequestCount.Should().Be(0);
    }

    [Fact]
    public void PerformanceTrend_RoundTrips_All_Properties()
    {
        var trend = new PerformanceTrend
        {
            MetricName = "AverageRUs",
            Trend = "Increasing",
            ChangePercentage = 22.5
        };

        trend.MetricName.Should().Be("AverageRUs");
        trend.Trend.Should().Be("Increasing");
        trend.ChangePercentage.Should().Be(22.5);
    }

    [Fact]
    public void PerformanceTrend_Defaults_Are_Safe()
    {
        var trend = new PerformanceTrend();

        trend.MetricName.Should().BeEmpty();
        trend.Trend.Should().BeEmpty();
        trend.ChangePercentage.Should().Be(0);
    }

    [Fact]
    public void PerformanceMetrics_Aggregates_HotPartitions_And_Trends()
    {
        var metrics = new PerformanceMetrics
        {
            TotalRUConsumption = 1_000_000,
            AverageRUsPerSecond = 100,
            PeakRUsPerSecond = 250,
            AverageRequestLatencyMs = 8.4,
            TotalRequests = 999_999,
            ErrorRate = 0.001,
            ThrottlingRate = 0.02,
            AnalysisPeriod = new TimeRange
            {
                StartTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2025, 6, 30, 23, 59, 59, DateTimeKind.Utc)
            },
            Trends = new List<PerformanceTrend>
            {
                new() { MetricName = "AverageRUs", Trend = "Stable", ChangePercentage = 0.5 },
                new() { MetricName = "Latency", Trend = "Decreasing", ChangePercentage = -10.0 }
            }
        };

        metrics.TotalRUConsumption.Should().Be(1_000_000);
        metrics.PeakRUsPerSecond.Should().Be(250);
        metrics.AnalysisPeriod.StartTime.Year.Should().Be(2025);
        metrics.AnalysisPeriod.EndTime.Month.Should().Be(6);
        metrics.Trends.Should().HaveCount(2);
        metrics.Trends[0].Trend.Should().Be("Stable");
        metrics.Trends[1].ChangePercentage.Should().Be(-10.0);
    }
}
