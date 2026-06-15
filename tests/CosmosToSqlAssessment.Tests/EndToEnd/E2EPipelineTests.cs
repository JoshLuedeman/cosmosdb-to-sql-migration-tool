using System.Xml.Linq;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Tests.EndToEnd;

/// <summary>
/// End-to-end smoke tests for the full assessment → SQL project generation
/// pipeline. These tests drive the real production services against the mocked
/// Azure SDKs from <c>../Mocks/</c> and assert on the on-disk artifacts the
/// two project-generation paths produce.
///
/// <para>
/// Wave-2+ parents are expected to run this suite (or extend it) before
/// declaring a refactor safe. See <c>EndToEnd/README.md</c> for the recommended
/// assertion patterns.
/// </para>
/// </summary>
public class E2EPipelineTests
{
    [Fact]
    public async Task FullPipeline_with_two_containers_generates_sql_project_via_integration()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(800)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");

        assessment.CosmosAnalysis.Containers.Should().HaveCount(2);
        assessment.SqlAssessment.DatabaseMappings.Should().NotBeEmpty();
        assessment.SqlAssessment.DatabaseMappings
            .SelectMany(m => m.ContainerMappings)
            .Select(cm => cm.SourceContainer)
            .Should().BeEquivalentTo(new[] { "users", "orders" });

        var result = await fixture.GenerateSqlProjectsViaIntegrationAsync(assessment);

        result.Success.Should().BeTrue();
        result.Project.Should().NotBeNull();
        File.Exists(result.Project!.ProjectFilePath).Should().BeTrue();

        // .sqlproj must parse as XML (R8 - structural, not text-comparison).
        var sqlproj = XDocument.Load(result.Project!.ProjectFilePath);
        sqlproj.Root.Should().NotBeNull();

        // Per-container table SQL files exist on disk and contain CREATE TABLE.
        var mappings = assessment.SqlAssessment.DatabaseMappings.SelectMany(m => m.ContainerMappings).ToList();
        var tablesRoot = Path.Combine(result.Project!.OutputPath, "Tables");
        Directory.Exists(tablesRoot).Should().BeTrue("a Tables/ folder should be generated under the SQL project");
        foreach (var cm in mappings)
        {
            var tableFile = Directory.EnumerateFiles(tablesRoot, $"*{cm.TargetTable}*.sql", SearchOption.AllDirectories)
                .FirstOrDefault();
            tableFile.Should().NotBeNull($"a table file for {cm.TargetTable} should be generated under Tables/");
            var contents = await File.ReadAllTextAsync(tableFile!);
            contents.Should().Contain("CREATE TABLE", $"table file {tableFile} should declare a table");
        }
    }

    [Fact]
    public async Task FullPipeline_with_two_containers_generates_sql_project_via_generation_service()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(800)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");
        var sqlProjectsDir = await fixture.GenerateSqlProjectsViaGenerationServiceAsync(assessment);

        Directory.Exists(sqlProjectsDir).Should().BeTrue();

        // One <db>.Database folder per database mapping with the canonical layout.
        var dbProjectDir = Directory.EnumerateDirectories(sqlProjectsDir).Single();
        Directory.Exists(Path.Combine(dbProjectDir, "Tables")).Should().BeTrue();
        Directory.Exists(Path.Combine(dbProjectDir, "Indexes")).Should().BeTrue();
        Directory.Exists(Path.Combine(dbProjectDir, "ForeignKeys")).Should().BeTrue();
        Directory.Exists(Path.Combine(dbProjectDir, "Scripts")).Should().BeTrue();

        var sqlprojFile = Directory.EnumerateFiles(dbProjectDir, "*.sqlproj").Single();
        var sqlproj = XDocument.Load(sqlprojFile);
        sqlproj.Root.Should().NotBeNull();

        // A table file exists per container mapping and references CREATE TABLE.
        var mappings = assessment.SqlAssessment.DatabaseMappings.SelectMany(m => m.ContainerMappings).ToList();
        var tablesDir = Path.Combine(dbProjectDir, "Tables");
        Directory.EnumerateFiles(tablesDir, "*.sql").Should().HaveCountGreaterThanOrEqualTo(mappings.Count);

        foreach (var tableFile in Directory.EnumerateFiles(tablesDir, "*.sql"))
        {
            (await File.ReadAllTextAsync(tableFile)).Should().Contain("CREATE TABLE");
        }
    }

    [Fact]
    public async Task FullPipeline_data_quality_runs_and_produces_score()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray())))
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");

        assessment.DataQualityAnalysis.Should().NotBeNull();
        assessment.DataQualityAnalysis!.ContainerAnalyses.Should().ContainSingle();
        assessment.DataQualityAnalysis.TotalDocumentsAnalyzed.Should().Be(2);
        assessment.DataQualityAnalysis.Summary.Should().NotBeNull();
        assessment.DataQualityAnalysis.Summary.OverallQualityScore.Should().BeGreaterThan(0);
        assessment.DataQualityAnalysis.Summary.QualityRating.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FullPipeline_uses_azure_monitor_when_configured()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .WithAzureMonitorMetrics(E2ESampleData.SixHoursOfMetrics)
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");

        assessment.CosmosAnalysis.PerformanceMetrics.Should().NotBeNull();
        assessment.CosmosAnalysis.PerformanceMetrics.AverageRUsPerSecond.Should().BeGreaterThan(0);
        assessment.CosmosAnalysis.PerformanceMetrics.PeakRUsPerSecond.Should().BeGreaterThan(0);
        assessment.CosmosAnalysis.PerformanceMetrics.TotalRUConsumption.Should().BeGreaterThan(0);
        assessment.CosmosAnalysis.MonitoringLimitations
            .Should().NotContain(l => l.Contains("Azure Monitor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullPipeline_without_azure_monitor_records_limitation()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");

        assessment.CosmosAnalysis.MonitoringLimitations
            .Should().Contain(l => l.Contains("Azure Monitor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullPipeline_single_empty_container_completes_without_error()
    {
        // Single empty container is the supported "no data" scenario.
        // Zero-container databases are explicitly out of scope (would crash
        // DataFactoryEstimateService - see README.md known limitations).
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("empty", c => c
                    .WithPartitionKey("/id").WithThroughput(400)))
            .Build();

        var assessment = await fixture.RunAssessmentAsync("AppDb");

        assessment.CosmosAnalysis.Containers.Should().ContainSingle();
        assessment.CosmosAnalysis.Containers[0].DocumentCount.Should().Be(0);

        // Both project-generation paths complete without throwing; we do not
        // assert that the generated table SQL is semantically meaningful (R4).
        var integrationResult = await fixture.GenerateSqlProjectsViaIntegrationAsync(assessment);
        integrationResult.Success.Should().BeTrue();
        File.Exists(integrationResult.Project!.ProjectFilePath).Should().BeTrue();

        var generationDir = await fixture.GenerateSqlProjectsViaGenerationServiceAsync(assessment);
        Directory.Exists(generationDir).Should().BeTrue();
    }
}
