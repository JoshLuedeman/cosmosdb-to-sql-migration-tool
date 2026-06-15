using BenchmarkDotNet.Attributes;
using CosmosToSqlAssessment.Benchmarks.Fixtures;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Benchmarks.Benchmarks;

/// <summary>
/// Microbenchmarks for the SQL assessment hot paths exercised by parent #79 / #175.
///
/// Three benchmarks:
/// * <see cref="AssessMigrationAsync_EndToEnd"/> — runs the full public
///   <c>SqlMigrationAssessmentService.AssessMigrationAsync</c> pipeline (platform recommendation,
///   container mappings, schema deduplication, index / FK / unique constraint generation,
///   complexity assessment, transformation rules) over Small / Medium / Large
///   <see cref="CosmosDbAnalysis"/> fixtures. This is the public-API cost users care about; the
///   measurement includes <c>NullLogger</c> extension-method boxing, which is intentional.
/// * <see cref="GenerateIndexScript_Bank"/> — loops over a fixed bank of
///   <see cref="IndexRecommendation"/>s exercising every branch in
///   <c>SqlProjectGenerationService.GenerateIndexScript</c> (each index type, with and without
///   <c>IncludedColumns</c>).
/// * <see cref="SanitizeName_Bank"/> — loops over a fixed bank of db/container names
///   exercising every branch in <c>SqlProjectGenerationService.SanitizeName</c>.
///
/// All three return a digest so BenchmarkDotNet cannot dead-code-eliminate the work.
/// </summary>
[MemoryDiagnoser]
public class SqlAssessmentBenchmarks
{
    private const int IndexScriptsPerInvoke = 100;
    private const int SanitizeNamesPerInvoke = 100;

    private SqlMigrationAssessmentService _service = null!;
    private CosmosDbAnalysis _smallAnalysis = null!;
    private CosmosDbAnalysis _mediumAnalysis = null!;
    private CosmosDbAnalysis _largeAnalysis = null!;
    private IndexRecommendation[] _indexBank = null!;
    private string[] _nameBank = null!;

    [Params(SqlAssessmentFixtures.AnalysisSize.Small, SqlAssessmentFixtures.AnalysisSize.Medium, SqlAssessmentFixtures.AnalysisSize.Large)]
    public SqlAssessmentFixtures.AnalysisSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        _service = new SqlMigrationAssessmentService(configuration, NullLogger<SqlMigrationAssessmentService>.Instance);

        _smallAnalysis = SqlAssessmentFixtures.BuildCosmosAnalysis(SqlAssessmentFixtures.AnalysisSize.Small);
        _mediumAnalysis = SqlAssessmentFixtures.BuildCosmosAnalysis(SqlAssessmentFixtures.AnalysisSize.Medium);
        _largeAnalysis = SqlAssessmentFixtures.BuildCosmosAnalysis(SqlAssessmentFixtures.AnalysisSize.Large);

        _indexBank = SqlAssessmentFixtures.BuildIndexRecommendations();
        _nameBank = SqlAssessmentFixtures.BuildSanitizeNameInputs();
    }

    [Benchmark]
    public SqlMigrationAssessment AssessMigrationAsync_EndToEnd()
    {
        var fixture = Size switch
        {
            SqlAssessmentFixtures.AnalysisSize.Small => _smallAnalysis,
            SqlAssessmentFixtures.AnalysisSize.Medium => _mediumAnalysis,
            _ => _largeAnalysis
        };

        // AssessMigrationAsync is synchronous-internally (returns Task.FromResult). Using
        // GetAwaiter().GetResult() measures exactly the work the service does without adding
        // async-state-machine noise from an `async Task<T>` benchmark wrapper.
        return _service.AssessMigrationAsync(fixture, "BenchmarkDb").GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = IndexScriptsPerInvoke)]
    public int GenerateIndexScript_Bank()
    {
        var digest = 0;
        for (var i = 0; i < IndexScriptsPerInvoke; i++)
        {
            var rec = _indexBank[i % _indexBank.Length];
            var script = SqlProjectGenerationService.GenerateIndexScript(rec);
            digest ^= script.Length;
        }
        return digest;
    }

    [Benchmark(OperationsPerInvoke = SanitizeNamesPerInvoke)]
    public int SanitizeName_Bank()
    {
        var digest = 0;
        for (var i = 0; i < SanitizeNamesPerInvoke; i++)
        {
            var name = _nameBank[i % _nameBank.Length];
            var sanitized = SqlProjectGenerationService.SanitizeName(name);
            digest ^= sanitized.Length;
        }
        return digest;
    }
}
