using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

/// <summary>
/// A/B comparison proving the learning-refined recommendation is measurably more accurate than the
/// static baseline across a labeled, non-circular fixture (acceptance criterion for #220).
/// </summary>
public class RecommendationRefinementAbComparisonTests
{
    private const string SqlDb = "Azure SQL Database";
    private const string ManagedInstance = "Azure SQL Managed Instance";

    private static RecommendationRefinementService CreateService()
    {
        var feedback = new FeedbackCollectionService(
            new Mock<CosmosToSqlAssessment.Services.Feedback.IFeedbackStore>().Object,
            new CosmosToSqlAssessment.Services.Feedback.NullFeedbackTelemetrySink(),
            new FeedbackOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeedbackCollectionService>.Instance);
        return new RecommendationRefinementService(feedback);
    }

    private static AssessmentResult Assessment(string complexity, double sizeGb, int containers, int maxRu, string platform, string tier)
    {
        long totalBytes = (long)(sizeGb * 1024 * 1024 * 1024);
        var list = new List<ContainerAnalysis>();
        for (int i = 0; i < containers; i++)
        {
            list.Add(new ContainerAnalysis
            {
                DocumentCount = 1000,
                SizeBytes = i == 0 ? totalBytes : 0,
                ProvisionedRUs = i == 0 ? maxRu : 0
            });
        }

        return new AssessmentResult
        {
            CosmosAnalysis = new CosmosDbAnalysis { Containers = list },
            SqlAssessment = new SqlMigrationAssessment
            {
                RecommendedPlatform = platform,
                RecommendedTier = tier,
                Complexity = new MigrationComplexity { OverallComplexity = complexity }
            }
        };
    }

    private static WorkloadProfile ProfileFor(AssessmentResult assessment) => WorkloadProfile.FromAssessment(assessment);

    private static List<MigrationOutcome> History(WorkloadProfile profile, string platform, string tier, bool satisfactory, int count)
    {
        var list = new List<MigrationOutcome>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new MigrationOutcome
            {
                Profile = profile,
                Status = MigrationOutcomeStatus.Succeeded,
                DeployedPlatform = platform,
                DeployedTier = tier,
                PerformanceSatisfactory = satisfactory,
                EstimatedMonthlyCostUsd = 100m,
                ActualMonthlyCostUsd = 100m
            });
        }

        return list;
    }

    [Fact]
    public void EvaluateAccuracy_RefinedBeatsBaseline_OnLabeledFixture()
    {
        var service = CreateService();
        var scenarios = new List<RefinementScenario>();

        // Scenario 1 — refinement helps (platform AND tier change). Baseline GP is wrong; history
        // shows MI/Business Critical performed well for this large/high workload.
        var a1 = Assessment("High", 200, 10, 1000, SqlDb, "General Purpose");
        var h1 = new List<MigrationOutcome>();
        h1.AddRange(History(ProfileFor(a1), ManagedInstance, "Business Critical", satisfactory: true, count: 6));
        h1.AddRange(History(ProfileFor(a1), SqlDb, "General Purpose", satisfactory: false, count: 4));
        scenarios.Add(new RefinementScenario(a1, h1, ManagedInstance, "Business Critical"));

        // Scenario 2 — baseline already correct; history confirms it.
        var a2 = Assessment("Low", 5, 3, 200, SqlDb, "General Purpose");
        var h2 = History(ProfileFor(a2), SqlDb, "General Purpose", satisfactory: true, count: 5);
        scenarios.Add(new RefinementScenario(a2, h2, SqlDb, "General Purpose"));

        // Scenario 3 — no comparable history; refinement abstains and defers to the (correct) baseline.
        var a3 = Assessment("Medium", 50, 5, 400, SqlDb, "Hyperscale");
        var h3 = new List<MigrationOutcome>();
        scenarios.Add(new RefinementScenario(a3, h3, SqlDb, "Hyperscale"));

        // Scenario 4 — refinement helps (tier change). Baseline GP wrong; history favors Hyperscale.
        var a4 = Assessment("High", 1500, 12, 4000, SqlDb, "General Purpose");
        var h4 = History(ProfileFor(a4), SqlDb, "Hyperscale", satisfactory: true, count: 5);
        scenarios.Add(new RefinementScenario(a4, h4, SqlDb, "Hyperscale"));

        // Scenario 5 — noise resistance. A single high-satisfaction Business Critical sample is below
        // the per-candidate floor and must not beat the larger, reliable General Purpose group.
        var a5 = Assessment("Medium", 200, 8, 800, SqlDb, "General Purpose");
        var h5 = new List<MigrationOutcome>();
        h5.AddRange(History(ProfileFor(a5), SqlDb, "Business Critical", satisfactory: true, count: 1));
        h5.AddRange(History(ProfileFor(a5), SqlDb, "General Purpose", satisfactory: true, count: 4));
        scenarios.Add(new RefinementScenario(a5, h5, SqlDb, "General Purpose"));

        // --- Per-scenario decision assertions (not just aggregate accuracy) ---
        var r1 = service.Refine(a1, h1);
        r1.ChangedFromBaseline.Should().BeTrue();
        r1.RefinedPlatform.Should().Be(ManagedInstance);
        r1.RefinedTier.Should().Be("Business Critical");

        var r2 = service.Refine(a2, h2);
        r2.HasRefinement.Should().BeTrue();
        r2.ChangedFromBaseline.Should().BeFalse();
        r2.RefinedTier.Should().Be("General Purpose");

        var r3 = service.Refine(a3, h3);
        r3.HasRefinement.Should().BeFalse();
        r3.RefinedTier.Should().Be("Hyperscale");

        var r4 = service.Refine(a4, h4);
        r4.ChangedFromBaseline.Should().BeTrue();
        r4.RefinedTier.Should().Be("Hyperscale");

        var r5 = service.Refine(a5, h5);
        r5.RefinedTier.Should().Be("General Purpose");

        // --- Aggregate A/B comparison on the full (platform, tier) pair ---
        var comparison = service.EvaluateAccuracy(scenarios);

        comparison.ScenarioCount.Should().Be(5);
        comparison.BaselineCorrect.Should().Be(3);  // scenarios 2, 3, 5
        comparison.RefinedCorrect.Should().Be(5);   // all
        comparison.BaselineAccuracy.Should().BeApproximately(0.6, 1e-9);
        comparison.RefinedAccuracy.Should().Be(1.0);
        comparison.RefinedImprovesOverBaseline.Should().BeTrue();
        comparison.RefinedTierOnlyCorrect.Should().BeGreaterThanOrEqualTo(comparison.BaselineTierOnlyCorrect);
    }

    [Fact]
    public void EvaluateAccuracy_EmptyScenarioSet_Throws()
    {
        var service = CreateService();

        var act = () => service.EvaluateAccuracy(new List<RefinementScenario>());

        act.Should().Throw<ArgumentException>();
    }
}
