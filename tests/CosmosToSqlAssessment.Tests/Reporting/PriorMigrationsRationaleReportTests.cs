using CosmosToSqlAssessment.Tests.Infrastructure;
using DocumentFormat.OpenXml.Packaging;

namespace CosmosToSqlAssessment.Tests.Reporting;

/// <summary>
/// Tests for the "Recommendations Based on Prior Migrations" Word report section (issue #221),
/// which surfaces the attributable rationale produced by the opt-in feedback loop.
/// </summary>
public class PriorMigrationsRationaleReportTests : TestBase, IDisposable
{
    private readonly ReportGenerationService _service;
    private readonly string _tempDirectory;

    public PriorMigrationsRationaleReportTests()
    {
        var logger = CreateMockLogger<ReportGenerationService>();
        _service = new ReportGenerationService(MockConfiguration.Object, logger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void AssessmentResult_RecommendationRefinement_RoundTrips()
    {
        var refinement = new RecommendationRefinement
        {
            HasRefinement = true,
            PriorSimilarMigrationCount = 4,
            RefinedPlatform = "Azure SQL Managed Instance",
            RefinedTier = "Business Critical"
        };

        var result = new AssessmentResult { RecommendationRefinement = refinement };

        result.RecommendationRefinement.Should().BeSameAs(refinement);
        result.RecommendationRefinement!.HasRefinement.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithRefinement_WordContainsRationaleSection()
    {
        // Arrange
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = new RecommendationRefinement
        {
            HasRefinement = true,
            ChangedFromBaseline = true,
            PriorSimilarMigrationCount = 6,
            BaselinePlatform = "Azure SQL Database",
            BaselineTier = "General Purpose",
            RefinedPlatform = "Azure SQL Managed Instance",
            RefinedTier = "Business Critical",
            Confidence = RefinementConfidence.High,
            ObservedSatisfactionRate = 0.9,
            AverageMonthlyCostVariancePercent = -5.0,
            Rationale = "Based on 6 prior similar migrations, Azure SQL Managed Instance (Business Critical) performed satisfactorily."
        };

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().Contain("Recommendations Based on Prior Migrations");
        text.Should().Contain("Prior similar migrations analyzed: 6");
        text.Should().Contain("Confidence: High");
        text.Should().Contain("Azure SQL Managed Instance");
        text.Should().Contain("baseline was Azure SQL Database");
        text.Should().Contain("90%");
        text.Should().Contain("-5.0%");
        text.Should().Contain("Based on 6 prior similar migrations");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithConfirmingRefinement_OmitsBaselineComparison()
    {
        // Arrange – refinement confirms the baseline (no change)
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = new RecommendationRefinement
        {
            HasRefinement = true,
            ChangedFromBaseline = false,
            PriorSimilarMigrationCount = 3,
            BaselinePlatform = "Azure SQL Database",
            BaselineTier = "General Purpose",
            RefinedPlatform = "Azure SQL Database",
            RefinedTier = "General Purpose",
            Confidence = RefinementConfidence.Medium,
            Rationale = "Based on 3 prior similar migrations, the baseline recommendation is confirmed."
        };

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().Contain("Recommendations Based on Prior Migrations");
        text.Should().Contain("Recommendation confirmed");
        text.Should().NotContain("baseline was");
        // Null performance/cost values must be omitted, never rendered as zero.
        text.Should().NotContain("Observed performance-satisfaction rate");
        text.Should().NotContain("Average monthly cost variance");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithoutRefinement_OmitsRationaleSection()
    {
        // Arrange – no refinement attached (default run, feedback loop off)
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = null;

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().NotContain("Recommendations Based on Prior Migrations");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithNoRefinementResult_OmitsRationaleSection()
    {
        // Arrange – a "None" refinement (insufficient comparable history) must not render the section
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = RecommendationRefinement.None(
            "Azure SQL Database", "General Purpose", priorSimilarCount: 1,
            rationale: "Insufficient comparable history.");

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().NotContain("Recommendations Based on Prior Migrations");
    }

    private static string ReadWordBodyText(string wordPath)
    {
        File.Exists(wordPath).Should().BeTrue();
        using var document = WordprocessingDocument.Open(wordPath, false);
        return document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
        GC.SuppressFinalize(this);
    }
}
