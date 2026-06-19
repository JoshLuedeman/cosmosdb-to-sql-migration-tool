using System.Globalization;
using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class IncrementalSyncEstimatorTests : TestBase
{
    private IncrementalSyncEstimator CreateEstimator(double rate = 5.0, int intervalMinutes = 15, double factor = 1.0)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncrementalMigration:DailyChangeRatePercent"] = rate.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:SyncIntervalMinutes"] = intervalMinutes.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:IncrementalThroughputFactor"] = factor.ToString(CultureInfo.InvariantCulture),
            })
            .Build();
        return new IncrementalSyncEstimator(config, CreateMockLogger<IncrementalSyncEstimator>().Object);
    }

    private static CosmosDbAnalysis CosmosWith(params ContainerAnalysis[] containers)
        => new() { Containers = containers.ToList() };

    private static DataFactoryEstimate DfWith(TimeSpan overall, params PipelineEstimate[] pipelines)
        => new() { EstimatedDuration = overall, PipelineEstimates = pipelines.ToList() };

    [Fact]
    public void Estimate_NullArguments_Throw()
    {
        var estimator = CreateEstimator();
        var actCosmos = () => estimator.Estimate(null!, DfWith(TimeSpan.Zero));
        var actDf = () => estimator.Estimate(CosmosWith(), null!);
        actCosmos.Should().Throw<ArgumentNullException>();
        actDf.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Estimate_NoContainers_IsHealthyAndSustainable()
    {
        var estimator = CreateEstimator();

        var result = estimator.Estimate(CosmosWith(), DfWith(TimeSpan.FromHours(1)));

        result.Containers.Should().BeEmpty();
        result.SteadyStateSustainable.Should().BeTrue();
        result.OverallRisk.Should().Be(SyncSustainabilityRisk.Healthy);
        result.EstimatedBacklogCatchUpAfterInitialLoad.Should().Be(TimeSpan.Zero);
        result.Notes.Should().Contain(n => n.Contains("No containers"));
        result.Assumptions.Should().NotBeEmpty();
    }

    [Fact]
    public void Estimate_HealthyContainer_ComputesThroughputAndCatchUp()
    {
        // 8,640,000 docs over 86,400s initial load = 100 docs/sec capacity (factor 1.0).
        // 5%/day churn = 432,000/day = 5 docs/sec → utilization 5% → Healthy.
        var estimator = CreateEstimator(rate: 5.0, factor: 1.0);
        var container = new ContainerAnalysis
        {
            ContainerName = "c1",
            DocumentCount = 8_640_000,
            SizeBytes = 8_640_000L * 1024,
            FeedRangeCount = 4,
        };
        var df = DfWith(TimeSpan.FromSeconds(86_400),
            new PipelineEstimate { SourceContainer = "c1", EstimatedDuration = TimeSpan.FromSeconds(86_400) });

        var result = estimator.Estimate(CosmosWith(container), df);
        var c = result.Containers.Single();

        c.InitialLoadDocsPerSecond.Should().BeApproximately(100.0, 0.001);
        c.EstimatedIncrementalCapacityDocsPerSecond.Should().BeApproximately(100.0, 0.001);
        c.EstimatedChangedDocumentsPerSecond.Should().BeApproximately(5.0, 0.001);
        c.UtilizationPercent.Should().BeApproximately(5.0, 0.001);
        c.Risk.Should().Be(SyncSustainabilityRisk.Healthy);
        c.SteadyStateSustainable.Should().BeTrue();
        c.EstimatedBacklogDocumentsAfterInitialLoad.Should().Be(432_000);
        c.EstimatedBacklogCatchUp.Should().NotBeNull();
        c.EstimatedBacklogCatchUp!.Value.TotalSeconds.Should().BeApproximately(432_000 / 95.0, 1.0);
        result.SteadyStateSustainable.Should().BeTrue();
        result.OverallRisk.Should().Be(SyncSustainabilityRisk.Healthy);
        result.HighestRiskContainers.Should().BeEmpty();
    }

    [Fact]
    public void Estimate_ChangeRateAboveCapacity_IsUnsustainable()
    {
        // 200%/day churn ⇒ 200 docs/sec > 100 docs/sec capacity ⇒ unsustainable.
        var estimator = CreateEstimator(rate: 200.0, factor: 1.0);
        var container = new ContainerAnalysis
        {
            ContainerName = "hot",
            DocumentCount = 8_640_000,
            SizeBytes = 8_640_000L * 512,
            FeedRangeCount = 8,
        };
        var df = DfWith(TimeSpan.FromSeconds(86_400),
            new PipelineEstimate { SourceContainer = "hot", EstimatedDuration = TimeSpan.FromSeconds(86_400) });

        var result = estimator.Estimate(CosmosWith(container), df);
        var c = result.Containers.Single();

        c.Risk.Should().Be(SyncSustainabilityRisk.Unsustainable);
        c.SteadyStateSustainable.Should().BeFalse();
        c.EstimatedBacklogCatchUp.Should().BeNull();
        c.UtilizationPercent.Should().BeApproximately(200.0, 0.001);
        result.SteadyStateSustainable.Should().BeFalse();
        result.OverallRisk.Should().Be(SyncSustainabilityRisk.Unsustainable);
        result.EstimatedBacklogCatchUpAfterInitialLoad.Should().BeNull();
        result.HighestRiskContainers.Should().Contain("hot");
        result.Notes.Should().Contain(n => n.Contains("at or above incremental capacity"));
    }

    [Fact]
    public void Estimate_ZeroDocuments_IsHealthyWithZeroBacklog()
    {
        var estimator = CreateEstimator();
        var container = new ContainerAnalysis { ContainerName = "empty", DocumentCount = 0, SizeBytes = 0 };
        var df = DfWith(TimeSpan.Zero,
            new PipelineEstimate { SourceContainer = "empty", EstimatedDuration = TimeSpan.Zero });

        var c = estimator.Estimate(CosmosWith(container), df).Containers.Single();

        c.InitialLoadThroughputKnown.Should().BeTrue();
        c.SteadyStateSustainable.Should().BeTrue();
        c.Risk.Should().Be(SyncSustainabilityRisk.Healthy);
        c.EstimatedBacklogCatchUp.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Estimate_UnknownInitialLoadDuration_MarksCapacityUnknown()
    {
        var estimator = CreateEstimator();
        var container = new ContainerAnalysis { ContainerName = "x", DocumentCount = 1000, SizeBytes = 1024 };
        // Pipeline present but zero duration while docs exist ⇒ capacity indeterminate.
        var df = DfWith(TimeSpan.Zero,
            new PipelineEstimate { SourceContainer = "x", EstimatedDuration = TimeSpan.Zero });

        var c = estimator.Estimate(CosmosWith(container), df).Containers.Single();

        c.InitialLoadThroughputKnown.Should().BeFalse();
        c.Risk.Should().Be(SyncSustainabilityRisk.Unknown);
        c.SteadyStateSustainable.Should().BeFalse();
        c.EstimatedBacklogCatchUp.Should().BeNull();
        c.Notes.Should().Contain(n => n.Contains("unknown"));
    }

    [Fact]
    public void Estimate_NoMatchingPipeline_DistributesByDocShare()
    {
        var estimator = CreateEstimator();
        var a = new ContainerAnalysis { ContainerName = "a", DocumentCount = 3000, SizeBytes = 3000 };
        var b = new ContainerAnalysis { ContainerName = "b", DocumentCount = 1000, SizeBytes = 1000 };
        // No pipeline estimates ⇒ overall 4000s distributed: a→3000s, b→1000s.
        var df = DfWith(TimeSpan.FromSeconds(4000));

        var result = estimator.Estimate(CosmosWith(a, b), df);
        var ca = result.Containers.Single(c => c.ContainerName == "a");
        var cb = result.Containers.Single(c => c.ContainerName == "b");

        ca.InitialLoadDuration.TotalSeconds.Should().BeApproximately(3000, 0.5);
        cb.InitialLoadDuration.TotalSeconds.Should().BeApproximately(1000, 0.5);
        ca.Notes.Should().Contain(n => n.Contains("distributed by document share"));
    }

    [Fact]
    public void Estimate_SingleFeedRangeWithChanges_AddsParallelismNote()
    {
        var estimator = CreateEstimator(rate: 10.0);
        var container = new ContainerAnalysis
        {
            ContainerName = "single",
            DocumentCount = 100_000,
            SizeBytes = 100_000 * 1024,
            FeedRangeCount = 1,
        };
        var df = DfWith(TimeSpan.FromSeconds(1000),
            new PipelineEstimate { SourceContainer = "single", EstimatedDuration = TimeSpan.FromSeconds(1000) });

        var c = estimator.Estimate(CosmosWith(container), df).Containers.Single();

        c.Notes.Should().Contain(n => n.Contains("single feed range"));
    }

    [Fact]
    public void Estimate_MixedRisk_AggregatesWorstCase()
    {
        var estimator = CreateEstimator(rate: 5.0, factor: 1.0);
        var healthy = new ContainerAnalysis { ContainerName = "ok", DocumentCount = 8_640_000, SizeBytes = 1, FeedRangeCount = 4 };
        var hot = new ContainerAnalysis { ContainerName = "hot", DocumentCount = 8_640_000, SizeBytes = 1, FeedRangeCount = 4 };
        var df = DfWith(TimeSpan.FromSeconds(86_400),
            new PipelineEstimate { SourceContainer = "ok", EstimatedDuration = TimeSpan.FromSeconds(86_400) },
            // "hot" gets a tiny initial load ⇒ very high docs/sec capacity is NOT what we want;
            // instead give it a long load so capacity is low relative to churn.
            new PipelineEstimate { SourceContainer = "hot", EstimatedDuration = TimeSpan.FromSeconds(86_400 * 100) });

        var result = estimator.Estimate(CosmosWith(healthy, hot), df);

        // hot: 8.64M docs / 8.64M s = 1 doc/sec capacity; churn 5 docs/sec ⇒ unsustainable.
        var hotEstimate = result.Containers.Single(c => c.ContainerName == "hot");
        hotEstimate.SteadyStateSustainable.Should().BeFalse();
        result.OverallRisk.Should().Be(SyncSustainabilityRisk.Unsustainable);
        result.SteadyStateSustainable.Should().BeFalse();
        result.EstimatedBacklogCatchUpAfterInitialLoad.Should().BeNull();
        result.HighestRiskContainers.Should().Contain("hot");
    }
}
