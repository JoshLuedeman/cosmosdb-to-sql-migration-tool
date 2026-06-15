using System.Text;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Orchestrates Azure Data Factory artifact generation: composes the per-database
/// linked service, all source / sink datasets, one Copy activity per
/// <see cref="ContainerMapping"/>, and writes every artifact under
/// <c>&lt;outputDirectory&gt;/ADF/{LinkedServices,Datasets,Pipelines}/</c>.
///
/// Pipelines are split per source database, and any database with more than
/// <see cref="DataFactoryGenerationOptions.MaxActivitiesPerPipeline"/> mappings is
/// chunked into multiple pipeline files so ADF's per-pipeline activity limit is
/// respected. A master orchestrator pipeline references every per-database pipeline
/// via <c>ExecutePipeline</c> activities and forwards per-database parameters down.
/// </summary>
public sealed class DataFactoryPipelineGenerationService : IDataFactoryPipelineGenerator
{
    private const string AdfRootFolder = "ADF";
    private const string LinkedServicesFolder = "LinkedServices";
    private const string DatasetsFolder = "Datasets";
    private const string PipelinesFolder = "Pipelines";
    private const string MonitoringFolder = "Monitoring";
    private const string MasterPipelineName = "MasterMigrationPipeline";
    private const string ParametersTemplateFileName = "adf-parameters.template.json";
    private const string DiagnosticSettingsTemplateFileName = "diagnostic-settings.template.json";
    private const string MonitoringQueriesFileName = "monitoring-queries.kql";

    private readonly ILogger<DataFactoryPipelineGenerationService> _logger;
    private readonly LinkedServiceBuilder _linkedServiceBuilder;
    private readonly DatasetBuilder _datasetBuilder;
    private readonly CopyActivityBuilder _copyActivityBuilder;
    private readonly FailureNotificationBuilder _failureNotificationBuilder;
    private readonly DiagnosticSettingsTemplateBuilder _diagnosticSettingsBuilder;

    public DataFactoryPipelineGenerationService(
        ILogger<DataFactoryPipelineGenerationService> logger,
        LinkedServiceBuilder? linkedServiceBuilder = null,
        DatasetBuilder? datasetBuilder = null,
        CopyActivityBuilder? copyActivityBuilder = null,
        FailureNotificationBuilder? failureNotificationBuilder = null,
        DiagnosticSettingsTemplateBuilder? diagnosticSettingsBuilder = null)
    {
        _logger = logger;
        _linkedServiceBuilder = linkedServiceBuilder ?? new LinkedServiceBuilder();
        _datasetBuilder = datasetBuilder ?? new DatasetBuilder();
        _copyActivityBuilder = copyActivityBuilder ?? new CopyActivityBuilder();
        _failureNotificationBuilder = failureNotificationBuilder ?? new FailureNotificationBuilder();
        _diagnosticSettingsBuilder = diagnosticSettingsBuilder ?? new DiagnosticSettingsTemplateBuilder();
    }

