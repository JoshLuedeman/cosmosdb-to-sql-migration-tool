using System.Text.Json;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services.DataFactory;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class DataFactoryPipelineGenerationServiceTests : TestBase, IDisposable
{
    private readonly string _outputDir;

    public DataFactoryPipelineGenerationServiceTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "adf-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            try { Directory.Delete(_outputDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private DataFactoryPipelineGenerationService CreateService() =>
        new(CreateMockLogger<DataFactoryPipelineGenerationService>().Object);

    private static AssessmentResult Assessment(params (string database, string[] containers)[] dbs)
    {
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings = dbs.Select(d => new DatabaseMapping
        {
            SourceDatabase = d.database,
            TargetDatabase = d.database + "_SQL",
            ContainerMappings = d.containers.Select(c => new ContainerMapping
            {
                SourceContainer = c,
                TargetSchema = "dbo",
                TargetTable = c,
                FieldMappings = new List<FieldMapping>
                {
                    new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" },
                },
            }).ToList(),
        }).ToList();
        return assessment;
    }

    [Fact]
    public async Task GenerateAsync_EmitsFullDirectoryLayout()
    {
        var assessment = Assessment(("MyDb", new[] { "users", "orders" }));
        var result = await CreateService().GenerateAsync(assessment, _outputDir);

        Directory.Exists(Path.Combine(_outputDir, "ADF", "LinkedServices")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputDir, "ADF", "Datasets")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputDir, "ADF", "Pipelines")).Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "ADF", "README.md")).Should().BeTrue();

        result.LinkedServiceCount.Should().Be(2); // 1 Cosmos + 1 SQL
        result.DatasetCount.Should().Be(4);       // 2 containers × (source+sink)
        result.CopyActivityCount.Should().Be(2);
        result.PipelineCount.Should().Be(2);      // 1 per-db + 1 master
    }

    [Fact]
    public async Task GenerateAsync_AllArtifacts_AreValidJson()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var result = await CreateService().GenerateAsync(assessment, _outputDir);

        foreach (var path in result.GeneratedFiles.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var contents = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(contents); // throws if not valid JSON
            doc.RootElement.TryGetProperty("name", out _).Should().BeTrue($"{Path.GetFileName(path)} must have 'name'");
            doc.RootElement.TryGetProperty("properties", out _).Should().BeTrue($"{Path.GetFileName(path)} must have 'properties'");
        }
    }

    [Fact]
    public async Task GenerateAsync_PerDatabasePipeline_HasCopyActivityForEveryMapping()
    {
        var assessment = Assessment(("MyDb", new[] { "users", "orders", "products" }));
        var result = await CreateService().GenerateAsync(assessment, _outputDir);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        File.Exists(pipelinePath).Should().BeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(pipelinePath));
        var activities = doc.RootElement.GetProperty("properties").GetProperty("activities");
        activities.GetArrayLength().Should().Be(3);
        foreach (var activity in activities.EnumerateArray())
        {
            activity.GetProperty("type").GetString().Should().Be("Copy");

            var src = activity.GetProperty("typeProperties").GetProperty("source");
            src.GetProperty("type").GetString().Should().Be("CosmosDbSqlApiSource");

            var sink = activity.GetProperty("typeProperties").GetProperty("sink");
            sink.GetProperty("type").GetString().Should().Be("AzureSqlSink");
        }

        result.Warnings.Should().NotContain(w => w.Contains("split into"));
    }

    [Fact]
    public async Task GenerateAsync_ChunksWhenActivityCountExceedsLimit()
    {
        // 5 mappings, cap at 2 → expect 3 pipeline files + master
        var manyContainers = Enumerable.Range(0, 5).Select(i => $"container{i}").ToArray();
        var assessment = Assessment(("MyDb", manyContainers));
        var options = new DataFactoryGenerationOptions { MaxActivitiesPerPipeline = 2 };

        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

        Directory.GetFiles(Path.Combine(_outputDir, "ADF", "Pipelines"), "Migrate_MyDb_part*.json")
            .Should().HaveCount(3);
        File.Exists(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")).Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("split into"));
    }

    [Fact]
    public async Task GenerateAsync_MultipleDatabases_NoDatasetNameCollision()
    {
        var assessment = Assessment(
            ("dbA", new[] { "users" }),
            ("dbB", new[] { "users" }));

        var result = await CreateService().GenerateAsync(assessment, _outputDir);

        var datasetFiles = Directory.GetFiles(Path.Combine(_outputDir, "ADF", "Datasets"));
        // 2 Cosmos source datasets (one per db) + 2 SQL sink datasets share targets w/ collision → still distinct names
        datasetFiles.Length.Should().BeGreaterThanOrEqualTo(3);
        datasetFiles.Any(f => f.Contains("Cosmos_dbA_users")).Should().BeTrue();
        datasetFiles.Any(f => f.Contains("Cosmos_dbB_users")).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_MasterPipeline_InvokesEveryPerDatabasePipeline()
    {
        var assessment = Assessment(("dbA", new[] { "x" }), ("dbB", new[] { "y" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        var master = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")));
        var activities = master.RootElement.GetProperty("properties").GetProperty("activities");
        activities.GetArrayLength().Should().Be(2);
        foreach (var activity in activities.EnumerateArray())
        {
            activity.GetProperty("type").GetString().Should().Be("ExecutePipeline");
            activity.GetProperty("typeProperties").GetProperty("pipeline").GetProperty("type").GetString()
                .Should().Be("PipelineReference");
        }
    }

    [Fact]
    public async Task GenerateAsync_NoMappings_StillEmitsAdfRootAndReadme_WithWarning()
    {
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings = new List<DatabaseMapping>();

        var result = await CreateService().GenerateAsync(assessment, _outputDir);

        Directory.Exists(Path.Combine(_outputDir, "ADF")).Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "ADF", "README.md")).Should().BeTrue();
        result.Warnings.Should().NotBeEmpty();
        result.PipelineCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateAsync_RespectsCancellation()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CreateService().GenerateAsync(assessment, _outputDir, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
