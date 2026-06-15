using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Benchmarks.Fixtures;

/// <summary>
/// Deterministic synthetic fixtures for the report-generation benchmarks. Reuses
/// <see cref="SqlAssessmentFixtures.BuildCosmosAnalysis"/> for the Cosmos-side shape and runs
/// <see cref="SqlMigrationAssessmentService.AssessMigrationAsync"/> once per size to produce a
/// realistic <see cref="SqlMigrationAssessment"/>. The resulting <see cref="AssessmentResult"/>
/// has a fixed <c>AssessmentId</c> so re-runs are stable.
/// </summary>
public static class ReportGenerationFixtures
{
    public static AssessmentResult BuildAssessmentResult(SqlAssessmentFixtures.AnalysisSize size)
    {
        var cosmosAnalysis = SqlAssessmentFixtures.BuildCosmosAnalysis(size);

        IConfiguration configuration = new ConfigurationBuilder().Build();
        var sqlService = new SqlMigrationAssessmentService(configuration, NullLogger<SqlMigrationAssessmentService>.Instance);
        var sqlAssessment = sqlService.AssessMigrationAsync(cosmosAnalysis, $"BenchmarkDb_{size}").GetAwaiter().GetResult();

        return new AssessmentResult
        {
            AssessmentId = $"benchmark-{size}-00000000-0000-0000-0000-000000000000",
            AssessmentDate = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            CosmosAccountName = "benchmark-cosmos-account",
            DatabaseName = $"BenchmarkDb_{size}",
            CosmosAnalysis = cosmosAnalysis,
            SqlAssessment = sqlAssessment,
            DataFactoryEstimate = BuildDataFactoryEstimate(size),
            Recommendations = BuildRecommendations()
        };
    }

    private static DataFactoryEstimate BuildDataFactoryEstimate(SqlAssessmentFixtures.AnalysisSize size)
    {
        // The Migration Estimates worksheet renders aggregate fields plus Prerequisites and
        // Recommendations — PipelineEstimates is not currently iterated by the service, so keep
        // it minimal and pad the rendered lists instead.
        var scale = size switch
        {
            SqlAssessmentFixtures.AnalysisSize.Small => 1,
            SqlAssessmentFixtures.AnalysisSize.Medium => 5,
            _ => 20
        };

        return new DataFactoryEstimate
        {
            EstimatedDuration = TimeSpan.FromHours(2 * scale),
            TotalDataSizeGB = 10 * scale,
            RecommendedDIUs = 4 + scale,
            RecommendedParallelCopies = Math.Min(8, 2 + scale),
            EstimatedCostUSD = 15.50m * scale,
            PipelineEstimates = new List<PipelineEstimate>
            {
                new()
                {
                    SourceContainer = "container_000",
                    TargetTable = "container_000",
                    DataSizeGB = 5,
                    EstimatedDuration = TimeSpan.FromHours(1),
                    RequiresTransformation = true,
                    TransformationComplexity = "Medium"
                }
            },
            Prerequisites = new List<string>
            {
                "Azure Data Factory instance provisioned in the target subscription",
                "Source Cosmos DB account configured with read permissions for the integration runtime",
                "Target Azure SQL Database created with sufficient compute tier",
                "Network connectivity validated between source, ADF, and target",
                "Service principal or managed identity configured for credential-free access",
                "Monitoring and alerting endpoints established"
            },
            Recommendations = new List<string>
            {
                "Use incremental copy patterns for large containers exceeding 50 GB",
                "Monitor RU consumption on source during migration windows",
                "Enable staging tables for transformation-heavy migrations",
                "Schedule migration runs during low-traffic windows",
                "Validate row counts and aggregate checksums post-migration",
                "Plan rollback procedures before cutover"
            }
        };
    }

    private static List<RecommendationItem> BuildRecommendations()
    {
        return new List<RecommendationItem>
        {
            new()
            {
                Category = "Performance",
                Title = "Tune indexing strategy",
                Description = "Review query patterns and add covering indexes for the top 10 most-frequent queries.",
                Priority = "High",
                Impact = "Reduces per-request RU consumption by 30-50%",
                ActionItems = new List<string> { "Capture query metrics", "Generate index DDL", "Test in staging" }
            },
            new()
            {
                Category = "Schema",
                Title = "Normalize nested collections",
                Description = "Identified nested arrays that should become separate tables for relational integrity.",
                Priority = "Medium",
                Impact = "Improves query performance and reduces storage overhead",
                ActionItems = new List<string> { "Identify nested arrays", "Design child-table DDL", "Plan migration cutover" }
            },
            new()
            {
                Category = "Security",
                Title = "Enable Azure SQL Auditing",
                Description = "Configure auditing to track database events and identify anomalies.",
                Priority = "High",
                Impact = "Strengthens compliance posture",
                ActionItems = new List<string> { "Enable auditing", "Configure log retention", "Set up alerts" }
            }
        };
    }

    /// <summary>
    /// Bank of realistic database names exercising every branch of
    /// <c>ReportGenerationService.SanitizeFileName</c>: whitespace, invalid filesystem chars,
    /// leading dot, mixed-case Unicode.
    /// </summary>
    public static string[] BuildFileNameInputs()
    {
        return new[]
        {
            "CustomersDB",
            "Customer Profiles",
            "orders/archive",
            "tenant:billing",
            ".hidden_db",
            "audit\\logs",
            "data?store",
            "results*final",
            "report<draft>",
            "MultiTenant DB (prod)"
        };
    }

    /// <summary>
    /// Bank of realistic worksheet names exercising every branch of
    /// <c>ReportGenerationService.CreateValidWorksheetName</c>: short names, invalid Excel chars,
    /// long names &gt; 31 chars, the "Multiple Databases (N)" regex pattern, and name + suffix
    /// pairs that trigger truncation.
    /// </summary>
    public static (string baseName, string suffix)[] BuildWorksheetNameInputs()
    {
        return new (string, string)[]
        {
            ("Summary", ""),
            ("Container:001", ""),
            ("ImportPath/Settings", "Details"),
            ("ThisIsAReallyLongWorksheetNameThatExceedsThirtyOneChars", ""),
            ("ThisIsAReallyLongWorksheetNameThatExceedsThirtyOneChars", "Indexes"),
            ("Multiple Databases (11)", ""),
            ("Multiple Databases (3)", "Summary"),
            ("Multiple Databases (42)", "VeryLongSuffixThatNeedsTruncating"),
            ("ShortName", "MediumSuffix"),
            ("Audit[Logs]?", "X")
        };
    }
}