    public async Task<DataFactoryGenerationResult> GenerateAsync(
        AssessmentResult assessment,
        string outputDirectory,
        DataFactoryGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        options ??= new DataFactoryGenerationOptions();
        var result = new DataFactoryGenerationResult();
        var registry = new AdfNameRegistry();
        var parameterTemplate = new SortedDictionary<string, ParameterTemplateEntry>(StringComparer.Ordinal);

        var databaseMappings = assessment.SqlAssessment?.DatabaseMappings ?? new List<DatabaseMapping>();
        if (databaseMappings.Count == 0)
        {
            _logger.LogWarning("No database mappings present in assessment — ADF generation produced no copy artifacts.");
            result.Warnings.Add("Assessment contained no DatabaseMappings — no ADF pipelines were generated.");
            // Still create the directory + README so the operator sees the section exists.
            var emptyAdfRoot = EnsureDirectory(outputDirectory, AdfRootFolder);
            await WriteParameterTemplateAsync(emptyAdfRoot, parameterTemplate, options, result, cancellationToken).ConfigureAwait(false);
            await WriteReadmeAsync(emptyAdfRoot, result, options, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var adfRoot = EnsureDirectory(outputDirectory, AdfRootFolder);
        var linkedServicesDir = EnsureDirectory(adfRoot, LinkedServicesFolder);
        var datasetsDir = EnsureDirectory(adfRoot, DatasetsFolder);
        var pipelinesDir = EnsureDirectory(adfRoot, PipelinesFolder);

        // 1) Optional Key Vault linked service (must precede Cosmos/SQL when used so refs resolve).
        if (options.UseAzureKeyVault &&
            (!options.UseManagedIdentityForCosmos || !options.UseManagedIdentityForSql))
        {
            var kvLs = _linkedServiceBuilder.BuildKeyVaultLinkedService(registry);
            await WriteArtifactAsync(linkedServicesDir, kvLs.Name, kvLs, result, cancellationToken).ConfigureAwait(false);
            result.LinkedServiceCount++;
            parameterTemplate[ParameterCatalog.KeyVaultBaseUrl] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.KeyVaultBaseUrl,
                $"<https://your-vault.vault.azure.net/>");
        }

        // 2) Azure SQL linked service (single, shared across all target tables).
        var azureSqlLs = _linkedServiceBuilder.BuildAzureSqlLinkedService(registry, options);
        await WriteArtifactAsync(linkedServicesDir, azureSqlLs.Name, azureSqlLs, result, cancellationToken).ConfigureAwait(false);
        result.LinkedServiceCount++;
        parameterTemplate[ParameterCatalog.SqlServerName] = ParameterTemplateEntry.Placeholder(
            ParameterCatalog.SqlServerName, "<sql-server-name-without-suffix>");
        if (options.UseAzureKeyVault && !options.UseManagedIdentityForSql)
        {
            parameterTemplate[ParameterCatalog.SqlUserName] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.SqlUserName, "<sql-user-name>");
            parameterTemplate[ParameterCatalog.SqlPasswordSecretName] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.SqlPasswordSecretName, "<key-vault-secret-name-for-sql-password>");
        }

        // 3) Per-database linked service + per-mapping datasets + per-database pipeline(s).
        var perDatabasePipelineExecutions = new List<MasterExecutionPlan>();

        foreach (var dbMapping in databaseMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var databaseName = string.IsNullOrWhiteSpace(dbMapping.SourceDatabase)
                ? assessment.DatabaseName
                : dbMapping.SourceDatabase;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                databaseName = "UnknownDatabase";
                result.Warnings.Add("DatabaseMapping had no SourceDatabase — defaulted to 'UnknownDatabase'.");
            }

            var targetDatabaseName = string.IsNullOrWhiteSpace(dbMapping.TargetDatabase)
                ? databaseName
                : dbMapping.TargetDatabase;

            var cosmosLs = _linkedServiceBuilder.BuildCosmosLinkedService(databaseName, registry, options);
            await WriteArtifactAsync(linkedServicesDir, cosmosLs.Name, cosmosLs, result, cancellationToken).ConfigureAwait(false);
            result.LinkedServiceCount++;

            var sanitisedDb = AdfNameRegistry.Sanitize(databaseName);
            // Per-database, env-overridable values seeded into the deployment template.
            parameterTemplate[$"{ParameterCatalog.CosmosAccountEndpoint}_{sanitisedDb}"] = ParameterTemplateEntry.Placeholder(
                $"{ParameterCatalog.CosmosAccountEndpoint}_{sanitisedDb}",
                "https://<cosmos-account>.documents.azure.com:443/");
            parameterTemplate[$"{ParameterCatalog.CosmosDatabaseName}_{sanitisedDb}"] = ParameterTemplateEntry.Default(
                $"{ParameterCatalog.CosmosDatabaseName}_{sanitisedDb}", databaseName);
            parameterTemplate[$"{ParameterCatalog.SqlDatabaseName}_{sanitisedDb}"] = ParameterTemplateEntry.Default(
                $"{ParameterCatalog.SqlDatabaseName}_{sanitisedDb}", targetDatabaseName);
            if (options.UseAzureKeyVault && !options.UseManagedIdentityForCosmos)
            {
                parameterTemplate[$"{ParameterCatalog.CosmosAccountKeySecretName}_{sanitisedDb}"] = ParameterTemplateEntry.Placeholder(
                    $"{ParameterCatalog.CosmosAccountKeySecretName}_{sanitisedDb}",
                    "<key-vault-secret-name-for-cosmos-account-key>");
            }

