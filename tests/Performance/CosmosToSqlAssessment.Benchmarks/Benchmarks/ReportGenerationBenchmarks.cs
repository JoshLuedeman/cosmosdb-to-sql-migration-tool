using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Benchmarks.Fixtures;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Microbenchmarks for the report-generation pipeline exercised by parent #79 / #176.
///
/// Three benchmarks:
/// * <see cref="GenerateAssessmentReportAsync_EndToEnd"/> — runs the full public
///   <c>ReportGenerationService.GenerateAssessmentReportAsync</c> pipeline (Excel + Word writes to
///   disk via ClosedXML and OpenXml) over Small / Medium / Large <see cref="AssessmentResult"/>
///   fixtures.
///
///   This benchmark includes filesystem I/O and document serialisation, so wall-clock variance is
///   higher than CPU-only benchmarks (filesystem cache, OS write-back, AV / indexer interference).
///   Use it for large-regression detection (e.g. tracking trends across releases), not tight
///   percentage comparisons between runs. <c>MemoryDiagnoser</c> captures managed allocation
///   pressure only — native and filesystem-cache pressure are not measured.
///
///   The benchmark covers the single-database report path (fixture leaves
///   <c>IndividualDatabaseResults</c> empty). A multi-database benchmark could be added as a
///   follow-up if profiling shows different scaling characteristics.
///
/// * <see cref="SanitizeFileName_Bank"/> — pure-CPU bank loop over realistic database names.
/// * <see cref="CreateValidWorksheetName_Bank"/> — pure-CPU bank loop covering every branch of
///   the worksheet-name sanitiser (invalid chars, length > 31, "Multiple Databases (N)" regex,
///   name + suffix truncation).
///
/// All three return a digest so BenchmarkDotNet cannot dead-code-eliminate the work.
/// </summary>
[MemoryDiagnoser]
public class ReportGenerationBenchmarks
{
    private const int FileNamesPerInvoke = 100;
    private const int WorksheetNamesPerInvoke = 100;

    private ReportGenerationService _service = null!;
    private AssessmentResult _smallResult = null!;
    private AssessmentResult _mediumResult = null!;
    private AssessmentResult _largeResult = null!;
    private string[] _fileNameBank = null!;
    private (string baseName, string suffix)[] _worksheetNameBank = null!;
    private string _tempDir = null!;
    private int _reportRunId;

    [Params(SqlAssessmentFixtures.AnalysisSize.Small, SqlAssessmentFixtures.AnalysisSize.Medium, SqlAssessmentFixtures.AnalysisSize.Large)]
    public SqlAssessmentFixtures.AnalysisSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        _service = new ReportGenerationService(configuration, NullLogger<ReportGenerationService>.Instance);

        _smallResult = ReportGenerationFixtures.BuildAssessmentResult(SqlAssessmentFixtures.AnalysisSize.Small);
        _mediumResult = ReportGenerationFixtures.BuildAssessmentResult(SqlAssessmentFixtures.AnalysisSize.Medium);
        _largeResult = ReportGenerationFixtures.BuildAssessmentResult(SqlAssessmentFixtures.AnalysisSize.Large);

        _fileNameBank = ReportGenerationFixtures.BuildFileNameInputs();
        _worksheetNameBank = ReportGenerationFixtures.BuildWorksheetNameInputs();

        _tempDir = Path.Combine(Path.GetTempPath(), $"cosmos-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [IterationCleanup(Target = nameof(GenerateAssessmentReportAsync_EndToEnd))]
    public void CleanupRuns()
    {
        // Delete accumulated per-run subdirectories so disk usage stays bounded across BDN's
        // many measurement iterations. Tolerant of transient AV / indexer file-handle locks.
        try
        {
            if (!Directory.Exists(_tempDir))
            {
                return;
            }

            foreach (var subDir in Directory.EnumerateDirectories(_tempDir))
            {
                try
                {
                    Directory.Delete(subDir, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GlobalCleanup]
    public void Teardown()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Benchmark]
    public string GenerateAssessmentReportAsync_EndToEnd()
    {
        // Unique per-invocation directory so each call writes to its own folder. Avoids the
        // timestamp-collision bug in ReportGenerationService (folder name derived from
        // DateTime.Now.ToString("yyyy-MM-dd__HH-mm-ss") — multiple iterations within the same
        // second would otherwise overwrite each other and bias the measurement.
        var runDir = Path.Combine(_tempDir, Interlocked.Increment(ref _reportRunId).ToString("D8"));

        var fixture = Size switch
        {
            SqlAssessmentFixtures.AnalysisSize.Small => _smallResult,
            SqlAssessmentFixtures.AnalysisSize.Medium => _mediumResult,
            _ => _largeResult
        };

        var (_, wordPath, _) = _service.GenerateAssessmentReportAsync(fixture, runDir).GetAwaiter().GetResult();
        return wordPath;
    }

    [Benchmark(OperationsPerInvoke = FileNamesPerInvoke)]
    public int SanitizeFileName_Bank()
    {
        var digest = 0;
        for (var i = 0; i < FileNamesPerInvoke; i++)
        {
            var input = _fileNameBank[i % _fileNameBank.Length];
            var sanitized = ReportGenerationService.SanitizeFileName(input);
            digest ^= sanitized.Length ^ (sanitized.Length > 0 ? sanitized[0] : 0) ^ (sanitized.Length > 0 ? sanitized[^1] : 0);
        }
        return digest;
    }

    [Benchmark(OperationsPerInvoke = WorksheetNamesPerInvoke)]
    public int CreateValidWorksheetName_Bank()
    {
        var digest = 0;
        for (var i = 0; i < WorksheetNamesPerInvoke; i++)
        {
            var (baseName, suffix) = _worksheetNameBank[i % _worksheetNameBank.Length];
            var name = ReportGenerationService.CreateValidWorksheetName(baseName, suffix);
            digest ^= name.Length ^ (name.Length > 0 ? name[0] : 0) ^ (name.Length > 0 ? name[^1] : 0);
        }
        return digest;
    }
}
