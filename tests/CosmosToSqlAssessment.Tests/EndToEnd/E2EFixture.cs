using Azure.Monitor.Query;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.SqlProject;
using CosmosToSqlAssessment.Tests.Mocks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.EndToEnd;

/// <summary>
/// Self-contained fluent fixture that wires every real production service in
/// the assessment → SQL project generation pipeline against the mock Azure
/// SDKs from <c>tests/CosmosToSqlAssessment.Tests/Mocks/</c>.
///
/// <para>
/// The fixture is the canonical smoke-test entry point for sub-issue #181 and
/// for every Wave-2+ parent that needs to verify "the whole pipeline still
/// works end-to-end" before declaring a refactor safe. See
/// <c>EndToEnd/README.md</c> for usage guidance and assertion patterns.
/// </para>
///
/// <para>
/// Each fixture instance owns a temp directory (see <see cref="TempRoot"/>)
/// that is created on construction and removed on <see cref="Dispose"/> with a
/// small retry loop to tolerate Windows file-lock races. Use one fixture per
/// test (<c>using var fixture = new E2EFixture()...</c>).
/// </para>
/// </summary>
public sealed class E2EFixture : IDisposable
{
    private const string DefaultEndpoint = "https://test.documents.azure.com:443/";

    private readonly CosmosClientMockBuilder _cosmosBuilder = new();
    private readonly Dictionary<string, string?> _config = new(StringComparer.Ordinal)
    {
        ["CosmosDb:AccountEndpoint"] = DefaultEndpoint,
    };
    private LogsQueryClient? _logsQueryClient;
    private bool _built;
    private bool _disposed;

    private CosmosClient? _cosmosClient;

    /// <summary>
    /// Temporary root directory owned by this fixture. SQL project generation
    /// targets this folder so tests never leak files into the repo.
    /// </summary>
    public string TempRoot { get; } = Directory.CreateTempSubdirectory("e2e-").FullName;

    /// <summary>Underlying configuration; populated by <see cref="Build"/>.</summary>
    public IConfiguration Configuration { get; private set; } = null!;

    // ----- Real services exposed for granular Wave-2 reuse (R11) -----
    public CosmosDbAnalysisService CosmosService { get; private set; } = null!;
    public SqlMigrationAssessmentService SqlAssessmentService { get; private set; } = null!;
    public DataQualityAnalysisService DataQualityService { get; private set; } = null!;
    public DataFactoryEstimateService DataFactoryService { get; private set; } = null!;
    public SqlProjectGenerationService SqlProjectService { get; private set; } = null!;
    public SqlDatabaseProjectService SqlDatabaseProjectService { get; private set; } = null!;
    public SqlProjectIntegrationService SqlProjectIntegrationService { get; private set; } = null!;

    /// <summary>Adds a database with optional container configuration.</summary>
    public E2EFixture WithDatabase(string databaseId, Action<DatabaseMockBuilder>? configure = null)
    {
        EnsureNotBuilt();
        _cosmosBuilder.WithDatabase(databaseId, configure);
        return this;
    }

    /// <summary>
    /// Sets a configuration key. Use this to override Cosmos endpoint, DataFactory
    /// defaults, or any other key the real services read from <see cref="IConfiguration"/>.
    /// </summary>
    public E2EFixture WithConfig(string key, string? value)
    {
        EnsureNotBuilt();
        _config[key] = value;
        return this;
    }

    /// <summary>
    /// Configures Azure Monitor metrics for the pipeline. Sets both
    /// <c>AzureMonitor:WorkspaceId</c> and <c>AzureMonitor:CosmosAccountName</c>
    /// (production short-circuits to default zero metrics if either is missing -
    /// R2 from rubber-duck).
    /// </summary>
    public E2EFixture WithAzureMonitorMetrics(
        IEnumerable<(DateTimeOffset Timestamp, double Avg, double Max, double Total)> rows,
        string workspaceId = "test-workspace",
        string cosmosAccountName = "test-account")
    {
        EnsureNotBuilt();
        _config["AzureMonitor:WorkspaceId"] = workspaceId;
        _config["AzureMonitor:CosmosAccountName"] = cosmosAccountName;
        _logsQueryClient = new LogsQueryClientMockBuilder().WithMetricsRows(rows).Build();
        return this;
    }