            var copyActivities = new List<PipelineActivity>();
            foreach (var mapping in dbMapping.ContainerMappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceDataset = _datasetBuilder.BuildCosmosCollectionDataset(databaseName, mapping, cosmosLs.Name, registry);
                await WriteArtifactAsync(datasetsDir, sourceDataset.Name, sourceDataset, result, cancellationToken).ConfigureAwait(false);
                result.DatasetCount++;

                var sinkDataset = _datasetBuilder.BuildAzureSqlTableDataset(mapping, azureSqlLs.Name, registry);
                await WriteArtifactAsync(datasetsDir, sinkDataset.Name, sinkDataset, result, cancellationToken).ConfigureAwait(false);
                result.DatasetCount++;

                var built = _copyActivityBuilder.Build(mapping, sourceDataset.Name, sinkDataset.Name, options.WriteBehavior, registry, options);
                copyActivities.Add(built.Activity);
                result.CopyActivityCount++;
                foreach (var warning in built.Warnings)
                {
                    result.Warnings.Add(warning);
                }
            }

            // Build the per-database pipeline `parameters` block once. Defaults make the
            // pipeline runnable stand-alone; master pipeline overrides per environment.
            // Build the per-database pipeline `parameters` block once. Defaults make the
            // pipeline runnable stand-alone; master pipeline overrides per environment.
            var perPipelineParameters = BuildPerDatabasePipelineParameters(databaseName, targetDatabaseName, options);

            // #143: extend per-copy activities with on-failure notification pairs (when opted in).
            var activitiesForPipeline = new List<PipelineActivity>(copyActivities.Count * 3);
            if (options.EmitFailureNotification && options.PerCopyFailureNotification)
            {
                foreach (var copy in copyActivities)
                {
                    activitiesForPipeline.Add(copy);
                    var pair = _failureNotificationBuilder.Build(copy, "perCopy", registry);
                    activitiesForPipeline.Add(pair.Web);
                    activitiesForPipeline.Add(pair.Fail);
                }
            }
            else
            {
                activitiesForPipeline.AddRange(copyActivities);
            }

            // Chunk on *total* activities, not just copy count, so notification pairs do not
            // accidentally push a chunk over ADF's per-pipeline activity cap.
            var chunkSize = Math.Max(1, options.MaxActivitiesPerPipeline);
            var totalChunks = CountChunks(activitiesForPipeline, chunkSize, options);
            var chunks = SplitChunks(activitiesForPipeline, chunkSize, options);
            totalChunks = chunks.Count;
            for (var chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
            {
                var slice = chunks[chunkIdx];
                var desired = totalChunks == 1
                    ? $"Migrate_{databaseName}"
                    : $"Migrate_{databaseName}_part{(chunkIdx + 1):D2}";
                var pipelineName = registry.Allocate(desired, $"pipeline|migrate|{databaseName}|chunk{chunkIdx}");

                var pipeline = new PipelineResource
                {
                    Name = pipelineName,
                    Properties = new PipelineProperties
                    {
                        Activities = slice,
                        Annotations = BuildPipelineAnnotations(
                            new[]
                            {
                                $"Migration pipeline for Cosmos database '{databaseName}'.",
                                totalChunks > 1
                                    ? $"Chunk {chunkIdx + 1} of {totalChunks} (per-pipeline activity cap: {chunkSize})."
                                    : "Single-chunk pipeline.",
                                "Retry / fault-tolerance configured in #143; modify CopyActivityPolicy on the generator to change defaults.",
                                "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issues #141, #142, #143 & #144).",
                                "migration",
                                "cosmos→sql",
                                $"db:{databaseName}",
                            },
                            options),
                        AdditionalProperties = new Dictionary<string, object?>
                        {
                            ["parameters"] = perPipelineParameters,
                        },
                    },
                };
                await WriteArtifactAsync(pipelinesDir, pipeline.Name, pipeline, result, cancellationToken).ConfigureAwait(false);
                result.PipelineCount++;
                perDatabasePipelineExecutions.Add(new MasterExecutionPlan(pipelineName, databaseName, sanitisedDb, targetDatabaseName, options));

                if (totalChunks > 1)
                {
                    result.Warnings.Add($"Database '{databaseName}' had {activitiesForPipeline.Count} activities (after retry/notification expansion) — split into {totalChunks} pipeline files to honour the ADF per-pipeline activity limit ({chunkSize}).");
                }
            }
        }

