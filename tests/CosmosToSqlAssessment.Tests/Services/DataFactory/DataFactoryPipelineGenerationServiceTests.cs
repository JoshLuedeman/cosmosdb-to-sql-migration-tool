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
            // The ARM-shape parameter template is intentionally NOT in the ADF artifact envelope.
            if (Path.GetFileName(path).Equals("adf-parameters.template.json", StringComparison.OrdinalIgnoreCase))
            {
                doc.RootElement.TryGetProperty("$schema", out _).Should().BeTrue();
                doc.RootElement.TryGetProperty("parameters", out _).Should().BeTrue();
                continue;
            }
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

    [Fact]
    public async Task GenerateAsync_PerDatabasePipeline_DeclaresParametersWithDefaults()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var parameters = doc.RootElement.GetProperty("properties").GetProperty("parameters");
        parameters.GetProperty(ParameterCatalog.PipelineParamCosmosDatabaseName)
            .GetProperty("defaultValue").GetString().Should().Be("MyDb");
        parameters.GetProperty(ParameterCatalog.PipelineParamSqlDatabaseName)
            .GetProperty("defaultValue").GetString().Should().Be("MyDb_SQL");
    }

    [Fact]
    public async Task GenerateAsync_MasterPipeline_ForwardsPerDatabaseParameters_WithCanonicalExpressionShape()
    {
        var assessment = Assessment(("dbA", new[] { "x" }), ("dbB", new[] { "y" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        var master = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")));

        var masterParams = master.RootElement.GetProperty("properties").GetProperty("parameters");
        masterParams.TryGetProperty(
            $"{ParameterCatalog.PipelineParamCosmosDatabaseName}_dbA", out _).Should().BeTrue();
        masterParams.TryGetProperty(
            $"{ParameterCatalog.PipelineParamCosmosDatabaseName}_dbB", out _).Should().BeTrue();
        masterParams.TryGetProperty(
            $"{ParameterCatalog.PipelineParamSqlDatabaseName}_dbA", out _).Should().BeTrue();

        var activities = master.RootElement.GetProperty("properties").GetProperty("activities");
        foreach (var activity in activities.EnumerateArray())
        {
            var executeParams = activity.GetProperty("typeProperties").GetProperty("parameters");
            var cosmos = executeParams.GetProperty(ParameterCatalog.PipelineParamCosmosDatabaseName);
            cosmos.GetProperty("type").GetString().Should().Be("Expression");
            cosmos.GetProperty("value").GetString()
                .Should().StartWith($"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}_");
        }
    }

    [Fact]
    public async Task GenerateAsync_EmitsAdfParametersTemplate_WithSecretNamesNotSecretValues()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForCosmos = false,
            UseManagedIdentityForSql = false,
            UseAzureKeyVault = true,
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var templatePath = Path.Combine(_outputDir, "ADF", "adf-parameters.template.json");
        File.Exists(templatePath).Should().BeTrue();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(templatePath));
        doc.RootElement.GetProperty("$schema").GetString()
            .Should().Contain("deploymentParameters.json");
        var parameters = doc.RootElement.GetProperty("parameters");
        parameters.TryGetProperty($"{ParameterCatalog.CosmosAccountKeySecretName}_MyDb", out _).Should().BeTrue();
        parameters.TryGetProperty(ParameterCatalog.SqlPasswordSecretName, out _).Should().BeTrue();
        // Sanity — none of the placeholders should ever contain a real secret-looking string.
        var raw = await File.ReadAllTextAsync(templatePath);
        raw.Should().NotMatchRegex(@"[A-Za-z0-9+/]{40,}={0,2}", "the parameter template must only contain secret NAMES, never secret values");
    }

    [Fact]
    public async Task GenerateAsync_KeyVaultOptIn_EmitsKeyVaultLinkedServiceFile()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            UseManagedIdentityForCosmos = false,
            UseAzureKeyVault = true,
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        File.Exists(Path.Combine(_outputDir, "ADF", "LinkedServices", "KeyVaultLinkedService.json"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_DefaultMode_DoesNotEmitKeyVaultLinkedService()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        File.Exists(Path.Combine(_outputDir, "ADF", "LinkedServices", "KeyVaultLinkedService.json"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_EveryCopyActivity_HasPolicyBlock_WithInsertSafeRetry()
    {
        var assessment = Assessment(("MyDb", new[] { "users", "orders" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        foreach (var activity in doc.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray())
        {
            activity.GetProperty("type").GetString().Should().Be("Copy");
            var policy = activity.GetProperty("policy");
            policy.GetProperty("retry").GetInt32().Should().Be(0);
            policy.GetProperty("timeout").GetString().Should().Be("12:00:00");
        }
    }

    [Fact]
    public async Task GenerateAsync_EveryExecutePipelineActivity_HasPolicyBlock()
    {
        var assessment = Assessment(("dbA", new[] { "x" }), ("dbB", new[] { "y" }));
        await CreateService().GenerateAsync(assessment, _outputDir);

        var master = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")));
        foreach (var activity in master.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray())
        {
            activity.GetProperty("type").GetString().Should().Be("ExecutePipeline");
            var policy = activity.GetProperty("policy");
            policy.GetProperty("timeout").GetString().Should().Be("1.00:00:00");
            policy.GetProperty("retry").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task GenerateAsync_FailureNotificationEnabled_MasterPipeline_PairsEachExecutePipeline_WithWebAndFail()
    {
        var assessment = Assessment(("dbA", new[] { "x" }), ("dbB", new[] { "y" }));
        var options = new DataFactoryGenerationOptions { EmitFailureNotification = true };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var master = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")));
        var activities = master.RootElement.GetProperty("properties").GetProperty("activities");
        // 2 ExecutePipeline + 2 Web + 2 Fail = 6
        activities.GetArrayLength().Should().Be(6);

        var types = activities.EnumerateArray().Select(a => a.GetProperty("type").GetString()!).ToList();
        types.Count(t => t == "ExecutePipeline").Should().Be(2);
        types.Count(t => t == "WebActivity").Should().Be(2);
        types.Count(t => t == "Fail").Should().Be(2);

        // Master pipeline params include the webhook URL.
        master.RootElement.GetProperty("properties").GetProperty("parameters")
            .TryGetProperty(ParameterCatalog.PipelineParamFailureNotificationWebhookUrl, out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_FailureNotificationEnabled_ExecutePipelineForwardsWebhookUrl()
    {
        var assessment = Assessment(("dbA", new[] { "x" }));
        var options = new DataFactoryGenerationOptions { EmitFailureNotification = true };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var master = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, "ADF", "Pipelines", "MasterMigrationPipeline.json")));
        var execute = master.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray()
            .First(a => a.GetProperty("type").GetString() == "ExecutePipeline");
        var executeParams = execute.GetProperty("typeProperties").GetProperty("parameters");
        executeParams.GetProperty(ParameterCatalog.PipelineParamFailureNotificationWebhookUrl)
            .GetProperty("type").GetString().Should().Be("Expression");
    }

    [Fact]
    public async Task GenerateAsync_PerCopyFailureNotification_AppendsWebAndFailToEveryCopy_AndAccountsForActivityCap()
    {
        // 3 mappings, with per-copy notification each mapping yields 3 activities → 9 total.
        // Cap at 4 → expect 3 pipeline files (3, 3, 3 — chunked at copy-group boundary).
        var assessment = Assessment(("MyDb", new[] { "a", "b", "c" }));
        var options = new DataFactoryGenerationOptions
        {
            EmitFailureNotification = true,
            PerCopyFailureNotification = true,
            MaxActivitiesPerPipeline = 4,
        };

        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelineFiles = Directory.GetFiles(Path.Combine(_outputDir, "ADF", "Pipelines"), "Migrate_MyDb_part*.json");
        pipelineFiles.Length.Should().Be(3);
        // Every chunk should have exactly 3 activities (a copy + its Web + its Fail).
        foreach (var file in pipelineFiles)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            doc.RootElement.GetProperty("properties").GetProperty("activities").GetArrayLength()
                .Should().Be(3);
        }
        // No "split" warning should be alarming — but the warning text references total activities.
        result.Warnings.Should().Contain(w => w.Contains("split into"));
    }

    [Fact]
    public async Task GenerateAsync_FaultToleranceEnabled_AddsParametersTemplateEntry_AndWarnsWhenNoStorageLs()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            FaultTolerance = new FaultToleranceOptions { Enabled = true },
        };

        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

        var templatePath = Path.Combine(_outputDir, "ADF", "adf-parameters.template.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(templatePath));
        doc.RootElement.GetProperty("parameters")
            .TryGetProperty(ParameterCatalog.PipelineParamFaultToleranceLogPath, out _)
            .Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("LogStorageLinkedServiceName"));
    }
}
