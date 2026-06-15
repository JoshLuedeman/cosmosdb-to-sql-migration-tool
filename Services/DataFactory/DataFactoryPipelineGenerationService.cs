using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string ArmTemplateFileName = "arm-template.json";
    private const string DiagnosticSettingsTemplateFileName = "diagnostic-settings.template.json";
    private const string MonitoringQueriesFileName = "monitoring-queries.kql";
    private const string FactoryArmParameterName = "dataFactoryName";

    private readonly ILogger<DataFactoryPipelineGenerationService> _logger;
    private readonly LinkedServiceBuilder _linkedServiceBuilder;
    private readonly DatasetBuilder _datasetBuilder;
    private readonly CopyActivityBuilder _copyActivityBuilder;
    private readonly FailureNotificationBuilder _failureNotificationBuilder;
    private readonly DiagnosticSettingsTemplateBuilder _diagnosticSettingsBuilder;
    private readonly ValidationActivityBuilder _validationActivityBuilder;
    private readonly ArmTemplateBuilder _armTemplateBuilder;

    public DataFactoryPipelineGenerationService(
        ILogger<DataFactoryPipelineGenerationService> logger,
        LinkedServiceBuilder? linkedServiceBuilder = null,
        DatasetBuilder? datasetBuilder = null,
        CopyActivityBuilder? copyActivityBuilder = null,
        FailureNotificationBuilder? failureNotificationBuilder = null,
        DiagnosticSettingsTemplateBuilder? diagnosticSettingsBuilder = null,
        ValidationActivityBuilder? validationActivityBuilder = null,
        ArmTemplateBuilder? armTemplateBuilder = null)
    {
        _logger = logger;
        _linkedServiceBuilder = linkedServiceBuilder ?? new LinkedServiceBuilder();
        _datasetBuilder = datasetBuilder ?? new DatasetBuilder();
        _copyActivityBuilder = copyActivityBuilder ?? new CopyActivityBuilder();
        _failureNotificationBuilder = failureNotificationBuilder ?? new FailureNotificationBuilder();
        _diagnosticSettingsBuilder = diagnosticSettingsBuilder ?? new DiagnosticSettingsTemplateBuilder();
        _validationActivityBuilder = validationActivityBuilder ?? new ValidationActivityBuilder();
        _armTemplateBuilder = armTemplateBuilder ?? new ArmTemplateBuilder();
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
        var armResources = new List<ArmResourceInput>();

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
            await WriteArtifactAsync(linkedServicesDir, kvLs.Name, kvLs, result, cancellationToken,
                armKind: ArmTemplateBuilder.LinkedServiceKind,
                armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
            result.LinkedServiceCount++;
            parameterTemplate[ParameterCatalog.KeyVaultBaseUrl] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.KeyVaultBaseUrl,
                $"<https://your-vault.vault.azure.net/>");
        }

        // 2) Azure SQL linked service (single, shared across all target tables).
        var azureSqlLs = _linkedServiceBuilder.BuildAzureSqlLinkedService(registry, options);
        await WriteArtifactAsync(linkedServicesDir, azureSqlLs.Name, azureSqlLs, result, cancellationToken,
            armKind: ArmTemplateBuilder.LinkedServiceKind,
            armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
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
            await WriteArtifactAsync(linkedServicesDir, cosmosLs.Name, cosmosLs, result, cancellationToken,
                armKind: ArmTemplateBuilder.LinkedServiceKind,
                armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
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

            // #145: each mapping now produces a *group* of activities that must stay together
            // when chunking (Lookup pre/post + IfCondition + Web/Fail notification pair).
            var mappingGroups = new List<List<PipelineActivity>>();
            foreach (var mapping in dbMapping.ContainerMappings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceDataset = _datasetBuilder.BuildCosmosCollectionDataset(databaseName, mapping, cosmosLs.Name, registry);
                await WriteArtifactAsync(datasetsDir, sourceDataset.Name, sourceDataset, result, cancellationToken,
                    armKind: ArmTemplateBuilder.DatasetKind,
                    armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
                result.DatasetCount++;

                var sinkDataset = _datasetBuilder.BuildAzureSqlTableDataset(mapping, azureSqlLs.Name, registry);
                await WriteArtifactAsync(datasetsDir, sinkDataset.Name, sinkDataset, result, cancellationToken,
                    armKind: ArmTemplateBuilder.DatasetKind,
                    armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
                result.DatasetCount++;

                var built = _copyActivityBuilder.Build(mapping, sourceDataset.Name, sinkDataset.Name, options.WriteBehavior, registry, options);
                result.CopyActivityCount++;
                foreach (var warning in built.Warnings)
                {
                    result.Warnings.Add(warning);
                }

                // Build the per-mapping atomic group.
                var group = BuildMappingActivityGroup(
                    mapping,
                    built.Activity,
                    sourceDataset.Name,
                    sinkDataset.Name,
                    registry,
                    options,
                    result);
                mappingGroups.Add(group);
            }

            // Build the per-database pipeline `parameters` block once. Defaults make the
            // pipeline runnable stand-alone; master pipeline overrides per environment.
            var perPipelineParameters = BuildPerDatabasePipelineParameters(databaseName, targetDatabaseName, options);

            // Chunk on *total* activities, never splitting a per-mapping group. Each group already
            // bakes in the #143 Web/Fail pair (when per-copy notification is on) so the chunker only
            // has to pack groups; it cannot reorder them.
            var chunkSize = Math.Max(1, options.MaxActivitiesPerPipeline);
            var chunks = SplitChunks(mappingGroups, chunkSize);
            var totalChunks = chunks.Count;
            var totalActivitiesAcrossChunks = mappingGroups.Sum(g => g.Count);
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
                                "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issues #141, #142, #143, #144, #145 & #146).",
                                "migration",
                                "cosmos→sql",
                                $"db:{databaseName}",
                                $"validation:{(options.Validation.Enabled ? options.Validation.Strategy.ToString() : "off")}",
                            },
                            options),
                        AdditionalProperties = new Dictionary<string, object?>
                        {
                            ["parameters"] = perPipelineParameters,
                        },
                    },
                };
                await WriteArtifactAsync(pipelinesDir, pipeline.Name, pipeline, result, cancellationToken,
                    armKind: ArmTemplateBuilder.PipelineKind,
                    armAccumulator: options.EmitArmTemplate ? armResources : null,
                    pipelineParameterArmOverrides: options.EmitArmTemplate
                        ? BuildPerDatabasePipelineArmOverrides(sanitisedDb, options)
                        : null).ConfigureAwait(false);
                result.PipelineCount++;
                perDatabasePipelineExecutions.Add(new MasterExecutionPlan(pipelineName, databaseName, sanitisedDb, targetDatabaseName, options));

                if (totalChunks > 1)
                {
                    result.Warnings.Add($"Database '{databaseName}' had {totalActivitiesAcrossChunks} activities (after retry/notification/validation expansion) — split into {totalChunks} pipeline files to honour the ADF per-pipeline activity limit ({chunkSize}).");
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
                            "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issues #141, #142, #143, #144, #145 & #146).",
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
            await WriteArtifactAsync(pipelinesDir, master.Name, master, result, cancellationToken,
                armKind: ArmTemplateBuilder.PipelineKind,
                armAccumulator: options.EmitArmTemplate ? armResources : null,
                pipelineParameterArmOverrides: options.EmitArmTemplate
                    ? BuildMasterPipelineArmOverrides(perDatabasePipelineExecutions, options)
                    : null).ConfigureAwait(false);
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

        // #146 — the ARM template needs `dataFactoryName` even if monitoring isn't on.
        // Promote it to adf-parameters.template.json so the operator only maintains one
        // shared parameter file across both ARM templates.
        if (options.EmitArmTemplate
            && !parameterTemplate.ContainsKey(ParameterCatalog.MonitoringParamDataFactoryName))
        {
            parameterTemplate[ParameterCatalog.MonitoringParamDataFactoryName] = ParameterTemplateEntry.Placeholder(
                ParameterCatalog.MonitoringParamDataFactoryName, "<azure-data-factory-name>");
        }

        await WriteParameterTemplateAsync(adfRoot, parameterTemplate, options, result, cancellationToken).ConfigureAwait(false);

        // #146 — wrap every accumulated artifact in a deployable ARM template. Done AFTER
        // the parameter template so the ARM template can be the single source of truth for
        // resource shapes while the parameter file is the single source of truth for values.
        if (options.EmitArmTemplate && armResources.Count > 0)
        {
            await WriteArmTemplateAsync(adfRoot, armResources, parameterTemplate, result, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// #146 — per-database pipeline → ARM parameter map. Used by
    /// <see cref="ArmTemplateBuilder"/> to rewrite each pipeline-level
    /// <c>properties.parameters.&lt;K&gt;.defaultValue</c> to
    /// <c>"[parameters('&lt;V&gt;')]"</c>, so deployment-time parameter values actually
    /// flow into the deployed ADF pipeline defaults (rubber-duck Blocker B2).
    /// </summary>
    private static Dictionary<string, string> BuildPerDatabasePipelineArmOverrides(
        string sanitisedDb,
        DataFactoryGenerationOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ParameterCatalog.PipelineParamCosmosDatabaseName] =
                $"{ParameterCatalog.PipelineParamCosmosDatabaseName}_{sanitisedDb}",
            [ParameterCatalog.PipelineParamSqlDatabaseName] =
                $"{ParameterCatalog.PipelineParamSqlDatabaseName}_{sanitisedDb}",
        };
        if (options.EmitFailureNotification)
        {
            map[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] =
                ParameterCatalog.PipelineParamFailureNotificationWebhookUrl;
        }
        if (options.FaultTolerance.Enabled)
        {
            map[ParameterCatalog.PipelineParamFaultToleranceLogPath] =
                ParameterCatalog.PipelineParamFaultToleranceLogPath;
        }
        return map;
    }

    /// <summary>
    /// #146 — master pipeline → ARM parameter map. Master-level parameter names already
    /// match the ARM parameter names (<c>cosmosDatabaseName_&lt;db&gt;</c> etc.), so this
    /// is effectively an identity map — but the rewrite step is still needed so the
    /// pipeline's <c>defaultValue</c> becomes an ARM expression rather than a literal.
    /// </summary>
    private static Dictionary<string, string> BuildMasterPipelineArmOverrides(
        IReadOnlyCollection<MasterExecutionPlan> plans,
        DataFactoryGenerationOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sanitisedDb in plans.Select(p => p.SanitisedDatabaseName).Distinct(StringComparer.Ordinal))
        {
            var cosmosKey = $"{ParameterCatalog.PipelineParamCosmosDatabaseName}_{sanitisedDb}";
            var sqlKey = $"{ParameterCatalog.PipelineParamSqlDatabaseName}_{sanitisedDb}";
            map[cosmosKey] = cosmosKey;
            map[sqlKey] = sqlKey;
        }
        if (options.EmitFailureNotification)
        {
            map[ParameterCatalog.PipelineParamFailureNotificationWebhookUrl] =
                ParameterCatalog.PipelineParamFailureNotificationWebhookUrl;
        }
        if (options.FaultTolerance.Enabled)
        {
            map[ParameterCatalog.PipelineParamFaultToleranceLogPath] =
                ParameterCatalog.PipelineParamFaultToleranceLogPath;
        }
        return map;
    }

    private async Task WriteArmTemplateAsync(
        string adfRoot,
        IReadOnlyList<ArmResourceInput> armResources,
        SortedDictionary<string, ParameterTemplateEntry> parameterTemplate,
        DataFactoryGenerationResult result,
        CancellationToken cancellationToken)
    {
        // Convert parameter-template entries (deployment-parameter shape) to
        // ArmParameterDefinition (ARM template shape). The two shapes are different files
        // and serve different roles; rubber-duck Blocker B2 caught the confusion.
        var armParameters = new Dictionary<string, ArmParameterDefinition>(StringComparer.Ordinal);
        foreach (var (key, entry) in parameterTemplate)
        {
            armParameters[key] = ArmParameterDefinition.String(
                entry.Value?.ToString() ?? string.Empty,
                entry.Description);
        }
        // The factory name parameter is the one parameter the operator MUST supply at
        // deployment time — no sensible default exists.
        armParameters[FactoryArmParameterName] = ArmParameterDefinition.Required(
            "string",
            "Name of the target Azure Data Factory resource (must already exist in the resource group).");

        var template = _armTemplateBuilder.Build(armResources, armParameters, FactoryArmParameterName);
        var path = Path.Combine(adfRoot, ArmTemplateFileName);
        await File.WriteAllTextAsync(path, AdfJsonSerializer.Serialize(template), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        result.GeneratedFiles.Add(path);
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
    /// #145: builds the per-mapping activity group. The group ALWAYS contains the
    /// Copy activity and never reorders activities relative to their <c>dependsOn</c>
    /// graph. Layout (logical execution order):
    /// <list type="bullet">
    ///   <item>No validation, no per-copy notification: <c>[Copy]</c></item>
    ///   <item>No validation, per-copy notification: <c>[Copy, Web, Fail]</c></item>
    ///   <item>Validation, no per-copy notification: <c>[LookupSrc, Copy, LookupTgt, If(+nested Fail)]</c></item>
    ///   <item>Validation + per-copy notification: <c>[LookupSrc, Copy, Web, Fail, LookupTgt, If(+nested Fail)]</c></item>
    /// </list>
    /// When validation fires for this mapping, Copy.dependsOn(LookupSrc, Succeeded) is
    /// MERGED into <see cref="PipelineActivity.AdditionalProperties"/> so the #143 policy
    /// and #144 userProperties blocks already in there are preserved.
    /// </summary>
    private List<PipelineActivity> BuildMappingActivityGroup(
        ContainerMapping mapping,
        PipelineActivity copy,
        string sourceDatasetName,
        string sinkDatasetName,
        AdfNameRegistry registry,
        DataFactoryGenerationOptions options,
        DataFactoryGenerationResult result)
    {
        var group = new List<PipelineActivity>(7);

        // Validation toggles: enabled, per-mapping size threshold, master "off" switch.
        var validationOn = options.Validation.Enabled;
        if (validationOn
            && options.Validation.SkipForContainerDocumentCountAbove is long threshold
            && mapping.EstimatedRowCount > threshold)
        {
            result.Warnings.Add(
                $"Row-count validation skipped for container '{mapping.SourceContainer}' (estimated {mapping.EstimatedRowCount:N0} rows > threshold {threshold:N0}) — Cosmos COUNT(1) would be too expensive.");
            validationOn = false;
        }

        var perCopyNotification = options.EmitFailureNotification && options.PerCopyFailureNotification;

        if (validationOn)
        {
            var triplet = _validationActivityBuilder.Build(
                mapping, sourceDatasetName, sinkDatasetName, copy.Name, registry, options.Validation);

            // MERGE dependsOn into the existing AdditionalProperties bag so #143's policy
            // and #144's userProperties are preserved (caught by rubber-duck on #145).
            copy.AdditionalProperties ??= new Dictionary<string, object?>();
            copy.AdditionalProperties["dependsOn"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["activity"] = triplet.LookupSourceName,
                    ["dependencyConditions"] = new[] { "Succeeded" },
                },
            };

            group.Add(triplet.LookupSource);
            group.Add(copy);
            if (perCopyNotification)
            {
                // Per-copy notification fires on direct Copy failure. Validation-specific
                // failures still surface via the nested Fail bubbling up to the master
                // pipeline's notification (when EmitFailureNotification is on).
                var pair = _failureNotificationBuilder.Build(copy, "perCopy", registry);
                group.Add(pair.Web);
                group.Add(pair.Fail);
            }
            group.Add(triplet.LookupTarget);
            group.Add(triplet.IfCondition);
        }
        else
        {
            group.Add(copy);
            if (perCopyNotification)
            {
                var pair = _failureNotificationBuilder.Build(copy, "perCopy", registry);
                group.Add(pair.Web);
                group.Add(pair.Fail);
            }
        }

        // Guard: ADF's 40-activity-per-pipeline cap INCLUDES nested activities under
        // IfCondition/ForEach/Until/Switch (caught by rubber-duck on #145). A single
        // mapping group must therefore fit inside the cap on its own; we have no way
        // to split it without breaking the dependsOn graph.
        // We count nested Fail (1) under the IfCondition explicitly because the
        // IfCondition itself only contributes 1 to group.Count.
        var nestedCount = validationOn ? 1 : 0; // nested Fail in ifFalseActivities
        var groupActivityCount = group.Count + nestedCount;
        var cap = Math.Max(1, options.MaxActivitiesPerPipeline);
        if (groupActivityCount > cap)
        {
            throw new InvalidOperationException(
                $"Per-mapping activity group for '{mapping.SourceContainer}' has {groupActivityCount} activities (including nested), which exceeds MaxActivitiesPerPipeline={cap}. " +
                $"Lower the per-copy/validation feature set or raise the cap.");
        }

        return group;
    }

    /// <summary>
    /// Packs ordered per-mapping groups into chunks of at most <paramref name="chunkSize"/>
    /// activities each (counting nested activities). A group is never split — moving a Web
    /// or Fail activity into another chunk would break its <c>dependsOn</c> reference, and
    /// splitting a validation triplet across chunks would orphan the IfCondition.
    /// </summary>
    private static List<List<PipelineActivity>> SplitChunks(
        List<List<PipelineActivity>> groups,
        int chunkSize)
    {
        var chunks = new List<List<PipelineActivity>>();
        var current = new List<PipelineActivity>(chunkSize);
        var currentCount = 0;
        foreach (var group in groups)
        {
            var groupCount = CountWithNested(group);
            if (currentCount + groupCount > chunkSize && current.Count > 0)
            {
                chunks.Add(current);
                current = new List<PipelineActivity>(chunkSize);
                currentCount = 0;
            }
            current.AddRange(group);
            currentCount += groupCount;
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

    /// <summary>
    /// Counts an activity group toward ADF's per-pipeline activity cap, including any
    /// nested activities under <c>IfCondition</c> (<c>ifTrueActivities</c> /
    /// <c>ifFalseActivities</c>), <c>ForEach</c>, <c>Until</c>, <c>Switch</c>, etc.
    /// </summary>
    private static int CountWithNested(IEnumerable<PipelineActivity> group)
    {
        var total = 0;
        foreach (var activity in group)
        {
            total++;
            if (activity.TypeProperties.TryGetValue("ifFalseActivities", out var fObj)
                && fObj is IEnumerable<object?> falseActivities)
            {
                total += falseActivities.Count();
            }
            if (activity.TypeProperties.TryGetValue("ifTrueActivities", out var tObj)
                && tObj is IEnumerable<object?> trueActivities)
            {
                total += trueActivities.Count();
            }
            if (activity.TypeProperties.TryGetValue("activities", out var aObj)
                && aObj is IEnumerable<object?> nestedActivities)
            {
                total += nestedActivities.Count();
            }
        }
        return total;
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
        CancellationToken cancellationToken,
        string? armKind = null,
        List<ArmResourceInput>? armAccumulator = null,
        IReadOnlyDictionary<string, string>? pipelineParameterArmOverrides = null) where TProps : PropertiesBase
    {
        var fileName = $"{artifactName}.json";
        var fullPath = Path.Combine(directory, fileName);
        var json = AdfJsonSerializer.Serialize(artifact);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        result.GeneratedFiles.Add(fullPath);

        if (armKind is not null && armAccumulator is not null)
        {
            // Parse the just-written JSON so the ARM template payload is byte-identical
            // to the file on disk. Round-tripping through JsonNode is also the canonical
            // way to flatten `[JsonExtensionData] AdditionalProperties` into ordinary keys
            // (caught by rubber-duck on #146 — Blocker 3).
            var node = JsonNode.Parse(json) as JsonObject;
            if (node?["properties"] is JsonObject propsObj)
            {
                armAccumulator.Add(new ArmResourceInput(
                    armKind, artifactName, propsObj, pipelineParameterArmOverrides));
            }
        }

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
        if (options.EmitArmTemplate)
        {
            readme.AppendLine($"- `{ArmTemplateFileName}` — deployable Azure Resource Manager template wrapping every linked service / dataset / pipeline above as `Microsoft.DataFactory/factories/{{kind}}` child resources (#146).");
        }
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
        if (options.EmitArmTemplate)
        {
            readme.AppendLine($"- A deployable ARM template (`{ArmTemplateFileName}`, sub-issue #146) wraps every linked service, dataset, and pipeline below as `Microsoft.DataFactory/factories/{{kind}}` child resources. Deploy with `New-AzResourceGroupDeployment -ResourceGroupName <rg> -TemplateFile {ArmTemplateFileName} -TemplateParameterFile {ParametersTemplateFileName}` (after you have populated `{ParametersTemplateFileName}`). The template's `dataFactoryName` parameter is required (no default) and must name an existing factory in the resource group.");
            readme.AppendLine("- Pipeline-level parameter defaults (`cosmosDatabaseName`, `sqlDatabaseName`, `failureNotificationWebhookUrl`, `faultToleranceLogPath`) are rewritten to ARM `parameters()` expressions during template generation, so the value an operator supplies via `-TemplateParameterFile` flows into the deployed pipeline as its default — no separate parameter wiring needed.");
        }
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
        if (options.Validation.Enabled)
        {
            readme.AppendLine("## Validation (#145)");
            readme.AppendLine();
            readme.AppendLine("- Every Copy activity is bracketed by `LookupSrc` (Cosmos `SELECT COUNT(1) AS docCount FROM c`) → `Copy` → `LookupTgt` (Azure SQL `SELECT COUNT_BIG(1) AS docCount FROM [schema].[table]`) → `IfCondition` (compares the two counts) → nested `Fail` (on mismatch).");
            readme.AppendLine($"- Strategy: `{options.Validation.Strategy}` (tolerance: {options.Validation.Tolerance}). `RowCountExact` requires `|source - target| <= tolerance`; `RowCountAtLeast` requires `target >= source - tolerance` (use when the source can grow during the load).");
            if (options.Validation.SkipForContainerDocumentCountAbove is long skipThreshold)
            {
                readme.AppendLine($"- Containers with more than **{skipThreshold:N0}** estimated rows skip validation (Cosmos full-collection counts are RU-expensive). Override via `ValidationOptions.SkipForContainerDocumentCountAbove`.");
            }
            readme.AppendLine("- Validation failures throw a `Fail` activity inside the `IfCondition`'s `ifFalseActivities`. The failure bubbles up to the per-database pipeline and on to the master pipeline (where it triggers the #143 webhook when enabled).");
            readme.AppendLine("- Disable globally with `ValidationOptions.Enabled = false`. The activity group counts (with nested `Fail`) toward ADF's per-pipeline activity cap of 40.");
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
