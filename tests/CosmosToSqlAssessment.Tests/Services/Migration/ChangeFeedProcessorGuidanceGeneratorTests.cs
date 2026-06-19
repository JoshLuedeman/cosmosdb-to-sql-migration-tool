using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class ChangeFeedProcessorGuidanceGeneratorTests : TestBase
{
    private ChangeFeedProcessorGuidanceGenerator CreateGenerator(int baseRUs = 400, int perLeaseRUs = 100)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncrementalMigration:ChangeFeedProcessorLeaseBaseRUs"] = baseRUs.ToString(),
                ["IncrementalMigration:ChangeFeedProcessorLeaseRUsPerLease"] = perLeaseRUs.ToString(),
            })
            .Build();
        return new ChangeFeedProcessorGuidanceGenerator(config, CreateMockLogger<ChangeFeedProcessorGuidanceGenerator>().Object);
    }

    private static IncrementalMigrationAnalysis AnalysisWith(params ContainerChangeFeedReadiness[] containers)
        => new()
        {
            ChangeFeed = new ChangeFeedAvailabilityAnalysis
            {
                Containers = containers.ToList(),
            },
        };

    private static ContainerChangeFeedReadiness Container(
        string name,
        ChangeFeedMode mode = ChangeFeedMode.LatestVersion,
        int feedRanges = 4)
        => new()
        {
            ContainerName = name,
            RecommendedMode = mode,
            FeedRangeCount = feedRanges,
        };

    [Fact]
    public void Constructor_WithNullArguments_Throws()
    {
        var config = new ConfigurationBuilder().Build();
        var actConfig = () => new ChangeFeedProcessorGuidanceGenerator(null!, CreateMockLogger<ChangeFeedProcessorGuidanceGenerator>().Object);
        var actLogger = () => new ChangeFeedProcessorGuidanceGenerator(config, null!);
        actConfig.Should().Throw<ArgumentNullException>();
        actLogger.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Generate_WithNullAnalysis_Throws()
    {
        var generator = CreateGenerator();
        var act = () => generator.Generate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Generate_LatestVersionContainers_RecommendsAutomaticCheckpointAndNoContinuousBackup()
    {
        var generator = CreateGenerator();
        var analysis = AnalysisWith(
            Container("orders", ChangeFeedMode.LatestVersion, feedRanges: 4),
            Container("customers", ChangeFeedMode.LatestVersion, feedRanges: 2));

        var guidance = generator.Generate(analysis);

        guidance.AnyContainerRequiresAllVersionsAndDeletes.Should().BeFalse();
        guidance.RequiresContinuousBackupForDeletes.Should().BeFalse();
        guidance.CheckpointStrategy.Should().Be(CheckpointStrategy.AutomaticPerBatch);
        guidance.RecommendedLeaseContainerName.Should().Be("leases");
        guidance.LeaseContainerPartitionKeyPath.Should().Be("/id");
        guidance.RecommendedInitialComputeInstances.Should().Be(1);
        guidance.Containers.Should().HaveCount(2);
        guidance.Containers.Should().OnlyContain(c => !c.RequiresContinuousBackup && !c.RequiresIsolatedLeaseState);
    }

    [Fact]
    public void Generate_LeaseRUs_UsesBasePlusPerLeaseTimesKnownRanges()
    {
        var generator = CreateGenerator(baseRUs: 400, perLeaseRUs: 100);
        var analysis = AnalysisWith(
            Container("a", feedRanges: 4),
            Container("b", feedRanges: 2));

        var guidance = generator.Generate(analysis);

        // 400 + 100 * (4 + 2) = 1000
        guidance.SuggestedLeaseContainerStartingRUs.Should().Be(1000);
        guidance.LeaseContainerUsesAutoscale.Should().BeTrue();
    }

    [Fact]
    public void Generate_LeaseRUs_NeverBelowMinimum()
    {
        var generator = CreateGenerator(baseRUs: 100, perLeaseRUs: 0);
        var analysis = AnalysisWith(Container("a", feedRanges: 1));

        var guidance = generator.Generate(analysis);

        guidance.SuggestedLeaseContainerStartingRUs.Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public void Generate_ComputeCeilings_ReflectSharedFleetAndIndependentPools()
    {
        var generator = CreateGenerator();
        var analysis = AnalysisWith(
            Container("big", feedRanges: 8),
            Container("small", feedRanges: 3));

        var guidance = generator.Generate(analysis);

        // Shared fleet = max(8, 3) = 8; independent pools = 8 + 3 = 11
        guidance.ComputeScaleOutCeilingSharedFleet.Should().Be(8);
        guidance.ComputeScaleOutCeilingIndependentPools.Should().Be(11);
        guidance.Containers.Single(c => c.ContainerName == "big").MaxUsefulComputeInstances.Should().Be(8);
        guidance.Containers.Single(c => c.ContainerName == "small").MaxUsefulComputeInstances.Should().Be(3);
    }

    [Fact]
    public void Generate_AllVersionsAndDeletesContainer_SetsContinuousBackupAndIsolatedLeaseFlags()
    {
        var generator = CreateGenerator();
        var analysis = AnalysisWith(
            Container("ttl-container", ChangeFeedMode.AllVersionsAndDeletes, feedRanges: 4),
            Container("plain", ChangeFeedMode.LatestVersion, feedRanges: 2));

        var guidance = generator.Generate(analysis);

        guidance.AnyContainerRequiresAllVersionsAndDeletes.Should().BeTrue();
        guidance.RequiresContinuousBackupForDeletes.Should().BeTrue();

        var avad = guidance.Containers.Single(c => c.ContainerName == "ttl-container");
        avad.RequiresContinuousBackup.Should().BeTrue();
        avad.RequiresIsolatedLeaseState.Should().BeTrue();
        avad.DeleteHandlingNote.Should().Contain("delete");

        var plain = guidance.Containers.Single(c => c.ContainerName == "plain");
        plain.RequiresContinuousBackup.Should().BeFalse();
        plain.RequiresIsolatedLeaseState.Should().BeFalse();

        guidance.Warnings.Should().Contain(w => w.Contains("continuous backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_UnknownFeedRangeCount_TreatedAsUnknownNotZero()
    {
        var generator = CreateGenerator(baseRUs: 400, perLeaseRUs: 100);
        var analysis = AnalysisWith(Container("unknown", feedRanges: 0));

        var guidance = generator.Generate(analysis);

        var container = guidance.Containers.Single();
        container.FeedRangeCountKnown.Should().BeFalse();
        container.MaxUsefulComputeInstances.Should().BeNull();
        container.Notes.Should().Contain(n => n.Contains("runtime", StringComparison.OrdinalIgnoreCase));

        // No known ranges -> ceilings unknown, lease RUs fall back to base only (never base + perLease*0 changes nothing here, but never negative)
        guidance.ComputeScaleOutCeilingSharedFleet.Should().BeNull();
        guidance.ComputeScaleOutCeilingIndependentPools.Should().BeNull();
        guidance.SuggestedLeaseContainerStartingRUs.Should().Be(400);
        guidance.RecommendedInitialComputeInstances.Should().Be(1);
        guidance.Warnings.Should().Contain(w => w.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_MixedKnownAndUnknownRanges_CountsOnlyKnownRanges()
    {
        var generator = CreateGenerator(baseRUs: 400, perLeaseRUs: 100);
        var analysis = AnalysisWith(
            Container("known", feedRanges: 5),
            Container("unknown", feedRanges: 0));

        var guidance = generator.Generate(analysis);

        // Only the known 5 ranges count: 400 + 100*5 = 900
        guidance.SuggestedLeaseContainerStartingRUs.Should().Be(900);
        guidance.ComputeScaleOutCeilingSharedFleet.Should().Be(5);
        guidance.ComputeScaleOutCeilingIndependentPools.Should().Be(5);
    }

    [Fact]
    public void Generate_EmptyContainerList_ProducesDefaultsWithWarning()
    {
        var generator = CreateGenerator();
        var analysis = AnalysisWith();

        var guidance = generator.Generate(analysis);

        guidance.Containers.Should().BeEmpty();
        guidance.SuggestedLeaseContainerStartingRUs.Should().Be(400);
        guidance.ComputeScaleOutCeilingSharedFleet.Should().BeNull();
        guidance.ComputeScaleOutCeilingIndependentPools.Should().BeNull();
        guidance.Warnings.Should().Contain(w => w.Contains("No containers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_AlwaysPopulatesGuidanceNarrative()
    {
        var generator = CreateGenerator();
        var analysis = AnalysisWith(Container("orders", feedRanges: 4));

        var guidance = generator.Generate(analysis);

        guidance.ImplementationSteps.Should().NotBeEmpty();
        guidance.Assumptions.Should().NotBeEmpty();
        guidance.RelationshipToDataFactoryWatermarkPipeline.Should().NotBeEmpty();
        guidance.CheckpointingNote.Should().NotBeNullOrWhiteSpace();
        guidance.ScaleOutTrigger.Should().NotBeNullOrWhiteSpace();
    }
}
