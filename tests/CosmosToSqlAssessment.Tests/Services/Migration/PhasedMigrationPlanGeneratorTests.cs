using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class PhasedMigrationPlanGeneratorTests : TestBase
{
    private PhasedMigrationPlanGenerator CreateGenerator()
        => new(MockConfiguration.Object, CreateMockLogger<PhasedMigrationPlanGenerator>().Object);

    private static IncrementalMigrationAnalysis HealthyAnalysis()
    {
        var analysis = new IncrementalMigrationAnalysis
        {
            ChangeFeed = new ChangeFeedAvailabilityAnalysis
            {
                Containers =
                {
                    new ContainerChangeFeedReadiness { ContainerName = "c1", KnownServerSideTtlDeletes = false },
                },
                AllContainersSupportLatestVersionIncrementalSync = true,
                AnyContainerHasKnownServerSideDeletes = false,
            },
            SyncEstimate = new IncrementalSyncEstimate
            {
                SyncInterval = TimeSpan.FromMinutes(15),
                InitialLoadDuration = TimeSpan.FromHours(4),
                EstimatedBacklogCatchUpAfterInitialLoad = TimeSpan.FromMinutes(30),
                EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(16),
                OverallRisk = SyncSustainabilityRisk.Healthy,
                SteadyStateSustainable = true,
                Containers = { new ContainerIncrementalSyncEstimate { ContainerName = "c1" } },
            },
            CutoverWindow = new CutoverWindowEstimate
            {
                TotalDowntime = TimeSpan.FromMinutes(20),
                MinimumKnownDowntime = TimeSpan.FromMinutes(18),
                Feasibility = CutoverFeasibility.Feasible,
                Risk = CutoverDowntimeRisk.Low,
            },
        };
        return analysis;
    }

    [Fact]
    public void Generate_NullArgument_Throws()
    {
        var act = () => CreateGenerator().Generate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Generate_HealthyAnalysis_IsReadyWithFiveOrderedPhases()
    {
        var plan = CreateGenerator().Generate(HealthyAnalysis());

        plan.OverallReadiness.Should().Be(MigrationReadiness.Ready);
        plan.Phases.Should().HaveCount(5);
        plan.Phases.Select(p => p.Order).Should().BeInAscendingOrder().And.Equal(1, 2, 3, 4, 5);
        plan.Phases.Should().OnlyContain(p => p.EntryCriteria.Count > 0 && p.ExitCriteria.Count > 0 && p.Steps.Count > 0);
    }

    [Fact]
    public void Generate_SeparatesPreparationFromDowntime()
    {
        var analysis = HealthyAnalysis();
        var plan = CreateGenerator().Generate(analysis);

        // Default soak = 4 × 15 min = 60 min. Prep = 4h initial + 30m catch-up + 60m soak = 5h30m.
        plan.EstimatedElapsedPreparationDuration.Should().Be(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(90));
        // Business downtime is the cutover window only — not summed with preparation.
        plan.EstimatedBusinessDowntime.Should().Be(TimeSpan.FromMinutes(20));
        plan.MinimumBusinessDowntime.Should().Be(TimeSpan.FromMinutes(18));
        plan.PlanWarnings.Should().Contain(w => w.Contains("exclude"));
    }

    [Fact]
    public void Generate_TtlDeleteContainers_IsReadyWithCaveats()
    {
        var analysis = HealthyAnalysis();
        analysis.ChangeFeed.Containers[0].KnownServerSideTtlDeletes = true;
        analysis.ChangeFeed.AnyContainerHasKnownServerSideDeletes = true;

        var plan = CreateGenerator().Generate(analysis);

        plan.OverallReadiness.Should().Be(MigrationReadiness.ReadyWithCaveats);
        plan.ReadinessFactors.Should().Contain(f => f.Contains("TTL"));
        plan.PlanWarnings.Should().Contain(w => w.Contains("delete fidelity"));
    }

    [Fact]
    public void Generate_CutoverRequiresCatchUp_IsReadyWithCaveats()
    {
        var analysis = HealthyAnalysis();
        analysis.CutoverWindow.Feasibility = CutoverFeasibility.RequiresPreCutoverCatchUp;
        analysis.CutoverWindow.TotalDowntime = null;

        var plan = CreateGenerator().Generate(analysis);

        plan.OverallReadiness.Should().Be(MigrationReadiness.ReadyWithCaveats);
        plan.EstimatedBusinessDowntime.Should().BeNull();
        plan.MinimumBusinessDowntime.Should().Be(TimeSpan.FromMinutes(18));
        plan.PlanWarnings.Should().Contain(w => w.Contains("Cutover downtime is unavailable"));
        plan.ReadinessFactors.Should().Contain(f => f.Contains("draining change-feed lag"));
    }

    [Fact]
    public void Generate_UnsustainableSync_IsNotReady()
    {
        var analysis = HealthyAnalysis();
        analysis.SyncEstimate.SteadyStateSustainable = false;
        analysis.SyncEstimate.OverallRisk = SyncSustainabilityRisk.Unsustainable;
        analysis.SyncEstimate.EstimatedBacklogCatchUpAfterInitialLoad = null;
        analysis.CutoverWindow.Feasibility = CutoverFeasibility.RequiresPreCutoverCatchUp;
        analysis.CutoverWindow.TotalDowntime = null;

        var plan = CreateGenerator().Generate(analysis);

        plan.OverallReadiness.Should().Be(MigrationReadiness.NotReady);
        plan.ReadinessFactors.Should().Contain(f => f.Contains("unsustainable"));
        // Catch-up unavailable ⇒ preparation duration unknown.
        plan.EstimatedElapsedPreparationDuration.Should().BeNull();
        plan.Phases[1].EstimatedDuration.Should().BeNull();
        plan.PlanWarnings.Should().Contain(w => w.Contains("Backlog catch-up is unavailable"));
    }

    [Fact]
    public void Generate_UnsustainableSyncButCutoverFeasible_FlagsInconsistency()
    {
        var analysis = HealthyAnalysis();
        analysis.SyncEstimate.SteadyStateSustainable = false;
        analysis.SyncEstimate.OverallRisk = SyncSustainabilityRisk.Unsustainable;
        // Contradictory: cutover claims feasible.
        analysis.CutoverWindow.Feasibility = CutoverFeasibility.Feasible;

        var plan = CreateGenerator().Generate(analysis);

        plan.OverallReadiness.Should().Be(MigrationReadiness.NotReady);
        plan.PlanWarnings.Should().Contain(w => w.Contains("Inconsistent upstream signals"));
    }

    [Fact]
    public void Generate_NoContainers_IsUnknown()
    {
        var analysis = new IncrementalMigrationAnalysis();

        var plan = CreateGenerator().Generate(analysis);

        plan.OverallReadiness.Should().Be(MigrationReadiness.Unknown);
        plan.ReadinessFactors.Should().Contain(f => f.Contains("No containers"));
    }

    [Fact]
    public void Generate_AlwaysIncludesDeleteAndRollbackRisks()
    {
        var plan = CreateGenerator().Generate(HealthyAnalysis());

        plan.KeyRisks.Should().Contain(r => r.Contains("never emits deletes"));
        plan.KeyRisks.Should().Contain(r => r.Contains("rollback"));
        plan.Phases.Single(p => p.Name == "Cutover").RollbackGuidance.Should().NotBeNullOrEmpty();
        plan.Phases.Single(p => p.Name == "Cutover Preparation").RollbackGuidance.Should().Contain("reverse-sync");
    }

    [Fact]
    public void Generate_SingleContainer_AddsConcentrationNote()
    {
        var plan = CreateGenerator().Generate(HealthyAnalysis());

        plan.Notes.Should().Contain(n => n.Contains("concentration risk"));
    }
}