        // 4) Master orchestrator pipeline — invokes every per-database pipeline, forwarding
        // per-database parameters so multi-database runs receive distinct values.
        if (perDatabasePipelineExecutions.Count > 0)
        {
            var masterName = registry.Allocate(MasterPipelineName, "pipeline|master");
            var masterParameters = BuildMasterPipelineParameters(perDatabasePipelineExecutions, options);
            var masterActivities = new List<PipelineActivity>();
            for (var i = 0; i < perDatabasePipelineExecutions.Count; i++)
            {
                var plan = perDatabasePipelineExecutions[i];
                var execute = BuildExecutePipelineActivity(plan, i, registry, options);
                masterActivities.Add(execute);
                if (options.EmitFailureNotification)
                {
                    var pair = _failureNotificationBuilder.Build(execute, "masterExecutePipeline", registry);
                    masterActivities.Add(pair.Web);
                    masterActivities.Add(pair.Fail);
                }
            }

            var master = new PipelineResource
            {
                Name = masterName,
                Properties = new PipelineProperties
                {
                    Activities = masterActivities,
                    Annotations = BuildPipelineAnnotations(
                        new[]
                        {
                            "Master orchestrator pipeline. Invokes every per-database migration pipeline sequentially.",
                            "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issues #141, #142, #143 & #144).",
                            "migration",
                            "cosmos→sql",
                            "scope:master",
                        },
                        options),
                    AdditionalProperties = new Dictionary<string, object?>
                    {
                        ["parameters"] = masterParameters,
                    },
                },
            };
            await WriteArtifactAsync(pipelinesDir, master.Name, master, result, cancellationToken).ConfigureAwait(false);
            result.PipelineCount++;
        }

        // #143 deployment parameters — only emit those that the active option toggles actually
        // reference, so the template stays minimal in default mode.
        if (options.EmitFailureNotification)
        {
            parameterTemplate[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.PipelineParamFailureNotificationWebhookUrl,
                "<https://prod-00.eastus.logic.azure.com/workflows/.../triggers/manual/paths/invoke?...>");
        }
        if (options.FaultTolerance.Enabled)
        {
            parameterTemplate[ParameterCatalog.PipelineParamFaultToleranceLogPath] = ParameterTemplateEntry.Default(
                ParameterCatalog.PipelineParamFaultToleranceLogPath,
                "adf-migration-faults/");
            if (string.IsNullOrWhiteSpace(options.FaultTolerance.LogStorageLinkedServiceName))
            {
                result.Warnings.Add("FaultTolerance enabled but DataFactoryGenerationOptions.FaultTolerance.LogStorageLinkedServiceName is null — logSettings were omitted on every copy activity. Supply a literal storage linked service name to persist skipped rows.");
            }
        }

