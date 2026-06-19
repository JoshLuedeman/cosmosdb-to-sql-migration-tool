using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.Feedback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class RecommendationRefinementServiceTests
{
    private const string Platform = "Azure SQL Database";

    private static RecommendationRefinementService CreateService(IFeedbackStore? store = null)
    {
        store ??= new Mock<IFeedbackStore>().Object;
        var feedback = new FeedbackCollectionService(
            store,
            new NullFeedbackTelemetrySink(),
            new FeedbackOptions(),
            NullLogger<FeedbackCollectionService>.Instance);
        return new RecommendationRefinementService(feedback);
    }

    // Builds an assessment whose derived WorkloadProfile is High complexity, Large size,
    // 10 containers, 1000 max RU — matching ComparableProfile() below.
    private static AssessmentResult Assessment(string platform, string tier)
    {
        const double sizeGb = 200; // 10 GB ≤ Large < 1024 GB
        long totalBytes = (long)(sizeGb * 1024 * 1024 * 1024);

        var containers = new List<ContainerAnalysis>();
        for (int i = 0; i < 10; i++)
        {
            containers.Add(new ContainerAnalysis
            {
                DocumentCount = 1000,
                SizeBytes = i == 0 ? totalBytes : 0,
                ProvisionedRUs = i == 0 ? 1000 : 0
            });
        }

        return new AssessmentResult
        {
            CosmosAnalysis = new CosmosDbAnalysis { Containers = containers },
            SqlAssessment = new SqlMigrationAssessment
            {
                RecommendedPlatform = platform,
                RecommendedTier = tier,
                Complexity = new MigrationComplexity { OverallComplexity = "High" }
            }
        };
    }

    private static WorkloadProfile ComparableProfile() => new()
    {
        ComplexityRating = MigrationComplexityRating.High,
        SizeBucket = WorkloadSizeBucket.Large,
        ContainerCount = 10,
        MaxProvisionedRUs = 1000
    };

    private static MigrationOutcome Outcome(
        string platform,
        string tier,
        bool satisfactory,
        decimal estimatedMonthly = 100m,
        decimal actualMonthly = 100m,
        WorkloadProfile? profile = null) => new()
    {
        Profile = profile ?? ComparableProfile(),
        Status = MigrationOutcomeStatus.Succeeded,
        DeployedPlatform = platform,
        DeployedTier = tier,
        PerformanceSatisfactory = satisfactory,
        EstimatedMonthlyCostUsd = estimatedMonthly,
        ActualMonthlyCostUsd = actualMonthly
    };

    private static List<MigrationOutcome> Repeat(MigrationOutcome template, int count)
    {
        var list = new List<MigrationOutcome>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(Outcome(
                template.DeployedPlatform,
                template.DeployedTier,
                template.PerformanceSatisfactory!.Value,
                template.EstimatedMonthlyCostUsd,
                template.ActualMonthlyCostUsd,
                template.Profile));
        }

        return list;
    }

    [Fact]
    public void Refine_NoHistory_DefersToBaseline()
    {
        var service = CreateService();

        var result = service.Refine(Assessment(Platform, "General Purpose"), new List<MigrationOutcome>());

        result.HasRefinement.Should().BeFalse();
        result.ChangedFromBaseline.Should().BeFalse();
        result.RefinedTier.Should().Be("General Purpose");
        result.PriorSimilarMigrationCount.Should().Be(0);
        result.Rationale.Should().Contain("No prior migration outcomes");
    }

    [Fact]
    public void Refine_FewerThanMinimumSimilar_DefersToBaseline()
    {
        var service = CreateService();
        var prior = Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 2);

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeFalse();
        result.PriorSimilarMigrationCount.Should().Be(2);
        result.Rationale.Should().Contain("comparable prior migration");
    }

    [Fact]
    public void Refine_NoConfigMeetsCandidateFloor_DefersToBaseline()
    {
        var service = CreateService();
        // Four similar outcomes but each on a distinct config (count 1 per config).
        var prior = new List<MigrationOutcome>
        {
            Outcome(Platform, "General Purpose", true),
            Outcome(Platform, "Business Critical", true),
            Outcome(Platform, "Hyperscale", true),
            Outcome("Azure SQL Managed Instance", "General Purpose", true)
        };

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeFalse();
        result.PriorSimilarMigrationCount.Should().Be(4);
        result.Rationale.Should().Contain("no single deployed configuration");
    }

    [Fact]
    public void Refine_HistorySupportsBaseline_ConfirmsWithoutChange()
    {
        var service = CreateService();
        var prior = Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 5);

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeTrue();
        result.ChangedFromBaseline.Should().BeFalse();
        result.RefinedTier.Should().Be("General Purpose");
        result.Rationale.Should().Contain("support the baseline");
    }

    [Fact]
    public void Refine_HistoryFavorsDifferentConfig_RefinesRecommendation()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>();
        prior.AddRange(Repeat(Outcome("Azure SQL Managed Instance", "Business Critical", satisfactory: true), 6));
        prior.AddRange(Repeat(Outcome(Platform, "General Purpose", satisfactory: false), 4));

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeTrue();
        result.ChangedFromBaseline.Should().BeTrue();
        result.RefinedPlatform.Should().Be("Azure SQL Managed Instance");
        result.RefinedTier.Should().Be("Business Critical");
        result.Rationale.Should().Contain("refining the baseline");
    }

    [Fact]
    public void Refine_SmallHighSatisfactionGroup_DoesNotBeatLargerReliableGroup()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>();
        // Tiny perfect group (3/3) vs larger reliable group (7/8). Wilson lower bound favors the larger.
        prior.AddRange(Repeat(Outcome(Platform, "Business Critical", satisfactory: true), 3));
        prior.AddRange(Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 7));
        prior.Add(Outcome(Platform, "General Purpose", satisfactory: false));

        // Baseline is intentionally neither, so a tie-break can't mask the ranking.
        var result = service.Refine(Assessment(Platform, "Hyperscale"), prior);

        result.HasRefinement.Should().BeTrue();
        result.RefinedTier.Should().Be("General Purpose");
    }

    [Fact]
    public void Refine_TieBetweenConfigs_FavorsBaseline()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>();
        prior.AddRange(Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 4));
        prior.AddRange(Repeat(Outcome(Platform, "Business Critical", satisfactory: true), 4));

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeTrue();
        result.ChangedFromBaseline.Should().BeFalse();
        result.RefinedTier.Should().Be("General Purpose");
    }

    [Fact]
    public void Refine_LargeConsistentHistory_YieldsHighConfidence()
    {
        var service = CreateService();
        var prior = Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 8);

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.Confidence.Should().Be(RefinementConfidence.High);
        result.ObservedSatisfactionRate.Should().Be(1.0);
    }

    [Fact]
    public void Refine_ModerateHistory_YieldsMediumConfidence()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>();
        prior.AddRange(Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 4));
        prior.Add(Outcome(Platform, "General Purpose", satisfactory: false));

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.Confidence.Should().Be(RefinementConfidence.Medium);
    }

    [Fact]
    public void Refine_MinimalHistory_YieldsLowConfidence()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>
        {
            Outcome(Platform, "General Purpose", true),
            Outcome(Platform, "General Purpose", true),
            Outcome(Platform, "General Purpose", false)
        };

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeTrue();
        result.Confidence.Should().Be(RefinementConfidence.Low);
    }

    [Fact]
    public void Refine_ExcludesFailedAndUnassessedOutcomes()
    {
        var service = CreateService();
        var prior = new List<MigrationOutcome>
        {
            // Failed migration — excluded even though deployed config is present.
            new()
            {
                Profile = ComparableProfile(),
                Status = MigrationOutcomeStatus.Failed,
                DeployedPlatform = Platform,
                DeployedTier = "General Purpose",
                PerformanceSatisfactory = true
            },
            // No performance signal — excluded.
            new()
            {
                Profile = ComparableProfile(),
                Status = MigrationOutcomeStatus.Succeeded,
                DeployedPlatform = Platform,
                DeployedTier = "General Purpose",
                PerformanceSatisfactory = null
            }
        };

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.HasRefinement.Should().BeFalse();
        result.PriorSimilarMigrationCount.Should().Be(0);
    }

    [Fact]
    public void Refine_IncludesCostVarianceInRationale()
    {
        var service = CreateService();
        var prior = Repeat(Outcome(Platform, "General Purpose", satisfactory: true, estimatedMonthly: 100m, actualMonthly: 130m), 5);

        var result = service.Refine(Assessment(Platform, "General Purpose"), prior);

        result.AverageMonthlyCostVariancePercent.Should().BeApproximately(30.0, 1e-6);
        result.Rationale.Should().Contain("above estimate");
    }

    [Fact]
    public async Task RefineAsync_ReadsOutcomesFromStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cosmos2sql-refine-tests", Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(tempDir, "outcomes.jsonl");
        try
        {
            var store = new LocalJsonFeedbackStore(
                new FeedbackOptions { StorePath = storePath }, NullLogger<LocalJsonFeedbackStore>.Instance);
            foreach (var outcome in Repeat(Outcome(Platform, "General Purpose", satisfactory: true), 5))
            {
                await store.AppendAsync(outcome);
            }

            var service = CreateService(store);

            var result = await service.RefineAsync(Assessment(Platform, "General Purpose"));

            result.HasRefinement.Should().BeTrue();
            result.PriorSimilarMigrationCount.Should().Be(5);
            result.RefinedTier.Should().Be("General Purpose");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RefineAsync_NullAssessment_Throws()
    {
        var service = CreateService();

        var act = () => service.RefineAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
