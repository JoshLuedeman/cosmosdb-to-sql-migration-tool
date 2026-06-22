using System.Text.Json;
using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Orchestration;
using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CosmosToSqlAssessment.Tests.Orchestration;

/// <summary>
/// Tests for the additive <c>feedback record --import-outcome</c> CLI path (#259): it imports an
/// anonymized <see cref="MigrationOutcome"/> into the local store, gated by the existing opt-in consent
/// (default OFF), and a recorded outcome is then picked up by <see cref="RecommendationRefinementService"/>.
/// </summary>
public class FeedbackRecordCommandTests
{
    private static MigrationOutcome ComparableSuccessfulOutcome() => new()
    {
        Profile = new WorkloadProfile
        {
            ComplexityRating = MigrationComplexityRating.High,
            SizeBucket = WorkloadSizeBucket.Large,
            ContainerCount = 10,
            MaxProvisionedRUs = 1000,
            RecommendedPlatform = "Azure SQL Database",
            RecommendedTier = "General Purpose",
        },
        Status = MigrationOutcomeStatus.Succeeded,
        DeployedPlatform = "Azure SQL Database",
        DeployedTier = "General Purpose",
        PerformanceSatisfactory = true,
        EstimatedMonthlyCostUsd = 100m,
        ActualMonthlyCostUsd = 100m,
    };

    // Builds an assessment whose derived WorkloadProfile matches ComparableSuccessfulOutcome().
    private static AssessmentResult ComparableAssessment()
    {
        const double sizeGb = 200; // Large band (10 GB ≤ Large < 1024 GB)
        long totalBytes = (long)(sizeGb * 1024 * 1024 * 1024);

        var containers = new List<ContainerAnalysis>();
        for (int i = 0; i < 10; i++)
        {
            containers.Add(new ContainerAnalysis
            {
                DocumentCount = 1000,
                SizeBytes = i == 0 ? totalBytes : 0,
                ProvisionedRUs = i == 0 ? 1000 : 0,
            });
        }

        return new AssessmentResult
        {
            CosmosAnalysis = new CosmosDbAnalysis { Containers = containers },
            SqlAssessment = new SqlMigrationAssessment
            {
                RecommendedPlatform = "Azure SQL Database",
                RecommendedTier = "General Purpose",
                Complexity = new MigrationComplexity { OverallComplexity = "High" },
            },
        };
    }

    private static string WriteOutcomeFile(string dir, MigrationOutcome outcome)
    {
        var path = Path.Combine(dir, $"outcome-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(outcome));
        return path;
    }

    [Fact]
    public async Task FeedbackRecord_WhenConsentDenied_IsNoOpAndWritesNothing()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var storePath = Path.Combine(workDir, "outcomes.jsonl");
        try
        {
            // No FeedbackLoop:Enabled => consent denied by default.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeedbackLoop:StorePath"] = storePath,
                })
                .Build();
            var services = new ServiceCollection().AddCosmosAssessment(configuration);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

            var importPath = WriteOutcomeFile(workDir, ComparableSuccessfulOutcome());

            var exitCode = await orchestrator.RunAsync(
                new CliOptions { FeedbackRecord = true, ImportOutcomeFile = importPath },
                CancellationToken.None);

            // Consent-denied is a legitimate no-op: exit 0, but nothing persisted.
            exitCode.Should().Be(0);
            File.Exists(storePath).Should().BeFalse("a denied consent must persist nothing");
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FeedbackRecord_WithMissingFile_ReturnsOne()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".json");

        var exitCode = await orchestrator.RunAsync(
            new CliOptions { FeedbackRecord = true, ImportOutcomeFile = missing },
            CancellationToken.None);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task FeedbackRecord_WhenConsentGranted_RoundTripsAndRefinementPicksItUp()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var storePath = Path.Combine(workDir, "outcomes.jsonl");
        try
        {
            // FeedbackLoop:Enabled=true => consent granted via configuration precedence.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeedbackLoop:Enabled"] = "true",
                    ["FeedbackLoop:StorePath"] = storePath,
                })
                .Build();
            var services = new ServiceCollection().AddCosmosAssessment(configuration);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

            // Record the minimum number of comparable successful outcomes via the CLI path so the
            // refinement engine clears its similar/candidate sample floors. Each import file holds a
            // distinct OutcomeId so the store accumulates separate records.
            for (int i = 0; i < RecommendationRefinementService.MinimumSimilarSamples; i++)
            {
                var importPath = WriteOutcomeFile(workDir, ComparableSuccessfulOutcome());
                var exit = await orchestrator.RunAsync(
                    new CliOptions { FeedbackRecord = true, ImportOutcomeFile = importPath },
                    CancellationToken.None);
                exit.Should().Be(0);
            }

            File.Exists(storePath).Should().BeTrue("granted consent must persist the imported outcomes");

            // The recorded outcomes must now be visible to the refinement service that reads the store.
            var refiner = scope.ServiceProvider.GetRequiredService<RecommendationRefinementService>();
            var refinement = await refiner.RefineAsync(ComparableAssessment(), CancellationToken.None);

            refinement.HasRefinement.Should().BeTrue("the recorded comparable outcomes should drive a refinement");
            refinement.PriorSimilarMigrationCount.Should()
                .BeGreaterThanOrEqualTo(RecommendationRefinementService.MinimumSimilarSamples);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }
}