        // #144 monitoring artifacts — stand-alone deployable ARM template + KQL cheat-sheet.
        // Diagnostic settings target the factory resource itself (not pipeline JSON), so
        // they live under ADF/Monitoring/ rather than alongside the pipeline files.
        if (options.Monitoring.EmitDiagnosticSettingsTemplate || options.Monitoring.EmitMonitoringQueriesCheatsheet)
        {
            var monitoringDir = EnsureDirectory(adfRoot, MonitoringFolder);
            if (options.Monitoring.EmitDiagnosticSettingsTemplate)
            {
                var template = _diagnosticSettingsBuilder.Build(options.Monitoring);
                var path = Path.Combine(monitoringDir, DiagnosticSettingsTemplateFileName);
                await File.WriteAllTextAsync(path, AdfJsonSerializer.Serialize(template), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                result.GeneratedFiles.Add(path);

                // Deployment-template parameters land in the diagnostic-settings template
                // itself, but also surface in adf-parameters.template.json so operators
                // can review every value they need to supply for an end-to-end deploy.
                parameterTemplate[ParameterCatalog.MonitoringParamDataFactoryName] = ParameterTemplateEntry.Placeholder(
                    ParameterCatalog.MonitoringParamDataFactoryName, "<azure-data-factory-name>");
                parameterTemplate[ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId] = ParameterTemplateEntry.Placeholder(
                    ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId,
                    "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.OperationalInsights/workspaces/<workspace>");
                parameterTemplate[ParameterCatalog.MonitoringParamDiagnosticSettingName] = ParameterTemplateEntry.Default(
                    ParameterCatalog.MonitoringParamDiagnosticSettingName,
                    options.Monitoring.DiagnosticSettingName);
            }
            if (options.Monitoring.EmitMonitoringQueriesCheatsheet)
            {
                var path = Path.Combine(monitoringDir, MonitoringQueriesFileName);
                await File.WriteAllTextAsync(path, _diagnosticSettingsBuilder.BuildKqlCheatsheet(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                result.GeneratedFiles.Add(path);
            }
        }

        await WriteParameterTemplateAsync(adfRoot, parameterTemplate, options, result, cancellationToken).ConfigureAwait(false);
        await WriteReadmeAsync(adfRoot, result, options, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ADF generation complete — {Pipelines} pipeline(s), {CopyActivities} copy activity(ies), {Datasets} dataset(s), {LinkedServices} linked service(s), {Warnings} warning(s).",
            result.PipelineCount, result.CopyActivityCount, result.DatasetCount, result.LinkedServiceCount, result.Warnings.Count);

        return result;
    }

    private static Dictionary<string, object?> BuildPerDatabasePipelineParameters(
        string sourceDatabaseName,
        string targetDatabaseName,
        DataFactoryGenerationOptions options)
    {
        var parameters = new Dictionary<string, object?>
        {
            [ParameterCatalog.PipelineParamCosmosDatabaseName] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = sourceDatabaseName,
            },
            [ParameterCatalog.PipelineParamSqlDatabaseName] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = targetDatabaseName,
            },
        };
        if (options.EmitFailureNotification)
        {
            parameters[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = string.Empty,
            };
        }
        if (options.FaultTolerance.Enabled)
        {
            parameters[ParameterCatalog.PipelineParamFaultToleranceLogPath] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = "adf-migration-faults/",
            };
        }
        return parameters;
    }

    private static Dictionary<string, object?> BuildMasterPipelineParameters(
        IReadOnlyCollection<MasterExecutionPlan> plans,
        DataFactoryGenerationOptions options)
    {
        var parameters = new Dictionary<string, object?>();
        // Distinct per source database — each child gets the right values.
        foreach (var sanitisedDb in plans.Select(p => p.SanitisedDatabaseName).Distinct(StringComparer.Ordinal))
        {
            var plan = plans.First(p => p.SanitisedDatabaseName == sanitisedDb);
            parameters[$"{ParameterCatalog.PipelineParamCosmosDatabaseName}_{sanitisedDb}"] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = plan.SourceDatabaseName,
            };
            parameters[$"{ParameterCatalog.PipelineParamSqlDatabaseName}_{sanitisedDb}"] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = plan.TargetDatabaseName,
            };
        }
        if (options.EmitFailureNotification)
        {
            parameters[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = string.Empty,
            };
        }
        if (options.FaultTolerance.Enabled)
        {
            parameters[ParameterCatalog.PipelineParamFaultToleranceLogPath] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = "adf-migration-faults/",
            };
        }
        return parameters;
    }

    private static PipelineActivity BuildExecutePipelineActivity(
        MasterExecutionPlan plan,
        int idx,
        AdfNameRegistry registry,
        DataFactoryGenerationOptions options)
    {
        var name = registry.Allocate($"Run_{plan.PipelineName}", $"activity|execute|{plan.PipelineName}|{idx}");
        var executeParameters = new Dictionary<string, object?>
        {
            [ParameterCatalog.PipelineParamCosmosDatabaseName] = new Dictionary<string, object?>
            {
                ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}_{plan.SanitisedDatabaseName}",
                ["type"] = "Expression",
            },
            [ParameterCatalog.PipelineParamSqlDatabaseName] = new Dictionary<string, object?>
            {
                ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}_{plan.SanitisedDatabaseName}",
                ["type"] = "Expression",
            },
        };
        if (options.EmitFailureNotification)
        {
            executeParameters[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] = new Dictionary<string, object?>
            {
                ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamFailureNotificationWebhookUrl}",
                ["type"] = "Expression",
            };
        }
        if (options.FaultTolerance.Enabled)
        {
            executeParameters[ParameterCatalog.PipelineParamFaultToleranceLogPath] = new Dictionary<string, object?>
            {
                ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamFaultToleranceLogPath}",
                ["type"] = "Expression",
            };
        }

        return new PipelineActivity
        {
            Name = name,
            Type = "ExecutePipeline",
            TypeProperties =
            {
                ["pipeline"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = plan.PipelineName,
                    ["type"] = "PipelineReference",
                },
                ["waitOnCompletion"] = true,
                ["parameters"] = executeParameters,
            },
            // #143 — every ExecutePipeline gets a `policy` block in the extension bag.
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["policy"] = ActivityPolicyBuilder.ForExecutePipeline(options.ExecutePipelinePolicy),
            },
        };
    }

    /// <summary>
    /// Splits the ordered activity list into chunks of at most <paramref name="chunkSize"/>
    /// activities, while keeping every Copy activity together with its sibling Web + Fail
    /// notification activities (so a chunk boundary never separates a notification pair from
    /// its upstream Copy and break the <c>dependsOn</c> graph).
    /// </summary>
    private static List<List<PipelineActivity>> SplitChunks(
        List<PipelineActivity> ordered,
        int chunkSize,
        DataFactoryGenerationOptions options)
    {
        var groupSize = options.EmitFailureNotification && options.PerCopyFailureNotification ? 3 : 1;
        var chunks = new List<List<PipelineActivity>>();
        var current = new List<PipelineActivity>(chunkSize);
        for (var i = 0; i < ordered.Count; i += groupSize)
        {
            var groupEnd = Math.Min(ordered.Count, i + groupSize);
            var groupCount = groupEnd - i;
            if (current.Count + groupCount > chunkSize && current.Count > 0)
            {
                chunks.Add(current);
                current = new List<PipelineActivity>(chunkSize);
            }
            for (var j = i; j < groupEnd; j++)
            {
                current.Add(ordered[j]);
            }
        }
        if (current.Count > 0)
        {
            chunks.Add(current);
        }
        if (chunks.Count == 0)
        {
            chunks.Add(new List<PipelineActivity>());
        }
        return chunks;
    }

    private static int CountChunks(List<PipelineActivity> ordered, int chunkSize, DataFactoryGenerationOptions options)
    {
        return SplitChunks(ordered, chunkSize, options).Count;
    }

    private static List<string> BuildPipelineAnnotations(
        IEnumerable<string> baseAnnotations,
        DataFactoryGenerationOptions options)
    {
        var list = new List<string>(baseAnnotations);
        if (options.Monitoring.ExtraAnnotations is { Count: > 0 } extras)
        {
            list.AddRange(extras);
        }
        return list;
    }

    private static string EnsureDirectory(string parent, string folderName)
    {
        var path = Path.Combine(parent, folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task WriteArtifactAsync<TProps>(
        string directory,
        string artifactName,
        DataFactoryArtifact<TProps> artifact,
        DataFactoryGenerationResult result,
        CancellationToken cancellationToken) where TProps : PropertiesBase
    {
        var fileName = $"{artifactName}.json";
        var fullPath = Path.Combine(directory, fileName);
        var json = AdfJsonSerializer.Serialize(artifact);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        result.GeneratedFiles.Add(fullPath);
        _logger.LogDebug("Wrote ADF artifact {ArtifactName} to {Path}.", artifactName, fullPath);
    }

    private static async Task WriteParameterTemplateAsync(
        string adfRoot,
        SortedDictionary<string, ParameterTemplateEntry> entries,
        DataFactoryGenerationOptions options,
        DataFactoryGenerationResult result,
        CancellationToken ct)
    {
        // ARM-style $schema so this file slots straight into a `New-AzResourceGroupDeployment -TemplateParameterFile` call.
        var template = new Dictionary<string, object?>
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = entries.ToDictionary(
                kv => kv.Key,
                kv => (object?)new Dictionary<string, object?>
                {
                    ["value"] = kv.Value.Value,
                    ["metadata"] = new Dictionary<string, object?>
                    {
                        ["description"] = kv.Value.Description,
                        ["isPlaceholder"] = kv.Value.IsPlaceholder,
                    },
                }),
        };
        var path = Path.Combine(adfRoot, ParametersTemplateFileName);
        await File.WriteAllTextAsync(path, AdfJsonSerializer.Serialize(template), Encoding.UTF8, ct).ConfigureAwait(false);
        result.GeneratedFiles.Add(path);
    }

    private static async Task WriteReadmeAsync(string adfRoot, DataFactoryGenerationResult result, DataFactoryGenerationOptions options, CancellationToken ct)
    {
        var readme = new StringBuilder();
        readme.AppendLine("# Generated Azure Data Factory artifacts");
        readme.AppendLine();
        readme.AppendLine("These files were generated by the Cosmos DB → SQL Migration Assessment tool.");
        readme.AppendLine();
        readme.AppendLine("## Layout");
        readme.AppendLine();
        readme.AppendLine("- `LinkedServices/` — parameterised Cosmos DB linked service per source database, plus a single Azure SQL Database linked service (and optionally a Key Vault linked service).");
        readme.AppendLine("- `Datasets/` — one Cosmos collection dataset per source container, one Azure SQL table dataset per target table. All datasets declare parameters so the same JSON is reusable across environments.");
        readme.AppendLine("- `Pipelines/` — per-database migration pipelines (chunked at 40 activities each) plus a `MasterMigrationPipeline` orchestrator that forwards per-database parameters down to each child pipeline.");
        if (options.Monitoring.EmitDiagnosticSettingsTemplate || options.Monitoring.EmitMonitoringQueriesCheatsheet)
        {
            readme.AppendLine($"- `{MonitoringFolder}/` — diagnostic-settings ARM template + KQL cheat-sheet for Log Analytics monitoring (#144).");
        }
        readme.AppendLine($"- `{ParametersTemplateFileName}` — ARM-shape deployment-time parameter template. Copy to `adf-parameters.<env>.json` per environment and replace the placeholder values.");
        readme.AppendLine();
        readme.AppendLine("## Authentication");
        readme.AppendLine();
        readme.AppendLine($"- Cosmos DB linked service: **{(options.UseManagedIdentityForCosmos ? "System-Assigned Managed Identity" : (options.UseAzureKeyVault ? "Account key via Key Vault" : "Placeholder account key"))}**.");
        readme.AppendLine($"- Azure SQL linked service: **{(options.UseManagedIdentityForSql ? "System-Assigned Managed Identity" : (options.UseAzureKeyVault ? "SQL auth with password from Key Vault" : "Placeholder connection string"))}**.");
        if (options.UseManagedIdentityForCosmos)
        {
            readme.AppendLine("- The factory MI requires the **Cosmos DB Built-in Data Contributor** role on the Cosmos account.");
        }
        if (options.UseManagedIdentityForSql)
        {
            readme.AppendLine("- The factory MI requires an AAD `EXTERNAL PROVIDER` user with appropriate `db_datareader`/`db_datawriter` membership on the target database.");
        }
        readme.AppendLine();
        readme.AppendLine("## Customization");
        readme.AppendLine();
        readme.AppendLine($"- Update `{ParametersTemplateFileName}` with environment-specific values; secrets should be referenced by **Key Vault secret name**, never embedded directly.");
        readme.AppendLine("- Full ARM template wrapping (deployable via `New-AzResourceGroupDeployment`) is generated in sub-issue #146.");
        readme.AppendLine();
        if (options.Monitoring.EmitDiagnosticSettingsTemplate || options.Monitoring.EmitMonitoringQueriesCheatsheet)
        {
            readme.AppendLine("## Monitoring");
            readme.AppendLine();
            if (options.Monitoring.EmitDiagnosticSettingsTemplate)
            {
                readme.AppendLine($"- `{MonitoringFolder}/{DiagnosticSettingsTemplateFileName}` — deployable ARM template attaching `Microsoft.Insights/diagnosticSettings` (`logAnalyticsDestinationType = \"Dedicated\"`) to the factory. Deploy with `az deployment group create --template-file {MonitoringFolder}/{DiagnosticSettingsTemplateFileName} --parameters dataFactoryName=<name> logAnalyticsWorkspaceId=<id>`.");
                readme.AppendLine($"- Every Copy activity emits `userProperties` (SourceDatabase / TargetDatabase as expressions, SourceContainer / TargetSchema / TargetTable as literals) so the `UserProperties` column on `ADFActivityRun` is queryable in Log Analytics.");
            }
            if (options.Monitoring.EmitMonitoringQueriesCheatsheet)
            {
                readme.AppendLine($"- `{MonitoringFolder}/{MonitoringQueriesFileName}` — KQL cheat-sheet (runs/durations/failure rate/row-counts).");
            }
            readme.AppendLine();
        }
        readme.AppendLine("## Summary");
        readme.AppendLine();
        readme.AppendLine($"- Linked services : **{result.LinkedServiceCount}**");
        readme.AppendLine($"- Datasets        : **{result.DatasetCount}**");
        readme.AppendLine($"- Pipelines       : **{result.PipelineCount}**");
        readme.AppendLine($"- Copy activities : **{result.CopyActivityCount}**");
        readme.AppendLine($"- Warnings        : **{result.Warnings.Count}**");

        if (result.Warnings.Count > 0)
        {
            readme.AppendLine();
            readme.AppendLine("## Warnings");
            readme.AppendLine();
            foreach (var warning in result.Warnings)
            {
                readme.AppendLine($"- {warning}");
            }
        }

        var path = Path.Combine(adfRoot, "README.md");
        await File.WriteAllTextAsync(path, readme.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        result.GeneratedFiles.Add(path);
    }

    private sealed record MasterExecutionPlan(
        string PipelineName,
        string SourceDatabaseName,
        string SanitisedDatabaseName,
        string TargetDatabaseName,
        DataFactoryGenerationOptions Options);

    private readonly record struct ParameterTemplateEntry(string Name, object? Value, string Description, bool IsPlaceholder)
    {
        public static ParameterTemplateEntry Placeholder(string name, string placeholder) =>
            new(name, placeholder, $"Operator must replace this placeholder before deployment.", IsPlaceholder: true);

        public static ParameterTemplateEntry Default(string name, string defaultValue) =>
            new(name, defaultValue, $"Default value derived from the assessment; override per environment if needed.", IsPlaceholder: false);
    }
}