    /// <summary>
    /// Wires the configuration, mocked clients, and all real services. Must be
    /// called before <see cref="RunAssessmentAsync"/> or any project generator.
    /// Idempotent: subsequent calls are no-ops.
    /// </summary>
    public E2EFixture Build()
    {
        if (_built) return this;

        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(_config!)
            .Build();

        _cosmosClient = _cosmosBuilder.Build();

        CosmosService = new CosmosDbAnalysisService(
            Configuration,
            NullLogger<CosmosDbAnalysisService>.Instance,
            _cosmosClient,
            _logsQueryClient);

        SqlAssessmentService = new SqlMigrationAssessmentService(
            Configuration,
            NullLogger<SqlMigrationAssessmentService>.Instance);

        DataQualityService = new DataQualityAnalysisService(
            Configuration,
            NullLogger<DataQualityAnalysisService>.Instance,
            _cosmosClient);

        DataFactoryService = new DataFactoryEstimateService(
            Configuration,
            NullLogger<DataFactoryEstimateService>.Instance);

        SqlProjectService = new SqlProjectGenerationService(
            Configuration,
            NullLogger<SqlProjectGenerationService>.Instance);

        SqlDatabaseProjectService = new SqlDatabaseProjectService(
            Configuration,
            NullLogger<SqlDatabaseProjectService>.Instance);

        SqlProjectIntegrationService = new SqlProjectIntegrationService(
            SqlDatabaseProjectService,
            Configuration,
            NullLogger<SqlProjectIntegrationService>.Instance);

        _built = true;
        return this;
    }

    /// <summary>
    /// Runs the four-phase assessment pipeline (Cosmos analysis → SQL assessment
    /// → data quality → Data Factory estimate) and returns the populated
    /// <see cref="AssessmentResult"/>.
    ///
    /// <para>
    /// Unlike <c>Program.RunAssessmentAsync</c>, this method does **not** swallow
    /// data-quality exceptions (R6) - tests want clear failures when mocks are
    /// wired wrong.
    /// </para>
    /// </summary>
    public async Task<AssessmentResult> RunAssessmentAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        EnsureBuilt();

        var result = new AssessmentResult
        {
            CosmosAccountName = "test-account",
            DatabaseName = databaseName
        };

        result.CosmosAnalysis = await CosmosService.AnalyzeDatabaseAsync(databaseName, cancellationToken);
        result.SqlAssessment = await SqlAssessmentService.AssessMigrationAsync(result.CosmosAnalysis, databaseName, cancellationToken);
        result.DataQualityAnalysis = await DataQualityService.AnalyzeDataQualityAsync(result.CosmosAnalysis, databaseName, cancellationToken);
        result.DataFactoryEstimate = await DataFactoryService.EstimateMigrationAsync(result.CosmosAnalysis, result.SqlAssessment, cancellationToken);

        return result;
    }

    /// <summary>
    /// Runs the production path used by <c>Program.GenerateOutputsAsync</c>:
    /// <see cref="SqlProjectGenerationService.GenerateSqlProjectsAsync"/> over the
    /// fixture's temp directory. Returns the resolved <c>sql-projects/</c> folder.
    /// </summary>
    public async Task<string> GenerateSqlProjectsViaGenerationServiceAsync(
        AssessmentResult assessment,
        CancellationToken cancellationToken = default)
    {
        EnsureBuilt();
        var baseDir = Path.Combine(TempRoot, "generation-service");
        Directory.CreateDirectory(baseDir);
        await SqlProjectService.GenerateSqlProjectsAsync(assessment, baseDir, cancellationToken);
        return Path.Combine(baseDir, "sql-projects");
    }

    /// <summary>
    /// Runs the production path used by <c>Program.GenerateSqlProjectAsync</c>:
    /// <see cref="SqlProjectIntegrationService.GenerateSqlProjectAsync"/> with a
    /// per-fixture <see cref="SqlProjectOptions"/> rooted in the temp directory.
    /// </summary>
    public async Task<SqlProjectGenerationResult> GenerateSqlProjectsViaIntegrationAsync(
        AssessmentResult assessment,
        SqlProjectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBuilt();
        options ??= SqlProjectOptions.CreateDefault();
        options.OutputPath = Path.Combine(TempRoot, "integration-service");
        if (string.IsNullOrEmpty(options.ProjectName))
        {
            options.ProjectName = $"{assessment.DatabaseName}_Migration";
        }
        return await SqlProjectIntegrationService.GenerateSqlProjectAsync(assessment.SqlAssessment, options, cancellationToken);
    }

    private void EnsureNotBuilt()
    {
        if (_built) throw new InvalidOperationException("Fixture already built; configure before calling Build().");
    }

    private void EnsureBuilt()
    {
        if (!_built) throw new InvalidOperationException("Call Build() before invoking pipeline methods.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Retry directory deletion to tolerate transient Windows file locks (R7).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
