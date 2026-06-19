using System.IO.Compression;
using System.Text;
using CosmosToSqlAssessment.Models.Migration;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Reporting;

/// <summary>
/// Tests for the incremental (change-feed) migration rendering added to the Excel and Word reports (#139).
/// Verifies the "Incremental Migration" worksheet and the "Incremental Migration &amp; Sync Process" Word
/// section are produced when an <see cref="IncrementalMigrationAnalysis"/> is present, and degrade
/// gracefully when it is absent.
/// </summary>
public class IncrementalMigrationReportTests : TestBase, IDisposable
{
    private readonly ReportGenerationService _service;
    private readonly string _tempDirectory;

    /// <summary>
    /// Initializes the report service and a unique temporary output directory for generated artifacts.
    /// </summary>
    public IncrementalMigrationReportTests()
    {
        var logger = CreateMockLogger<ReportGenerationService>();
        _service = new ReportGenerationService(MockConfiguration.Object, logger.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlIncReport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithIncrementalMigration_ExcelContainsWorksheetAndData()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        assessmentResult.IncrementalMigration = BuildSampleIncrementalMigration();

        // Act
        var (excelPaths, _, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        excelPaths.Should().NotBeEmpty();
        var workbookXml = ReadAllXmlText(excelPaths.First());
        workbookXml.Should().Contain("Incremental Migration"); // worksheet name in xl/workbook.xml
        workbookXml.Should().Contain("Change Feed Availability");
        workbookXml.Should().Contain("Initial Load vs Incremental Sync");
        workbookXml.Should().Contain("Cutover Downtime Window");
        workbookXml.Should().Contain("Phased Migration Plan");
        workbookXml.Should().Contain("Time-Based Partitioning");
        workbookXml.Should().Contain("Change Feed Processor Guidance");
        workbookXml.Should().Contain("orders"); // per-container row rendered
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithIncrementalMigration_WordContainsSyncSection()
    {
        // Arrange
        var assessmentResult = CreateSampleAssessmentResult();
        assessmentResult.IncrementalMigration = BuildSampleIncrementalMigration();

        // Act
        var (_, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert
        File.Exists(wordPath).Should().BeTrue();
        var documentXml = ReadWordDocumentXml(wordPath);
        documentXml.Should().Contain("Incremental Migration and Sync Process");
        documentXml.Should().Contain("Migration Readiness");
        documentXml.Should().Contain("Change Feed Availability");
        documentXml.Should().Contain("Initial Load vs Incremental Sync");
        documentXml.Should().Contain("Cutover Downtime Window");
        documentXml.Should().Contain("Phased Migration Plan");
        documentXml.Should().Contain("Time-Based Partitioning");
        documentXml.Should().Contain("Change Feed Processor Guidance");
    }

    [Fact]
    public async Task GenerateAssessmentReportAsync_WithoutIncrementalMigration_ExcelShowsNotAvailableAndWordSkipsSection()
    {
        // Arrange – the default sample has no IncrementalMigration analysis.
        var assessmentResult = CreateSampleAssessmentResult();
        assessmentResult.IncrementalMigration.Should().BeNull();

        // Act
        var (excelPaths, wordPath, _) = await _service.GenerateAssessmentReportAsync(assessmentResult, _tempDirectory);

        // Assert – worksheet is still added (with an unavailable note); Word section is skipped.
        var workbookXml = ReadAllXmlText(excelPaths.First());
        workbookXml.Should().Contain("Incremental Migration");
        workbookXml.Should().Contain("not available");

        var documentXml = ReadWordDocumentXml(wordPath);
        documentXml.Should().NotContain("Incremental Migration and Sync Process");
        documentXml.Should().NotContain("Migration Readiness");
    }

    private static IncrementalMigrationAnalysis BuildSampleIncrementalMigration()
    {
        return new IncrementalMigrationAnalysis
        {
            ChangeFeed = new ChangeFeedAvailabilityAnalysis
            {
                AllContainersSupportLatestVersionIncrementalSync = true,
                AnyContainerHasKnownServerSideDeletes = true,
                DeletePropagationRequiresExternalValidation = true,
                GlobalWarnings = { "Latest-version change feed never emits hard deletes." },
                Containers =
                {
                    new ContainerChangeFeedReadiness
                    {
                        ContainerName = "orders",
                        LatestVersionChangeFeedAvailable = true,
                        RecommendedMode = ChangeFeedMode.AllVersionsAndDeletes,
                        TimeToLiveEnabled = true,
                        KnownServerSideTtlDeletes = true,
                        FeedRangeCount = 4
                    }
                }
            },
            SyncEstimate = new IncrementalSyncEstimate
            {
                OverallRisk = SyncSustainabilityRisk.Moderate,
                SteadyStateSustainable = true,
                DailyDocumentChangeRatePercent = 5.0,
                SyncInterval = TimeSpan.FromMinutes(15),
                InitialLoadDuration = TimeSpan.FromHours(2),
                EstimatedBacklogCatchUpAfterInitialLoad = TimeSpan.FromMinutes(30),
                EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(16),
                EstimatedTotalChangedDocumentsPerDay = 50_000,
                HighestRiskContainers = { "orders" },
                Assumptions = { "Assumed 5% daily churn." },
                Containers =
                {
                    new ContainerIncrementalSyncEstimate
                    {
                        ContainerName = "orders",
                        DocumentCount = 1_000_000,
                        InitialLoadDuration = TimeSpan.FromHours(2),
                        InitialLoadThroughputKnown = true,
                        UtilizationPercent = 60.0,
                        Risk = SyncSustainabilityRisk.Moderate,
                        SteadyStateSustainable = true,
                        EstimatedBacklogCatchUp = TimeSpan.FromMinutes(30),
                        EstimatedSteadyStateSyncLag = TimeSpan.FromMinutes(16)
                    }
                }
            },
            CutoverWindow = new CutoverWindowEstimate
            {
                Feasibility = CutoverFeasibility.Feasible,
                Risk = CutoverDowntimeRisk.Low,
                TotalDowntime = TimeSpan.FromMinutes(25),
                MinimumKnownDowntime = TimeSpan.FromMinutes(20),
                FixedOverheadDuration = TimeSpan.FromMinutes(20),
                ParallelDrainDuration = TimeSpan.FromMinutes(3),
                FullyContendedDrainDuration = TimeSpan.FromMinutes(5),
                TargetDowntime = TimeSpan.FromMinutes(30),
                Assumptions = { "Source quiesced before final drain." }
            },
            Plan = new PhasedMigrationPlan
            {
                OverallReadiness = MigrationReadiness.ReadyWithCaveats,
                EstimatedElapsedPreparationDuration = TimeSpan.FromHours(2.5),
                EstimatedBusinessDowntime = TimeSpan.FromMinutes(25),
                MinimumBusinessDowntime = TimeSpan.FromMinutes(20),
                ReadinessFactors = { "TTL deletes require external handling before cutover." },
                Phases =
                {
                    new MigrationPlanPhase
                    {
                        Order = 1,
                        Name = "Initial Bulk Load",
                        Objective = "Copy existing data to Azure SQL.",
                        PrimaryTooling = "Azure Data Factory",
                        EstimatedDuration = TimeSpan.FromHours(2)
                    }
                }
            },
            Partitioning = new TimeBasedPartitioningAnalysis
            {
                ContainersRecommendedForPartitioning = 1,
                Assumptions = { "Volume thresholds applied per appsettings." },
                Containers =
                {
                    new ContainerPartitioningRecommendation
                    {
                        ContainerName = "orders",
                        Strength = PartitioningStrength.Recommended,
                        RecommendedGranularity = PartitionGranularity.Day,
                        RecommendedPartitionColumn = "createdAt",
                        RequiresSyntheticCreationColumn = false,
                        SlidingWindow = SlidingWindowConsideration.ConsiderWithValidation,
                        InitialLoadParallelismUpperBound = 4
                    }
                }
            },
            ProcessorGuidance = new ChangeFeedProcessorGuidance
            {
                SuggestedLeaseContainerStartingRUs = 800,
                CheckpointStrategy = CheckpointStrategy.AutomaticPerBatch,
                CheckpointingNote = "At-least-once delivery requires idempotent upsert.",
                RecommendedInitialComputeInstances = 1,
                ComputeScaleOutCeilingSharedFleet = 4,
                ComputeScaleOutCeilingIndependentPools = 4,
                ScaleOutTrigger = "Scale on estimator lag.",
                AnyContainerRequiresAllVersionsAndDeletes = true,
                RequiresContinuousBackupForDeletes = true,
                Containers =
                {
                    new ContainerChangeFeedProcessorGuidance
                    {
                        ContainerName = "orders",
                        RecommendedMode = ChangeFeedMode.AllVersionsAndDeletes,
                        FeedRangeCount = 4,
                        FeedRangeCountKnown = true,
                        MaxUsefulComputeInstances = 4,
                        RequiresContinuousBackup = true,
                        RequiresIsolatedLeaseState = true,
                        DeleteHandlingNote = "AVAD surfaces delete tombstones within retention."
                    }
                },
                ImplementationSteps = { "Provision a dedicated lease container named leases." },
                Assumptions = { "One lease per feed range." },
                Warnings = { "AVAD requires continuous backup enabled." },
                RelationshipToDataFactoryWatermarkPipeline = { "Choose one incremental path per target table." }
            }
        };
    }

    private static string ReadAllXmlText(string xlsxPath)
    {
        File.Exists(xlsxPath).Should().BeTrue();
        var sb = new StringBuilder();
        using var archive = ZipFile.OpenRead(xlsxPath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            sb.Append(reader.ReadToEnd());
        }

        return sb.ToString();
    }

    private static string ReadWordDocumentXml(string docxPath)
    {
        File.Exists(docxPath).Should().BeTrue();
        using var archive = ZipFile.OpenRead(docxPath);
        var entry = archive.GetEntry("word/document.xml");
        entry.Should().NotBeNull();
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Removes the temporary output directory created for the test.
    /// </summary>
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
            // Ignore cleanup errors in tests.
        }

        GC.SuppressFinalize(this);
    }
}
