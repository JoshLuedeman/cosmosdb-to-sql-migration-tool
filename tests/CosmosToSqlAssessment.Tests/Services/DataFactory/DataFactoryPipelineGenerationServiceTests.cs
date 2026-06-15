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
            // The diagnostic-settings ARM template (#144) also uses the ARM envelope, not the
            // ADF artifact envelope — has $schema/contentVersion/parameters/resources.
            if (Path.GetFileName(path).Equals("diagnostic-settings.template.json", StringComparison.OrdinalIgnoreCase))
            {
                doc.RootElement.TryGetProperty("$schema", out _).Should().BeTrue();
                doc.RootElement.TryGetProperty("resources", out _).Should().BeTrue();
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
        // Disable #145 validation so each mapping contributes a single Copy activity.
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { Enabled = false },
        };
        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

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
        var options = new DataFactoryGenerationOptions
        {
            MaxActivitiesPerPipeline = 2,
            // #145 validation expands each mapping to 5 activities; disable so the
            // 1-activity-per-mapping chunking math holds.
            Validation = new ValidationOptions { Enabled = false },
        };

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
        // Disable #145 validation so the pipeline contains only Copy activities (no Lookups).
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { Enabled = false },
        };
        await CreateService().GenerateAsync(assessment, _outputDir, options);

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
        // #145 validation OFF so the mapping group is just [Copy, Web, Fail] (size 3).
        var assessment = Assessment(("MyDb", new[] { "a", "b", "c" }));
        var options = new DataFactoryGenerationOptions
        {
            EmitFailureNotification = true,
            PerCopyFailureNotification = true,
            MaxActivitiesPerPipeline = 4,
            Validation = new ValidationOptions { Enabled = false },
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

    // ---- #144 monitoring ----

    [Fact]
    public async Task GenerateAsync_DefaultMonitoring_EmitsDiagnosticSettingsTemplate()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var path = Path.Combine(_outputDir, "ADF", "Monitoring", "diagnostic-settings.template.json");
        File.Exists(path).Should().BeTrue();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        doc.RootElement.GetProperty("$schema").GetString().Should().Contain("deploymentTemplate.json");
        var resource = doc.RootElement.GetProperty("resources")[0];
        resource.GetProperty("type").GetString().Should().Be(DiagnosticSettingsTemplateBuilder.ResourceType);
        resource.GetProperty("properties").GetProperty("logAnalyticsDestinationType").GetString().Should().Be("Dedicated");
    }

    [Fact]
    public async Task GenerateAsync_DefaultMonitoring_EmitsKqlCheatsheet()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var path = Path.Combine(_outputDir, "ADF", "Monitoring", "monitoring-queries.kql");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Contain("ADFActivityRun");
    }

    [Fact]
    public async Task GenerateAsync_MonitoringDisabled_DoesNotEmitMonitoringFolderFiles()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            Monitoring = new MonitoringOptions
            {
                EmitDiagnosticSettingsTemplate = false,
                EmitMonitoringQueriesCheatsheet = false,
            },
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        Directory.Exists(Path.Combine(_outputDir, "ADF", "Monitoring")).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_MonitoringEnabled_AddsMonitoringParameterPlaceholders()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var template = Path.Combine(_outputDir, "ADF", "adf-parameters.template.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(template));
        var parameters = doc.RootElement.GetProperty("parameters");
        parameters.TryGetProperty(ParameterCatalog.MonitoringParamDataFactoryName, out _).Should().BeTrue();
        parameters.TryGetProperty(ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId, out _).Should().BeTrue();
        parameters.TryGetProperty(ParameterCatalog.MonitoringParamDiagnosticSettingName, out _).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_MonitoringDisabled_OmitsMonitoringParameterPlaceholders()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            Monitoring = new MonitoringOptions
            {
                EmitDiagnosticSettingsTemplate = false,
                EmitMonitoringQueriesCheatsheet = false,
            },
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var template = Path.Combine(_outputDir, "ADF", "adf-parameters.template.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(template));
        var parameters = doc.RootElement.GetProperty("parameters");
        parameters.TryGetProperty(ParameterCatalog.MonitoringParamDataFactoryName, out _).Should().BeFalse();
        parameters.TryGetProperty(ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_DefaultMonitoring_EveryCopyActivityHasUserPropertiesAlongsidePolicy()
    {
        var assessment = Assessment(("MyDb", new[] { "users", "orders" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        // #145 validation is on by default → also emits Lookup / IfCondition activities;
        // restrict the assertion to Copy activities (still must be 2, one per mapping).
        var copyActivities = doc.RootElement.GetProperty("properties").GetProperty("activities")
            .EnumerateArray()
            .Where(a => a.GetProperty("type").GetString() == "Copy")
            .ToList();
        copyActivities.Should().HaveCount(2);
        foreach (var activity in copyActivities)
        {
            // Both policy (#143) and userProperties (#144) must be present — merge, not replace.
            activity.TryGetProperty("policy", out _).Should().BeTrue();
            var userProps = activity.GetProperty("userProperties");
            userProps.GetArrayLength().Should().Be(6);
            var names = userProps.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
            names.Should().Contain(new[]
            {
                UserPropertiesBuilder.PropSourceDatabase,
                UserPropertiesBuilder.PropTargetDatabase,
                UserPropertiesBuilder.PropSourceContainer,
                UserPropertiesBuilder.PropTargetSchema,
                UserPropertiesBuilder.PropTargetTable,
                UserPropertiesBuilder.PropMigrationTool,
            });
        }
    }

    [Fact]
    public async Task GenerateAsync_DiagnosticSettingsTemplate_IsParseableArmTemplate()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var path = Path.Combine(_outputDir, "ADF", "Monitoring", "diagnostic-settings.template.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        doc.RootElement.GetProperty("contentVersion").GetString().Should().Be("1.0.0.0");
        var parameters = doc.RootElement.GetProperty("parameters");
        parameters.GetProperty(ParameterCatalog.MonitoringParamDataFactoryName).GetProperty("type").GetString().Should().Be("string");
        parameters.GetProperty(ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId).GetProperty("type").GetString().Should().Be("string");
        parameters.GetProperty(ParameterCatalog.MonitoringParamDiagnosticSettingName).GetProperty("defaultValue").GetString().Should().Be("migration-diagnostics");

        var resource = doc.RootElement.GetProperty("resources")[0];
        resource.GetProperty("dependsOn").GetArrayLength().Should().Be(1);
        resource.GetProperty("dependsOn")[0].GetString()
            .Should().Be($"[resourceId('Microsoft.DataFactory/factories', parameters('{ParameterCatalog.MonitoringParamDataFactoryName}'))]");
    }

    // ---- #145 validation ----

    [Fact]
    public async Task GenerateAsync_DefaultValidation_AddsLookupSrcCopyLookupTgtIfFailGroupPerMapping()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));

        await CreateService().GenerateAsync(assessment, _outputDir);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var activities = doc.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray().ToList();
        // 1 LookupSrc + 1 Copy + 1 LookupTgt + 1 IfCondition (nested Fail not in top-level activities) = 4
        activities.Should().HaveCount(4);

        var types = activities.Select(a => a.GetProperty("type").GetString()).ToList();
        types.Count(t => t == "Lookup").Should().Be(2);
        types.Count(t => t == "Copy").Should().Be(1);
        types.Count(t => t == "IfCondition").Should().Be(1);

        var ifCondition = activities.Single(a => a.GetProperty("type").GetString() == "IfCondition");
        var nestedFalse = ifCondition.GetProperty("typeProperties").GetProperty("ifFalseActivities");
        nestedFalse.GetArrayLength().Should().Be(1);
        nestedFalse[0].GetProperty("type").GetString().Should().Be("Fail");

        // Copy must depend on LookupSrc (Succeeded) so it doesn't race ahead of the baseline count.
        var copy = activities.Single(a => a.GetProperty("type").GetString() == "Copy");
        copy.TryGetProperty("dependsOn", out var dependsOn).Should().BeTrue();
        dependsOn[0].GetProperty("activity").GetString().Should().StartWith("LookupSrc_");

        // Copy MUST still carry #143 policy + #144 userProperties — dependsOn merged, not replaced.
        copy.TryGetProperty("policy", out _).Should().BeTrue();
        copy.TryGetProperty("userProperties", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_ValidationDisabled_OnlyEmitsCopyActivities()
    {
        var assessment = Assessment(("MyDb", new[] { "users", "orders" }));
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { Enabled = false },
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var activities = doc.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray().ToList();
        activities.Should().HaveCount(2);
        activities.Should().AllSatisfy(a => a.GetProperty("type").GetString().Should().Be("Copy"));
    }

    [Fact]
    public async Task GenerateAsync_ValidationSkipThreshold_HonoursPerMappingOptOut()
    {
        // Large container above threshold → skip; small one below → keep.
        var assessment = TestDataFactory.CreateSampleAssessmentResult();
        assessment.SqlAssessment.DatabaseMappings = new List<DatabaseMapping>
        {
            new()
            {
                SourceDatabase = "MyDb",
                TargetDatabase = "MyDb_SQL",
                ContainerMappings = new List<ContainerMapping>
                {
                    new()
                    {
                        SourceContainer = "huge",
                        TargetSchema = "dbo",
                        TargetTable = "huge",
                        EstimatedRowCount = 100_000_000,
                        FieldMappings = { new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" } },
                    },
                    new()
                    {
                        SourceContainer = "tiny",
                        TargetSchema = "dbo",
                        TargetTable = "tiny",
                        EstimatedRowCount = 100,
                        FieldMappings = { new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" } },
                    },
                },
            },
        };
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { SkipForContainerDocumentCountAbove = 1_000_000 },
        };

        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var activities = doc.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray().ToList();
        // huge: 1 Copy (validation skipped)
        // tiny: 4 activities (LookupSrc + Copy + LookupTgt + If)
        // Total = 5
        activities.Should().HaveCount(5);
        activities.Count(a => a.GetProperty("type").GetString() == "Copy").Should().Be(2);
        activities.Count(a => a.GetProperty("type").GetString() == "Lookup").Should().Be(2);
        activities.Count(a => a.GetProperty("type").GetString() == "IfCondition").Should().Be(1);

        result.Warnings.Should().Contain(w => w.Contains("Row-count validation skipped for container 'huge'"));
    }

    [Fact]
    public async Task GenerateAsync_PipelineAnnotations_IncludeValidationStrategy()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { Strategy = ValidationStrategy.RowCountAtLeast, Tolerance = 50 },
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var annotations = doc.RootElement.GetProperty("properties").GetProperty("annotations")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        annotations.Should().Contain("validation:RowCountAtLeast");
    }

    [Fact]
    public async Task GenerateAsync_PipelineAnnotations_ValidationOffWhenDisabled()
    {
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            Validation = new ValidationOptions { Enabled = false },
        };

        await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelinePath = Path.Combine(_outputDir, "ADF", "Pipelines", "Migrate_MyDb.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pipelinePath));
        var annotations = doc.RootElement.GetProperty("properties").GetProperty("annotations")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        annotations.Should().Contain("validation:off");
    }

    [Fact]
    public async Task GenerateAsync_OversizedMappingGroup_Throws()
    {
        // Validation expands to 5 activities (4 visible + 1 nested). Cap at 4 → throws.
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            MaxActivitiesPerPipeline = 4,
            Validation = new ValidationOptions { Enabled = true },
        };

        var act = async () => await CreateService().GenerateAsync(assessment, _outputDir, options);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds MaxActivitiesPerPipeline*");
    }

    [Fact]
    public async Task GenerateAsync_ValidationPlusPerCopyNotification_ProducesSevenActivityGroupAndChunksOnGroupBoundary()
    {
        // 3 mappings × (4 visible + 2 notification + 1 nested counted) = 21 activities total.
        // Cap at 7 → exactly 3 chunks of 1 group each.
        var assessment = Assessment(("MyDb", new[] { "a", "b", "c" }));
        var options = new DataFactoryGenerationOptions
        {
            EmitFailureNotification = true,
            PerCopyFailureNotification = true,
            MaxActivitiesPerPipeline = 7,
        };

        var result = await CreateService().GenerateAsync(assessment, _outputDir, options);

        var pipelineFiles = Directory.GetFiles(Path.Combine(_outputDir, "ADF", "Pipelines"), "Migrate_MyDb_part*.json");
        pipelineFiles.Length.Should().Be(3);

        foreach (var file in pipelineFiles)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var activities = doc.RootElement.GetProperty("properties").GetProperty("activities").EnumerateArray().ToList();
            // Each chunk has exactly one mapping group: 2 Lookup + 1 Copy + 1 Web + 1 Fail + 1 IfCondition = 6 visible (+ nested Fail under If counts to 7 toward cap).
            activities.Should().HaveCount(6);
            activities.Count(a => a.GetProperty("type").GetString() == "Lookup").Should().Be(2);
            activities.Count(a => a.GetProperty("type").GetString() == "Copy").Should().Be(1);
            activities.Count(a => a.GetProperty("type").GetString() == "WebActivity").Should().Be(1);
            activities.Count(a => a.GetProperty("type").GetString() == "Fail").Should().Be(1);
            activities.Count(a => a.GetProperty("type").GetString() == "IfCondition").Should().Be(1);
        }
    }

    [Fact]
    public async Task GenerateAsync_ValidationOnlyMappingGroup_HasFiveCountedActivities_FitsInMinCap()
    {
        // Validation only (no per-copy notification): group is 4 visible + 1 nested Fail = 5 counted.
        // Confirm a single mapping fits in cap 5.
        var assessment = Assessment(("MyDb", new[] { "users" }));
        var options = new DataFactoryGenerationOptions
        {
            MaxActivitiesPerPipeline = 5,
            Validation = new ValidationOptions { Enabled = true },
        };

        var act = async () => await CreateService().GenerateAsync(assessment, _outputDir, options);
        await act.Should().NotThrowAsync();
    }
}
