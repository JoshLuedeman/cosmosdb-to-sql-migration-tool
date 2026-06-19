using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace CosmosToSqlAssessment.Tests.Services.Migration;

public class TimeBasedPartitioningAnalyzerTests : TestBase
{
    private TimeBasedPartitioningAnalyzer CreateAnalyzer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncrementalMigration:PartitioningMonthlyDocumentThreshold"] = "1000000",
                ["IncrementalMigration:PartitioningDailyDocumentThreshold"] = "50000000",
                ["IncrementalMigration:PartitioningMonthlySizeGB"] = "5.0",
                ["IncrementalMigration:PartitioningDailySizeGB"] = "100.0",
            })
            .Build();
        return new TimeBasedPartitioningAnalyzer(config, CreateMockLogger<TimeBasedPartitioningAnalyzer>().Object);
    }

    private static CosmosDbAnalysis CosmosWith(params ContainerAnalysis[] containers)
        => new() { Containers = containers.ToList() };

    private static ContainerAnalysis Container(
        string name,
        long docs,
        long sizeBytes,
        int feedRanges = 4,
        int? ttl = null,
        params DocumentSchema[] schemas)
        => new()
        {
            ContainerName = name,
            DocumentCount = docs,
            SizeBytes = sizeBytes,
            FeedRangeCount = feedRanges,
            DefaultTimeToLiveSeconds = ttl,
            DetectedSchemas = schemas.ToList(),
        };

    private static DocumentSchema Schema(double prevalence, params (string name, string sqlType)[] fields)
    {
        var schema = new DocumentSchema { SchemaName = "s", Prevalence = prevalence, SampleCount = 100 };
        foreach (var (n, t) in fields)
        {
            schema.Fields[n] = new FieldInfo
            {
                FieldName = n,
                RecommendedSqlType = t,
                DetectedTypes = new List<string> { "string" },
            };
        }

        return schema;
    }

    private const long GB = 1024L * 1024L * 1024L;

    [Fact]
    public void Constructor_WithNullArguments_Throws()
    {
        var config = new ConfigurationBuilder().Build();
        var actConfig = () => new TimeBasedPartitioningAnalyzer(null!, CreateMockLogger<TimeBasedPartitioningAnalyzer>().Object);
        var actLogger = () => new TimeBasedPartitioningAnalyzer(config, null!);
        actConfig.Should().Throw<ArgumentNullException>();
        actLogger.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analyze_WithNullAnalysis_Throws()
    {
        var analyzer = CreateAnalyzer();
        var act = () => analyzer.Analyze(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Analyze_WithNoContainers_ReturnsEmptyWithAssumptions()
    {
        var analyzer = CreateAnalyzer();

        var result = analyzer.Analyze(CosmosWith());

        result.Containers.Should().BeEmpty();
        result.ContainersRecommendedForPartitioning.Should().Be(0);
        result.Assumptions.Should().NotBeEmpty();
        result.Notes.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_EmptyContainer_IsNotRecommended()
    {
        var analyzer = CreateAnalyzer();

        var rec = analyzer.Analyze(CosmosWith(Container("empty", docs: 0, sizeBytes: 0))).Containers.Single();

        rec.Strength.Should().Be(PartitioningStrength.NotRecommended);
        rec.RecommendedGranularity.Should().Be(PartitionGranularity.None);
        rec.Rationale.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_SmallContainer_IsNotRecommended()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("small", docs: 50_000, sizeBytes: 100L * 1024 * 1024,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.Strength.Should().Be(PartitioningStrength.NotRecommended);
        rec.RecommendedGranularity.Should().Be(PartitionGranularity.None);
    }

    [Fact]
    public void Analyze_VeryLargeContainerWithImmutableColumn_IsRecommendedDailyWithColumn()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("events", docs: 60_000_000, sizeBytes: 10L * GB, feedRanges: 16,
            schemas: Schema(1.0, ("createdAt", "datetime2"), ("status", "nvarchar(50)")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RecommendedGranularity.Should().Be(PartitionGranularity.Day);
        rec.Strength.Should().Be(PartitioningStrength.Recommended);
        rec.RecommendedPartitionColumn.Should().Be("createdAt");
        rec.RequiresSyntheticCreationColumn.Should().BeFalse();
        rec.IndexAlignmentCaveats.Should().NotBeEmpty();
        rec.InitialLoadParallelismUpperBound.Should().Be(16);
        rec.InitialLoadSlicingApproach.Should().Contain("_ts");
    }

    [Fact]
    public void Analyze_MonthlyVolumeWithImmutableColumn_IsConditionalNotRecommendedStrength()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("orders", docs: 2_000_000, sizeBytes: 2L * GB,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RecommendedGranularity.Should().Be(PartitionGranularity.Month);
        // Has a recommended column, but only Day granularity earns the strong "Recommended" verdict.
        rec.Strength.Should().Be(PartitioningStrength.ConditionalManageability);
        rec.RecommendedPartitionColumn.Should().Be("createdAt");
    }

    [Fact]
    public void Analyze_YearVolume_IsConditional()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("logs", docs: 200_000, sizeBytes: 200L * 1024 * 1024,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RecommendedGranularity.Should().Be(PartitionGranularity.Year);
        rec.Strength.Should().Be(PartitioningStrength.ConditionalManageability);
    }

    [Fact]
    public void Analyze_NoImmutableColumn_RecommendsSyntheticColumn()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("audit", docs: 60_000_000, sizeBytes: 10L * GB,
            schemas: Schema(1.0, ("lastModified", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RequiresSyntheticCreationColumn.Should().BeTrue();
        rec.RecommendedPartitionColumn.Should().BeNull();
        rec.TemporalColumnCandidates.Should().Contain(c => c.IsSyntheticFromInitialLoad);

        var mutable = rec.TemporalColumnCandidates.Single(c => c.FieldName == "lastModified");
        mutable.MutabilityRisk.Should().Be(TemporalColumnMutabilityRisk.MutableLikely);
        mutable.Confidence.Should().Be(TemporalColumnConfidence.Low);
    }

    [Fact]
    public void Analyze_EmptySchemaLargeContainer_RecommendsSyntheticAndCaveats()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("nodata", docs: 60_000_000, sizeBytes: 10L * GB);

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RequiresSyntheticCreationColumn.Should().BeTrue();
        rec.TemporalColumnCandidates.Should().ContainSingle().Which.IsSyntheticFromInitialLoad.Should().BeTrue();
        rec.Caveats.Should().Contain(c => c.Contains("No document schema"));
    }

    [Fact]
    public void Analyze_TtlEnabled_FlagsSlidingWindowWithCaveat()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("ttl", docs: 60_000_000, sizeBytes: 10L * GB, ttl: 3600,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.SlidingWindow.Should().Be(SlidingWindowConsideration.ConsiderWithValidation);
        rec.Caveats.Should().Contain(c => c.Contains("TTL"));
    }

    [Fact]
    public void Analyze_ItemLevelTtl_MentionsNoContainerDefault()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("itemttl", docs: 60_000_000, sizeBytes: 10L * GB, ttl: -1,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.SlidingWindow.Should().Be(SlidingWindowConsideration.ConsiderWithValidation);
        rec.Caveats.Should().Contain(c => c.Contains("item-level"));
    }

    [Fact]
    public void Analyze_NoTtl_SlidingWindowNotApplicable()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("nottl", docs: 60_000_000, sizeBytes: 10L * GB, ttl: null,
            schemas: Schema(1.0, ("createdAt", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.SlidingWindow.Should().Be(SlidingWindowConsideration.NotApplicable);
    }

    [Fact]
    public void Analyze_TwoHighConfidenceColumns_DoesNotAutoSelectColumn()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("ambiguous", docs: 60_000_000, sizeBytes: 10L * GB,
            schemas: Schema(1.0, ("createdAt", "datetime2"), ("creationTime", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RecommendedPartitionColumn.Should().BeNull();
        rec.RequiresSyntheticCreationColumn.Should().BeFalse();
        rec.Strength.Should().Be(PartitioningStrength.ConditionalManageability);
    }

    [Fact]
    public void Analyze_SparseImmutableColumn_IsNotAutoSelected()
    {
        var analyzer = CreateAnalyzer();
        // createdAt appears in only one of two equally-prevalent schema variants => ~50% prevalence.
        var container = Container("sparse", docs: 60_000_000, sizeBytes: 10L * GB,
            schemas: new[]
            {
                Schema(0.5, ("createdAt", "datetime2")),
                Schema(0.5, ("name", "nvarchar(100)")),
            });

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        rec.RecommendedPartitionColumn.Should().BeNull();
        var createdAt = rec.TemporalColumnCandidates.Single(c => c.FieldName == "createdAt");
        createdAt.Confidence.Should().Be(TemporalColumnConfidence.Medium);
        createdAt.SchemaPrevalence.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Analyze_EventColumn_IsMediumImmutable()
    {
        var analyzer = CreateAnalyzer();
        var container = Container("telemetry", docs: 60_000_000, sizeBytes: 10L * GB,
            schemas: Schema(1.0, ("eventTime", "datetime2")));

        var rec = analyzer.Analyze(CosmosWith(container)).Containers.Single();

        var candidate = rec.TemporalColumnCandidates.Single(c => c.FieldName == "eventTime");
        candidate.Confidence.Should().Be(TemporalColumnConfidence.Medium);
        candidate.MutabilityRisk.Should().Be(TemporalColumnMutabilityRisk.ImmutableLikely);
        // Medium (not High) => not auto-selected as the single partition column.
        rec.RecommendedPartitionColumn.Should().BeNull();
    }

    [Fact]
    public void Analyze_AggregatesRecommendedCount()
    {
        var analyzer = CreateAnalyzer();
        var big = Container("big", docs: 60_000_000, sizeBytes: 10L * GB,
            schemas: Schema(1.0, ("createdAt", "datetime2")));
        var small = Container("small", docs: 10, sizeBytes: 1024);

        var result = analyzer.Analyze(CosmosWith(big, small));

        result.Containers.Should().HaveCount(2);
        result.ContainersRecommendedForPartitioning.Should().Be(1);
    }
}
