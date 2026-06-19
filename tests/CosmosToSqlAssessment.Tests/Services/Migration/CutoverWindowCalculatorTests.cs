using System.Globalization;
using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class CutoverWindowCalculatorTests : TestBase
{
    private CutoverWindowCalculator CreateCalculator(
        double appStop = 5,
        double validation = 15,
        double connectionSwitch = 5,
        double buffer = 20,
        double target = 30,
        double parallelism = 100)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncrementalMigration:CutoverAppStopMinutes"] = appStop.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:CutoverValidationMinutes"] = validation.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:CutoverConnectionSwitchMinutes"] = connectionSwitch.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:CutoverSafetyBufferPercent"] = buffer.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:CutoverTargetDowntimeMinutes"] = target.ToString(CultureInfo.InvariantCulture),
                ["IncrementalMigration:CutoverDrainParallelismPercent"] = parallelism.ToString(CultureInfo.InvariantCulture),
            })
            .Build();
        return new CutoverWindowCalculator(config, CreateMockLogger<CutoverWindowCalculator>().Object);
    }

    private static IncrementalSyncEstimate SyncWith(TimeSpan syncInterval, params ContainerIncrementalSyncEstimate[] containers)
        => new() { SyncInterval = syncInterval, Containers = containers.ToList() };

    private static ContainerIncrementalSyncEstimate SustainableContainer(
        string name,
        double changedPerSecond,
        double capacity,
        TimeSpan lag)
        => new()
        {
            ContainerName = name,
            DocumentCount = 1_000_000,
            EstimatedChangedDocumentsPerSecond = changedPerSecond,
            EstimatedIncrementalCapacityDocsPerSecond = capacity,
            EstimatedSteadyStateSyncLag = lag,
            InitialLoadThroughputKnown = true,
            SteadyStateSustainable = true,
        };

    [Fact]
    public void Calculate_NullArgument_Throws()
    {
        var act = () => CreateCalculator().Calculate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Calculate_NoContainers_IsFixedOverheadOnly()
    {
        var result = CreateCalculator(appStop: 5, validation: 15, connectionSwitch: 5, buffer: 20).Calculate(
            SyncWith(TimeSpan.FromMinutes(15)));

        result.FixedOverheadDuration.Should().Be(TimeSpan.FromMinutes(25));
        result.MinimumKnownDowntime.Should().Be(TimeSpan.FromMinutes(30)); // 25 * 1.2
        result.FinalSyncDrainDuration.Should().Be(TimeSpan.Zero);
        result.TotalDowntime.Should().Be(TimeSpan.FromMinutes(30));
        result.DrainBounded.Should().BeTrue();
        result.Feasibility.Should().Be(CutoverFeasibility.Feasible);
        result.Risk.Should().Be(CutoverDowntimeRisk.Low); // 30 <= target 30
        result.Notes.Should().Contain(n => n.Contains("No containers"));
    }

    [Fact]
    public void Calculate_SustainableContainer_ComputesBoundedWindow()
    {
        // capacity 100/s, churn 5/s, lag 900s ⇒ residual 4500 docs, drain 45s.
        var calc = CreateCalculator(appStop: 1, validation: 2, connectionSwitch: 1, buffer: 0, target: 30);
        var container = SustainableContainer("c1", changedPerSecond: 5, capacity: 100, lag: TimeSpan.FromSeconds(900));

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), container));
        var c = result.Containers.Single();

        c.ResidualBacklogDocuments.Should().Be(4500);
        c.DrainBounded.Should().BeTrue();
        c.ResidualDrainDuration!.Value.TotalSeconds.Should().BeApproximately(45, 0.5);
        result.FinalSyncDrainDuration!.Value.TotalSeconds.Should().BeApproximately(45, 0.5);
        result.ParallelDrainDuration!.Value.TotalSeconds.Should().BeApproximately(45, 0.5);
        result.FullyContendedDrainDuration!.Value.TotalSeconds.Should().BeApproximately(45, 0.5);
        // fixed overhead 4 min = 240s + 45s = 285s, buffer 0 ⇒ 285s.
        result.TotalDowntime!.Value.TotalSeconds.Should().BeApproximately(285, 1.0);
        result.Feasibility.Should().Be(CutoverFeasibility.Feasible);
        result.Risk.Should().Be(CutoverDowntimeRisk.Low);
    }

    [Fact]
    public void Calculate_BufferIsApplied()
    {
        var calc = CreateCalculator(appStop: 10, validation: 0, connectionSwitch: 0, buffer: 50, target: 30);
        var container = SustainableContainer("c1", changedPerSecond: 0, capacity: 100, lag: TimeSpan.FromSeconds(900));

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), container));

        // No churn ⇒ residual 0 ⇒ drain 0. Fixed 10 min × 1.5 = 15 min.
        result.FinalSyncDrainDuration.Should().Be(TimeSpan.Zero);
        result.TotalDowntime.Should().Be(TimeSpan.FromMinutes(15));
        result.MinimumKnownDowntime.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Calculate_RiskBands_DerivedFromTarget()
    {
        // fixed 50 min, no drain, target 30 ⇒ 50 > 30 and <= 60 ⇒ Moderate.
        var moderate = CreateCalculator(appStop: 50, validation: 0, connectionSwitch: 0, buffer: 0, target: 30)
            .Calculate(SyncWith(TimeSpan.FromMinutes(15)));
        moderate.TotalDowntime.Should().Be(TimeSpan.FromMinutes(50));
        moderate.Risk.Should().Be(CutoverDowntimeRisk.Moderate);

        // fixed 90 min, target 30 ⇒ 90 > 60 ⇒ High.
        var high = CreateCalculator(appStop: 90, validation: 0, connectionSwitch: 0, buffer: 0, target: 30)
            .Calculate(SyncWith(TimeSpan.FromMinutes(15)));
        high.Risk.Should().Be(CutoverDowntimeRisk.High);
    }

    [Fact]
    public void Calculate_UnsustainableContainer_RequiresPreCutoverCatchUp()
    {
        var calc = CreateCalculator();
        var hot = new ContainerIncrementalSyncEstimate
        {
            ContainerName = "hot",
            DocumentCount = 1_000_000,
            EstimatedChangedDocumentsPerSecond = 200,
            EstimatedIncrementalCapacityDocsPerSecond = 100,
            EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(15),
            InitialLoadThroughputKnown = true,
            SteadyStateSustainable = false,
        };

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), hot));

        result.Feasibility.Should().Be(CutoverFeasibility.RequiresPreCutoverCatchUp);
        result.TotalDowntime.Should().BeNull();
        result.FinalSyncDrainDuration.Should().BeNull();
        result.DrainBounded.Should().BeFalse();
        result.Risk.Should().Be(CutoverDowntimeRisk.Unknown);
        result.ContainersRequiringPreCutoverCatchUp.Should().Contain("hot");
        // The fixed floor is still surfaced.
        result.MinimumKnownDowntime.Should().BeGreaterThan(TimeSpan.Zero);
        result.Containers.Single().Notes.Should().Contain(n => n.Contains("unsustainable"));
    }

    [Fact]
    public void Calculate_UnknownCapacityWithChurn_RequiresPreCutoverCatchUp()
    {
        var calc = CreateCalculator();
        var unknown = new ContainerIncrementalSyncEstimate
        {
            ContainerName = "x",
            DocumentCount = 1_000,
            EstimatedChangedDocumentsPerSecond = 1,
            EstimatedIncrementalCapacityDocsPerSecond = 0,
            EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(15),
            InitialLoadThroughputKnown = false,
            SteadyStateSustainable = true,
        };

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), unknown));

        result.Feasibility.Should().Be(CutoverFeasibility.RequiresPreCutoverCatchUp);
        result.Containers.Single().DrainBounded.Should().BeFalse();
        result.Containers.Single().Notes.Should().Contain(n => n.Contains("capacity is unknown"));
    }

    [Fact]
    public void Calculate_ZeroResidualWithUnknownCapacity_IsBounded()
    {
        // No churn ⇒ residual 0 ⇒ drain 0 regardless of unknown capacity.
        var calc = CreateCalculator();
        var idle = new ContainerIncrementalSyncEstimate
        {
            ContainerName = "idle",
            DocumentCount = 1_000,
            EstimatedChangedDocumentsPerSecond = 0,
            EstimatedIncrementalCapacityDocsPerSecond = 0,
            EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(15),
            InitialLoadThroughputKnown = false,
            SteadyStateSustainable = true,
        };

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), idle));

        result.Containers.Single().DrainBounded.Should().BeTrue();
        result.Containers.Single().ResidualDrainDuration.Should().Be(TimeSpan.Zero);
        result.Feasibility.Should().Be(CutoverFeasibility.Feasible);
        result.TotalDowntime.Should().NotBeNull();
    }

    [Fact]
    public void Calculate_FullContention_UsesSerialSum()
    {
        // parallelism 0 ⇒ final drain = serial sum of per-container drains.
        // c1: residual 4500/100 = 45s; c2: residual 9000/100 = 90s. parallel max = 90s, serial = 135s.
        var calc = CreateCalculator(appStop: 0, validation: 0, connectionSwitch: 0, buffer: 0, target: 30, parallelism: 0);
        var c1 = SustainableContainer("c1", changedPerSecond: 5, capacity: 100, lag: TimeSpan.FromSeconds(900));
        var c2 = SustainableContainer("c2", changedPerSecond: 10, capacity: 100, lag: TimeSpan.FromSeconds(900));

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), c1, c2));

        result.ParallelDrainDuration!.Value.TotalSeconds.Should().BeApproximately(90, 1.0);
        result.FullyContendedDrainDuration!.Value.TotalSeconds.Should().BeApproximately(135, 1.0);
        result.FinalSyncDrainDuration!.Value.TotalSeconds.Should().BeApproximately(135, 1.0);
        result.Notes.Should().Contain(n => n.Contains("contention"));
    }

    [Fact]
    public void Calculate_FullParallel_UsesSlowestContainer()
    {
        var calc = CreateCalculator(appStop: 0, validation: 0, connectionSwitch: 0, buffer: 0, target: 30, parallelism: 100);
        var c1 = SustainableContainer("c1", changedPerSecond: 5, capacity: 100, lag: TimeSpan.FromSeconds(900));
        var c2 = SustainableContainer("c2", changedPerSecond: 10, capacity: 100, lag: TimeSpan.FromSeconds(900));

        var result = calc.Calculate(SyncWith(TimeSpan.FromMinutes(15), c1, c2));

        // Fully parallel ⇒ slowest container (90s), not the serial sum.
        result.FinalSyncDrainDuration!.Value.TotalSeconds.Should().BeApproximately(90, 1.0);
    }
}
