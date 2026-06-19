using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class ChangeFeedAvailabilityAnalyzerTests : TestBase
{
    private ChangeFeedAvailabilityAnalyzer CreateAnalyzer()
        => new(CreateMockLogger<ChangeFeedAvailabilityAnalyzer>().Object);

    private static CosmosDbAnalysis AnalysisWith(params ContainerAnalysis[] containers)
        => new() { Containers = containers.ToList() };

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var act = () => new ChangeFeedAvailabilityAnalyzer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analyze_WithNullAnalysis_Throws()
    {
        var analyzer = CreateAnalyzer();
        var act = () => analyzer.Analyze(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analyze_WithNoContainers_ReturnsEmptyWithGlobalWarnings()
    {
        var analyzer = CreateAnalyzer();

        var result = analyzer.Analyze(AnalysisWith());

        result.Containers.Should().BeEmpty();
        result.AllContainersSupportLatestVersionIncrementalSync.Should().BeTrue();
        result.AnyContainerHasKnownServerSideDeletes.Should().BeFalse();
        result.DeletePropagationRequiresExternalValidation.Should().BeTrue();
        // The "latest-version never emits deletes" and "all-versions requires manual verification"
        // global warnings are always present.
        result.GlobalWarnings.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Analyze_ContainerWithoutTtl_RecommendsLatestVersion()
    {
        var analyzer = CreateAnalyzer();
        var container = new ContainerAnalysis
        {
            ContainerName = "orders",
            PartitionKey = "/customerId",
            DefaultTimeToLiveSeconds = null,
            FeedRangeCount = 4,
        };

        var readiness = analyzer.Analyze(AnalysisWith(container)).Containers.Single();

        readiness.LatestVersionChangeFeedAvailable.Should().BeTrue();
        readiness.RecommendedMode.Should().Be(ChangeFeedMode.LatestVersion);
        readiness.TimeToLiveEnabled.Should().BeFalse();
        readiness.DefaultTtlExpirationEnabled.Should().BeFalse();
        readiness.ItemLevelTtlPossible.Should().BeFalse();
        readiness.KnownServerSideTtlDeletes.Should().BeFalse();
        readiness.DeletePropagationSupportedByLatestVersion.Should().BeFalse();
        readiness.RequiresDeleteHandlingValidation.Should().BeTrue();
        readiness.AllVersionsAndDeletesAvailability.Should().Be(ChangeFeedModeAvailability.RequiresManualVerification);
        readiness.FeedRangeCount.Should().Be(4);
        readiness.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ContainerWithDefaultTtl_RecommendsAllVersionsAndWarns()
    {
        var analyzer = CreateAnalyzer();
        var container = new ContainerAnalysis
        {
            ContainerName = "sessions",
            PartitionKey = "/sessionId",
            DefaultTimeToLiveSeconds = 3600,
        };

        var result = analyzer.Analyze(AnalysisWith(container));
        var readiness = result.Containers.Single();

        readiness.TimeToLiveEnabled.Should().BeTrue();
        readiness.DefaultTtlExpirationEnabled.Should().BeTrue();
        readiness.ItemLevelTtlPossible.Should().BeFalse();
        readiness.KnownServerSideTtlDeletes.Should().BeTrue();
        readiness.RecommendedMode.Should().Be(ChangeFeedMode.AllVersionsAndDeletes);
        readiness.Warnings.Should().ContainSingle(w => w.Contains("TTL"));
        result.AnyContainerHasKnownServerSideDeletes.Should().BeTrue();
        result.GlobalWarnings.Should().Contain(w => w.Contains("TTL enabled"));
    }

    [Fact]
    public void Analyze_ContainerWithItemLevelTtl_FlagsItemLevelTtlPossible()
    {
        var analyzer = CreateAnalyzer();
        var container = new ContainerAnalysis
        {
            ContainerName = "events",
            PartitionKey = "/deviceId",
            DefaultTimeToLiveSeconds = -1,
        };

        var readiness = analyzer.Analyze(AnalysisWith(container)).Containers.Single();

        readiness.TimeToLiveEnabled.Should().BeTrue();
        readiness.DefaultTtlExpirationEnabled.Should().BeFalse();
        readiness.ItemLevelTtlPossible.Should().BeTrue();
        readiness.KnownServerSideTtlDeletes.Should().BeTrue();
        readiness.RecommendedMode.Should().Be(ChangeFeedMode.AllVersionsAndDeletes);
    }

    [Fact]
    public void Analyze_HierarchicalPartitionKey_IsDetected()
    {
        var analyzer = CreateAnalyzer();
        var container = new ContainerAnalysis
        {
            ContainerName = "telemetry",
            PartitionKey = "/tenantId",
            PartitionKeyPaths = new List<string> { "/tenantId", "/deviceId" },
        };

        var readiness = analyzer.Analyze(AnalysisWith(container)).Containers.Single();

        readiness.IsHierarchicalPartitionKey.Should().BeTrue();
        readiness.PartitionKeyPathCount.Should().Be(2);
        readiness.PartitionKeyPaths.Should().BeEquivalentTo(new[] { "/tenantId", "/deviceId" });
    }

    [Fact]
    public void Analyze_SinglePartitionKey_FallsBackToPartitionKeyPath()
    {
        var analyzer = CreateAnalyzer();
        var container = new ContainerAnalysis
        {
            ContainerName = "products",
            PartitionKey = "/categoryId",
            PartitionKeyPaths = new List<string>(),
        };

        var readiness = analyzer.Analyze(AnalysisWith(container)).Containers.Single();

        readiness.IsHierarchicalPartitionKey.Should().BeFalse();
        readiness.PartitionKeyPathCount.Should().Be(1);
        readiness.PartitionKeyPaths.Should().ContainSingle().Which.Should().Be("/categoryId");
    }

    [Fact]
    public void Analyze_MixedContainers_AggregatesFlags()
    {
        var analyzer = CreateAnalyzer();
        var noTtl = new ContainerAnalysis { ContainerName = "a", PartitionKey = "/id", DefaultTimeToLiveSeconds = null };
        var ttl = new ContainerAnalysis { ContainerName = "b", PartitionKey = "/id", DefaultTimeToLiveSeconds = 600 };

        var result = analyzer.Analyze(AnalysisWith(noTtl, ttl));

        result.Containers.Should().HaveCount(2);
        result.AllContainersSupportLatestVersionIncrementalSync.Should().BeTrue();
        result.AnyContainerHasKnownServerSideDeletes.Should().BeTrue();
    }
}
