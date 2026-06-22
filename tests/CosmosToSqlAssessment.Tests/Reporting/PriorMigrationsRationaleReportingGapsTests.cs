using ClosedXML.Excel;
using CosmosToSqlAssessment.Tests.Infrastructure;
using DocumentFormat.OpenXml.Packaging;

namespace CosmosToSqlAssessment.Tests.Reporting;

/// <summary>
/// Tests for the two reporting gaps closed by issue #260: surfacing the opt-in feedback-loop
/// refinement rationale (originally Word-only, single-DB only — see #221) in (a) the Excel
/// executive-summary worksheet and (b) the per-database subsections of multi-database Word reports.
/// These mirror <see cref="PriorMigrationsRationaleReportTests"/> and assert that default
/// (no-feedback) runs remain unchanged and that no misleading aggregate refinement is rendered.
/// </summary>
public class PriorMigrationsRationaleReportingGapsTests : TestBase, IDisposable
{
    private readonly ReportGenerationService _service;
    private readonly string _tempDirectory;

    public PriorMigrationsRationaleReportingGapsTests()
    {
        var logger = CreateMockLogger<ReportGenerationService>();
        _service = new ReportGenerationService(MockConfiguration.Object, logger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    private static RecommendationRefinement CreateChangedRefinement() => new()
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

    // ---- Excel executive-summary path (gap (a)) ----

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithRefinement_ExcelExecSummaryContainsRationale()
    {
        // Arrange
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = CreateChangedRefinement();

        // Act
        var (excelPaths, _, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadExcelWorksheetText(excelPaths.First(), "Executive Summary");
        text.Should().Contain("Based on Prior Migrations");
        text.Should().Contain("Refined Recommendation:");
        text.Should().Contain("Azure SQL Managed Instance");
        text.Should().Contain("Business Critical");
        text.Should().Contain("Azure SQL Database");
        text.Should().Contain("High");
        text.Should().Contain("Prior Similar Migrations:");
        text.Should().Contain("6");
        text.Should().Contain("90%");
        text.Should().Contain("-5.0%");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithConfirmingRefinement_ExcelOmitsOptionalMetrics()
    {
        // Arrange – refinement confirms the baseline (no change), null performance/cost values.
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
        var (excelPaths, _, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadExcelWorksheetText(excelPaths.First(), "Executive Summary");
        text.Should().Contain("Based on Prior Migrations");
        text.Should().Contain("Recommendation confirmed (unchanged from baseline)");
        // Null performance/cost values must be omitted, never rendered as zero.
        text.Should().NotContain("Observed Satisfaction Rate:");
        text.Should().NotContain("Avg Monthly Cost Variance:");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithoutRefinement_ExcelOmitsRationale()
    {
        // Arrange – no refinement attached (default run, feedback loop off).
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = null;

        // Act
        var (excelPaths, _, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadExcelWorksheetText(excelPaths.First(), "Executive Summary");
        text.Should().NotContain("Based on Prior Migrations");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithNoRefinementResult_ExcelOmitsRationale()
    {
        // Arrange – a "None" refinement (insufficient comparable history) must not render the block.
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = RecommendationRefinement.None(
            "Azure SQL Database", "General Purpose", priorSimilarCount: 1,
            rationale: "Insufficient comparable history.");

        // Act
        var (excelPaths, _, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadExcelWorksheetText(excelPaths.First(), "Executive Summary");
        text.Should().NotContain("Based on Prior Migrations");
    }

    // ---- Multi-database Word per-database path (gap (b)) ----

    [Fact]
    public async Task GenerateAssessmentReportAsync_MultiDatabase_WordContainsPerDatabaseRationale()
    {
        // Arrange – two databases; refinement attached to the first only. The combined top-level
        // result is intentionally left unrefined (no meaningful merged tier).
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = null;
        assessmentResult.IndividualDatabaseResults = new List<AssessmentResult>
        {
            TestDataFactory.CreateSampleAssessmentResult(),
            TestDataFactory.CreateSampleAssessmentResult()
        };
        assessmentResult.IndividualDatabaseResults[0].DatabaseName = "Database1";
        assessmentResult.IndividualDatabaseResults[0].RecommendationRefinement = CreateChangedRefinement();
        assessmentResult.IndividualDatabaseResults[1].DatabaseName = "Database2";
        assessmentResult.IndividualDatabaseResults[1].RecommendationRefinement = null;

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().Contain("Recommendations Based on Prior Migrations");
        text.Should().Contain("Prior similar migrations analyzed: 6");
        text.Should().Contain("Confidence: High");
        text.Should().Contain("Azure SQL Managed Instance");
        text.Should().Contain("baseline was Azure SQL Database");
        // Rendered for the one refined database only — never for the unrefined one or the combined result.
        CountOccurrences(text, "Recommendations Based on Prior Migrations").Should().Be(1);
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_MultiDatabase_WithoutAnyRefinement_WordOmitsRationale()
    {
        // Arrange – multi-database run with no refinements anywhere (default run).
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = null;
        assessmentResult.IndividualDatabaseResults = new List<AssessmentResult>
        {
            TestDataFactory.CreateSampleAssessmentResult(),
            TestDataFactory.CreateSampleAssessmentResult()
        };
        assessmentResult.IndividualDatabaseResults[0].DatabaseName = "Database1";
        assessmentResult.IndividualDatabaseResults[1].DatabaseName = "Database2";

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().NotContain("Recommendations Based on Prior Migrations");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_MultiDatabase_CombinedRefinementIsNotRendered()
    {
        // Arrange – a (spurious) refinement on the combined top-level result must NOT render in a
        // multi-database report; only per-database refinements are surfaced. This guards the intent
        // that there is no misleading aggregate refinement.
        var assessmentResult = TestDataFactory.CreateSampleAssessmentResult();
        assessmentResult.RecommendationRefinement = CreateChangedRefinement();
        assessmentResult.IndividualDatabaseResults = new List<AssessmentResult>
        {
            TestDataFactory.CreateSampleAssessmentResult(),
            TestDataFactory.CreateSampleAssessmentResult()
        };
        assessmentResult.IndividualDatabaseResults[0].DatabaseName = "Database1";
        assessmentResult.IndividualDatabaseResults[0].RecommendationRefinement = null;
        assessmentResult.IndividualDatabaseResults[1].DatabaseName = "Database2";
        assessmentResult.IndividualDatabaseResults[1].RecommendationRefinement = null;

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        var text = ReadWordBodyText(wordPath);
        text.Should().NotContain("Recommendations Based on Prior Migrations");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReadWordBodyText(string wordPath)
    {
        File.Exists(wordPath).Should().BeTrue();
        using var document = WordprocessingDocument.Open(wordPath, false);
        return document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }

    private static string ReadExcelWorksheetText(string excelPath, string worksheetName)
    {
        File.Exists(excelPath).Should().BeTrue();
        using var workbook = new XLWorkbook(excelPath);
        var worksheet = workbook.Worksheet(worksheetName);
        return string.Join("\n", worksheet.CellsUsed().Select(cell => cell.GetString()));
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
