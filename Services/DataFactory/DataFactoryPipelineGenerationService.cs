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
    private const string SqlFolder = "SQL";
    private const string MasterPipelineName = "MasterMigrationPipeline";
    private const string ParametersTemplateFileName = "adf-parameters.template.json";
    private const string ArmTemplateFileName = "arm-template.json";
    private const string DiagnosticSettingsTemplateFileName = "diagnostic-settings.template.json";
    private const string MonitoringQueriesFileName = "monitoring-queries.kql";
    private const string FactoryArmParameterName = "dataFactoryName";
    private const string WatermarkSchemaFileName = "Create__AdfWatermark.sql";

    private readonly ILogger<DataFactoryPipelineGenerationService> _logger;
    private readonly LinkedServiceBuilder _linkedServiceBuilder;
    private readonly DatasetBuilder _datasetBuilder;
    private readonly CopyActivityBuilder _copyActivityBuilder;
    private readonly FailureNotificationBuilder _failureNotificationBuilder;
    private readonly DiagnosticSettingsTemplateBuilder _diagnosticSettingsBuilder;
    private readonly ValidationActivityBuilder _validationActivityBuilder;
    private readonly ArmTemplateBuilder _armTemplateBuilder;
    private readonly IncrementalCopyActivityBuilder _incrementalCopyBuilder;
    private readonly WatermarkSchemaBuilder _watermarkSchemaBuilder;

    /// <summary>
    /// Initialises the service with optional overrides for each sub-builder. All builders
    /// default to their own parameterless constructors when <c>null</c>, so the class is
    /// directly instantiable without a DI container for testing.
    /// </summary>
    /// <param name="logger">Logger for progress and diagnostic messages.</param>
    /// <param name="linkedServiceBuilder">Builder for Cosmos / Azure SQL / Key Vault linked services; defaults to a new <see cref="LinkedServiceBuilder"/> when <c>null</c>.</param>
    /// <param name="datasetBuilder">Builder for Cosmos collection and Azure SQL table datasets; defaults to a new <see cref="DatasetBuilder"/> when <c>null</c>.</param>
    /// <param name="copyActivityBuilder">Builder for per-mapping Copy activities; defaults to a new <see cref="CopyActivityBuilder"/> when <c>null</c>.</param>
    /// <param name="failureNotificationBuilder">Builder for Web + Fail notification pairs; defaults to a new <see cref="FailureNotificationBuilder"/> when <c>null</c>.</param>
    /// <param name="diagnosticSettingsBuilder">Builder for the diagnostic-settings ARM template; defaults to a new <see cref="DiagnosticSettingsTemplateBuilder"/> when <c>null</c>.</param>
    /// <param name="validationActivityBuilder">Builder for row-count validation triplets; defaults to a new <see cref="ValidationActivityBuilder"/> when <c>null</c>.</param>
    /// <param name="armTemplateBuilder">Builder for the deployable ARM template wrapping all ADF artifacts; defaults to a new <see cref="ArmTemplateBuilder"/> when <c>null</c>.</param>
    /// <param name="incrementalCopyBuilder">Builder for the incremental watermark activity group; defaults to a new <see cref="IncrementalCopyActivityBuilder"/> when <c>null</c>.</param>
    /// <param name="watermarkSchemaBuilder">Builder for watermark DDL and SQL scripts; defaults to a new <see cref="WatermarkSchemaBuilder"/> when <c>null</c>.</param>
    public DataFactoryPipelineGenerationService(
        ILogger<DataFactoryPipelineGenerationService> logger,
        LinkedServiceBuilder? linkedServiceBuilder = null,
        DatasetBuilder? datasetBuilder = null,
        CopyActivityBuilder? copyActivityBuilder = null,
        FailureNotificationBuilder? failureNotificationBuilder = null,
        DiagnosticSettingsTemplateBuilder? diagnosticSettingsBuilder = null,
        ValidationActivityBuilder? validationActivityBuilder = null,
        ArmTemplateBuilder? armTemplateBuilder = null,
        IncrementalCopyActivityBuilder? incrementalCopyBuilder = null,
        WatermarkSchemaBuilder? watermarkSchemaBuilder = null)
    {
        _logger = logger;
        _linkedServiceBuilder = linkedServiceBuilder ?? new LinkedServiceBuilder();
        _datasetBuilder = datasetBuilder ?? new DatasetBuilder();
        _copyActivityBuilder = copyActivityBuilder ?? new CopyActivityBuilder();
        _failureNotificationBuilder = failureNotificationBuilder ?? new FailureNotificationBuilder();
        _diagnosticSettingsBuilder = diagnosticSettingsBuilder ?? new DiagnosticSettingsTemplateBuilder();
        _validationActivityBuilder = validationActivityBuilder ?? new ValidationActivityBuilder();
        _armTemplateBuilder = armTemplateBuilder ?? new ArmTemplateBuilder();
        _watermarkSchemaBuilder = watermarkSchemaBuilder ?? new WatermarkSchemaBuilder();
        _incrementalCopyBuilder = incrementalCopyBuilder ?? new IncrementalCopyActivityBuilder(_watermarkSchemaBuilder);
    }

    /// <summary>
    /// Generates all ADF artifacts for the container-to-table mappings in <paramref name="assessment"/>
    /// and writes them under <paramref name="outputDirectory"/>. Implements <see cref="IDataFactoryPipelineGenerator.GenerateAsync"/>.
    /// </summary>
    /// <param name="assessment">Assessment result whose <c>SqlAssessment.DatabaseMappings</c> drive artifact generation.</param>
    /// <param name="outputDirectory">Root path; the <c>ADF/</c> folder tree is created beneath it.</param>
    /// <param name="options">Generation toggles; defaults to a safe full-load configuration when <c>null</c>.</param>
    /// <param name="cancellationToken">Token for cooperative cancellation of async file I/O.</param>
    /// <returns>A <see cref="DataFactoryGenerationResult"/> listing every file written and any operator-facing warnings.</returns>
    /// <example>
    /// <code language="csharp"><![CDATA[
    /// using Microsoft.Extensions.DependencyInjection;
    ///
    /// var generator = serviceProvider.GetRequiredService<IDataFactoryPipelineGenerator>();
    ///
    /// DataFactoryGenerationResult result = await generator.GenerateAsync(
    ///     assessmentResult,
    ///     outputDirectory: "out",
    ///     options: new DataFactoryGenerationOptions()); // defaults to a full-load configuration
    ///
    /// Console.WriteLine($"Wrote {result.GeneratedFiles.Count} files " +
    ///     $"({result.PipelineCount} pipelines, {result.CopyActivityCount} copy activities).");
    /// foreach (var warning in result.Warnings)
    ///     Console.WriteLine($"WARN: {warning}");
    /// ]]></code>
    /// </example>
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

        // #147: validate incremental options up front. Insert-sink + incremental is a
        // duplicate-row hazard on Copy retry / boundary-second re-read; the option
        // RequireUpsertSink lets the operator decide whether to hard-fail or warn.
        if (options.IncrementalCopy.Enabled && options.WriteBehavior == SinkWriteBehavior.Insert)
        {
            if (options.IncrementalCopy.RequireUpsertSink)
            {
                throw new InvalidOperationException(
                    "IncrementalCopy.Enabled is true with WriteBehavior=Insert and RequireUpsertSink=true. "
                    + "Insert is not idempotent and can duplicate rows on Copy retry / boundary-second re-read. "
                    + "Either switch to SinkWriteBehavior.Upsert or set RequireUpsertSink=false to acknowledge the risk.");
            }
            result.Warnings.Add(
                "IncrementalCopy is enabled with WriteBehavior=Insert. Insert is not idempotent — Copy retries or boundary-second re-reads can duplicate rows. Prefer Upsert sinks for incremental workflows, or wire a staging-table MERGE pattern downstream.");
        }

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

        // #147: shared watermark dataset. Built once and reused by every per-mapping
        // LookupWatermark across every per-database pipeline. The dataset's
        // sqlDatabaseName parameter is forwarded from each pipeline's matching
        // pipeline parameter (so the same dataset drives runs that target different
        // databases). Only emitted when incremental is on.
        string? watermarkDatasetName = null;
        if (options.IncrementalCopy.Enabled)
        {
            var watermarkDataset = _datasetBuilder.BuildWatermarkDataset(
                options.IncrementalCopy, azureSqlLs.Name, registry);
            await WriteArtifactAsync(datasetsDir, watermarkDataset.Name, watermarkDataset, result, cancellationToken,
                armKind: ArmTemplateBuilder.DatasetKind,
                armAccumulator: options.EmitArmTemplate ? armResources : null).ConfigureAwait(false);
            result.DatasetCount++;
            watermarkDatasetName = watermarkDataset.Name;
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
            // #147: per-mapping incremental metadata, accumulated so the per-db pipeline can
            // declare matching parameters / variables and the ARM-template wiring forwards them.
            var perDbIncrementalGroups = new List<IncrementalGroup>();
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

                // #147 — opt-in: wrap this mapping in the incremental Lookup → SetVar →
                // SetVar → Copy(override) → Script chain when the allow-list permits.
                IncrementalGroup? incrementalGroup = null;
                if (IsIncrementalMapping(mapping, options.IncrementalCopy))
                {
                    incrementalGroup = _incrementalCopyBuilder.BuildGroup(
                        mapping,
                        databaseName,
                        targetDatabaseName,
                        azureSqlLs.Name,
                        watermarkDatasetName!,
                        built.Activity,
                        registry,
                        options.IncrementalCopy);
                    perDbIncrementalGroups.Add(incrementalGroup);
                }

                // Build the per-mapping atomic group.
                var group = BuildMappingActivityGroup(
                    mapping,
                    built.Activity,
                    sourceDataset.Name,
                    sinkDataset.Name,
                    registry,
                    options,
                    result,
                    incrementalGroup);
                mappingGroups.Add(group);
            }

            // Build the per-database pipeline `parameters` block once. Defaults make the
            // pipeline runnable stand-alone; master pipeline overrides per environment.
            var perPipelineParameters = BuildPerDatabasePipelineParameters(databaseName, targetDatabaseName, options, perDbIncrementalGroups);

            // #147 — per-db pipeline `variables` block (used to ferry the watermark
            // values between Lookup, SetVariable, and Copy activities).
            var perPipelineVariables = BuildPerDatabasePipelineVariables(perDbIncrementalGroups);

            // #147 — preamble Script activity that self-bootstraps the watermark table
            // on first run, so the pipeline can be deployed without pre-running DDL.
            PipelineActivity? ensureWatermarkActivity = null;
            if (perDbIncrementalGroups.Count > 0
                && options.IncrementalCopy.EnsureWatermarkTableAtRuntime)
            {
                ensureWatermarkActivity = _incrementalCopyBuilder.BuildEnsureTableActivity(
                    azureSqlLs.Name, registry, options.IncrementalCopy);
            }

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

                // #147 — preamble lives ONLY in the first chunk's pipeline. Every
                // incremental group across all chunks then gets an extra dependsOn entry
                // pointing at it (see WireIncrementalPreambleDependsOn below).
                if (chunkIdx == 0 && ensureWatermarkActivity is not null)
                {
                    var withPreamble = new List<PipelineActivity>(slice.Count + 1) { ensureWatermarkActivity };
                    withPreamble.AddRange(slice);
                    slice = withPreamble;
                }
                if (ensureWatermarkActivity is not null)
                {
                    // Every incremental Lookup in this chunk depends on the preamble's success.
                    // First chunk: direct in-pipeline dependency (Succeeded).
                    // Later chunks: ADF cannot express cross-pipeline dependsOn, so the preamble
                    // runs again as a no-op (the DDL is idempotent via `IF OBJECT_ID(...) IS NULL`).
                    // We log this so operators know why later chunks repeat the activity.
                    if (chunkIdx > 0)
                    {
                        // Insert a fresh EnsureWatermarkTable into later chunks too — the
                        // builder's allocate has already locked the original name, so we
                        // create a uniquely-suffixed copy.
                        var extraEnsure = _incrementalCopyBuilder.BuildEnsureTableActivity(
                            azureSqlLs.Name, registry, options.IncrementalCopy);
                        var withPreambleN = new List<PipelineActivity>(slice.Count + 1) { extraEnsure };
                        withPreambleN.AddRange(slice);
                        slice = withPreambleN;
                        WireIncrementalPreambleDependsOn(perDbIncrementalGroups, slice, extraEnsure.Name);
                    }
                    else
                    {
                        WireIncrementalPreambleDependsOn(perDbIncrementalGroups, slice, ensureWatermarkActivity.Name);
                    }
                }

                var desired = totalChunks == 1
                    ? $"Migrate_{databaseName}"
                    : $"Migrate_{databaseName}_part{(chunkIdx + 1):D2}";
                var pipelineName = registry.Allocate(desired, $"pipeline|migrate|{databaseName}|chunk{chunkIdx}");

                var additionalProps = new Dictionary<string, object?>
                {
                    ["parameters"] = perPipelineParameters,
                };
                if (perPipelineVariables.Count > 0)
                {
                    additionalProps["variables"] = perPipelineVariables;
                }

                var pipelineAnnotations = new List<string>
                {
                    $"Migration pipeline for Cosmos database '{databaseName}'.",
                    totalChunks > 1
                        ? $"Chunk {chunkIdx + 1} of {totalChunks} (per-pipeline activity cap: {chunkSize})."
                        : "Single-chunk pipeline.",
                    "Retry / fault-tolerance configured in #143; modify CopyActivityPolicy on the generator to change defaults.",
                    "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issues #141, #142, #143, #144, #145, #146 & #147).",
                    "migration",
                    "cosmos→sql",
                    $"db:{databaseName}",
                    $"validation:{(options.Validation.Enabled ? options.Validation.Strategy.ToString() : "off")}",
                };
                if (perDbIncrementalGroups.Count > 0)
                {
                    pipelineAnnotations.Add("incremental:_ts-watermark");
                    foreach (var ig in perDbIncrementalGroups)
                    {
                        pipelineAnnotations.Add($"incremental:{ig.MappingKey}");
                    }
                }

                var pipeline = new PipelineResource
                {
                    Name = pipelineName,
                    Properties = new PipelineProperties
                    {
                        Activities = slice,
                        Annotations = BuildPipelineAnnotations(pipelineAnnotations, options),
                        AdditionalProperties = additionalProps,
                    },
                };
                await WriteArtifactAsync(pipelinesDir, pipeline.Name, pipeline, result, cancellationToken,
                    armKind: ArmTemplateBuilder.PipelineKind,
                    armAccumulator: options.EmitArmTemplate ? armResources : null,
                    pipelineParameterArmOverrides: options.EmitArmTemplate
                        ? BuildPerDatabasePipelineArmOverrides(sanitisedDb, options, perDbIncrementalGroups)
                        : null).ConfigureAwait(false);
                result.PipelineCount++;
                perDatabasePipelineExecutions.Add(new MasterExecutionPlan(pipelineName, databaseName, sanitisedDb, targetDatabaseName, options, perDbIncrementalGroups));

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

        // #147 — surface every per-mapping initial-watermark parameter into the
        // deployment-time parameter template so operators can override per
        // environment without re-running the assessment.
        if (options.IncrementalCopy.Enabled)
        {
            foreach (var paramName in perDatabasePipelineExecutions
                .SelectMany(p => p.IncrementalGroups)
                .Select(g => g.InitialWatermarkParameterName)
                .Distinct(StringComparer.Ordinal))
            {
                parameterTemplate[paramName] = ParameterTemplateEntry.Default(
                    paramName,
                    options.IncrementalCopy.InitialWatermarkSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        // #147 — stand-alone SQL DDL file the operator can pre-run if they prefer to
        // manage the watermark table outside the pipeline self-bootstrap path.
        if (options.IncrementalCopy.Enabled && options.IncrementalCopy.EmitWatermarkSchemaScript)
        {
            var sqlDir = EnsureDirectory(adfRoot, SqlFolder);
            var sqlPath = Path.Combine(sqlDir, WatermarkSchemaFileName);
            var sqlBody = _watermarkSchemaBuilder.BuildCreateScript(options.IncrementalCopy);
            await File.WriteAllTextAsync(sqlPath, sqlBody, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            result.GeneratedFiles.Add(sqlPath);
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
        DataFactoryGenerationOptions options,
        IReadOnlyList<IncrementalGroup>? incrementalGroups = null)
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
        // #147 — one `incrementalInitialWatermark_<m>` per incremental mapping. Always
        // emitted as `string` so the same parameter shape carries either a Unix-seconds
        // integer or the literal "0" sentinel. Type chosen to match the variable type
        // (SetVariable on a String variable rejects integer literals).
        if (incrementalGroups is not null)
        {
            foreach (var ig in incrementalGroups)
            {
                parameters[ig.InitialWatermarkParameterName] = new Dictionary<string, object?>
                {
                    ["type"] = ParameterCatalog.ParameterTypeString,
                    ["defaultValue"] = options.IncrementalCopy.InitialWatermarkSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
            }
        }
        return parameters;
    }

    /// <summary>
    /// #147 — per-database pipeline `variables` block. Two String variables per
    /// incremental mapping (<c>lastWatermark_&lt;m&gt;</c> and <c>newWatermark_&lt;m&gt;</c>).
    /// </summary>
    private static Dictionary<string, object?> BuildPerDatabasePipelineVariables(
        IReadOnlyList<IncrementalGroup> incrementalGroups)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var ig in incrementalGroups)
        {
            variables[ig.LastWatermarkVariableName] = new Dictionary<string, object?>
            {
                ["type"] = "String",
                ["defaultValue"] = "0",
            };
            variables[ig.NewWatermarkVariableName] = new Dictionary<string, object?>
            {
                ["type"] = "String",
                ["defaultValue"] = "0",
            };
        }
        return variables;
    }

    /// <summary>
    /// #147 — wires every <see cref="IncrementalGroup.LookupActivityName"/> in
    /// <paramref name="chunkActivities"/> to <paramref name="preambleActivityName"/>
    /// via <c>dependsOn</c>, so the watermark table is guaranteed to exist before
    /// any Lookup attempts to read it. MERGEs into the activity's
    /// <c>AdditionalProperties</c> bag so any existing <c>dependsOn</c> entries
    /// (none today, but defensive for forward changes) are preserved.
    /// </summary>
    private static void WireIncrementalPreambleDependsOn(
        IReadOnlyList<IncrementalGroup> allIncrementalGroups,
        List<PipelineActivity> chunkActivities,
        string preambleActivityName)
    {
        var lookupNamesInChunk = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in chunkActivities)
        {
            // The Lookup activities for incremental are typed "Lookup" and named with the
            // LookupWatermark_<m> prefix; match by exact name set so we don't accidentally
            // wire validation Lookups (LookupSrc_/LookupTgt_) to the preamble.
            lookupNamesInChunk.Add(activity.Name);
        }
        foreach (var ig in allIncrementalGroups)
        {
            if (!lookupNamesInChunk.Contains(ig.LookupActivityName))
            {
                continue;
            }
            IncrementalCopyActivityBuilder.MergeDependsOn(ig.LookupWatermark, preambleActivityName);
        }
    }

    /// <summary>
    /// #147 — true when the mapping should be incremental: incremental on, AND
    /// (no allow-list OR allow-list contains the source container name).
    /// </summary>
    private static bool IsIncrementalMapping(ContainerMapping mapping, IncrementalCopyOptions options)
    {
        if (!options.Enabled)
        {
            return false;
        }
        if (options.ContainerAllowList.Count == 0)
        {
            return true;
        }
        foreach (var allowed in options.ContainerAllowList)
        {
            if (string.Equals(allowed, mapping.SourceContainer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
        // #147 — surface every per-mapping initial-watermark parameter at the master
        // level so the operator can override per environment without touching every
        // child pipeline. Distinct() guards against double-emission when a chunked
        // pipeline shows up multiple times in `plans`.
        foreach (var paramName in plans
            .SelectMany(p => p.IncrementalGroups)
            .Select(g => g.InitialWatermarkParameterName)
            .Distinct(StringComparer.Ordinal))
        {
            parameters[paramName] = new Dictionary<string, object?>
            {
                ["type"] = ParameterCatalog.ParameterTypeString,
                ["defaultValue"] = options.IncrementalCopy.InitialWatermarkSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        DataFactoryGenerationOptions options,
        IReadOnlyList<IncrementalGroup>? incrementalGroups = null)
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
        // #147 — identity mapping for each `incrementalInitialWatermark_<m>` param.
        if (incrementalGroups is not null)
        {
            foreach (var ig in incrementalGroups)
            {
                map[ig.InitialWatermarkParameterName] = ig.InitialWatermarkParameterName;
            }
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
        // #147 — identity mapping for every distinct incremental param.
        foreach (var paramName in plans
            .SelectMany(p => p.IncrementalGroups)
            .Select(g => g.InitialWatermarkParameterName)
            .Distinct(StringComparer.Ordinal))
        {
            map[paramName] = paramName;
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
        // #147 — forward every per-mapping initial-watermark parameter from master to
        // child. Master params have the same name as child params (e.g. they're
        // distinct per mapping so we don't suffix by database), so the forwarding is
        // a simple identity map.
        foreach (var ig in plan.IncrementalGroups)
        {
            executeParameters[ig.InitialWatermarkParameterName] = new Dictionary<string, object?>
            {
                ["value"] = $"@pipeline().parameters.{ig.InitialWatermarkParameterName}",
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
        DataFactoryGenerationResult result,
        IncrementalGroup? incrementalGroup = null)
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

        // #147 — when an incremental window is in effect AND validation strategy is
        // "exact", we omit the LookupTgt + IfCondition triplet by default because the
        // target table is cumulative across runs (a full-table COUNT_BIG on the sink
        // would never match the per-window source count). RowCountAtLeast keeps the
        // triplet because operators using it have explicitly opted into the
        // shortfall-tolerant comparison.
        if (validationOn && incrementalGroup is not null && options.Validation.Strategy == ValidationStrategy.RowCountExact)
        {
            validationOn = false;
            result.Warnings.Add(
                $"Row-count validation skipped for incremental container '{mapping.SourceContainer}' (Strategy=RowCountExact + Incremental is inconsistent: target is cumulative, source is per-window). Switch ValidationOptions.Strategy to RowCountAtLeast if you still want a delta check.");
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
            // #147 — Lookup → SetLastTs → SetNewTs come before the Copy. They're
            // already wired via dependsOn inside IncrementalCopyActivityBuilder.BuildGroup,
            // and the Copy's existing dependsOn chain has been extended to include SetNewTs.
            if (incrementalGroup is not null)
            {
                group.Add(incrementalGroup.LookupWatermark);
                group.Add(incrementalGroup.SetLastTs);
                group.Add(incrementalGroup.SetNewTs);
            }
            group.Add(copy);
            if (perCopyNotification)
            {
                var pair = _failureNotificationBuilder.Build(copy, "perCopy", registry);
                group.Add(pair.Web);
                group.Add(pair.Fail);
            }
            // ScriptUpdateWatermark runs AFTER the Copy succeeds — placed last in the
            // group so the dependsOn chain matches the execution order in the file.
            if (incrementalGroup is not null)
            {
                group.Add(incrementalGroup.ScriptUpdateWatermark);
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
        if (options.IncrementalCopy.Enabled)
        {
            readme.AppendLine("## Incremental load (#147)");
            readme.AppendLine();
            readme.AppendLine($"- Each per-mapping Copy is wrapped in `LookupWatermark → SetVariable(lastTs) → SetVariable(newTs) → Copy(WHERE c.{options.IncrementalCopy.TimestampField} > lastTs AND c.{options.IncrementalCopy.TimestampField} <= newTs) → Script(MERGE)`.");
            readme.AppendLine($"- Watermark storage: `[{options.IncrementalCopy.WatermarkSchemaName}].[{options.IncrementalCopy.WatermarkTableName}]` — composite PK `mappingKey NVARCHAR(450)` so multiple source databases / fan-out targets stay isolated.");
            readme.AppendLine($"- Safety lag: **{options.IncrementalCopy.WatermarkSafetyLagSeconds}s** subtracted from `utcnow()` so writes committed during the boundary second are picked up by the next run, not skipped. `newTs` is also clamped to `lastTs` so very low/zero safety-lag values never push the watermark backwards.");
            readme.AppendLine("- Race-condition protection: the `MERGE` includes a `WHEN MATCHED AND S.[lastTs] > T.[lastTs]` guard so a slower concurrent run cannot move the watermark backwards.");
            readme.AppendLine("- **Cosmos `_ts` does NOT capture deletes.** This pattern handles inserts + updates only; soft-deletes via a status field are workable but hard-deletes need a separate reconciliation pass (see parent #69 for the change-feed-based approach).");
            if (options.WriteBehavior == SinkWriteBehavior.Insert)
            {
                readme.AppendLine("- ⚠️ **Sink writeBehavior is `Insert` while incremental is enabled.** Copy retries / boundary-second re-reads can duplicate rows. Switch to `Upsert` (with target-table keys), or wire a staging-table MERGE downstream. Set `IncrementalCopyOptions.RequireUpsertSink = true` to make this a generation-time error.");
            }
            if (options.IncrementalCopy.EmitWatermarkSchemaScript)
            {
                readme.AppendLine($"- `SQL/{WatermarkSchemaFileName}` — idempotent `IF OBJECT_ID(...) IS NULL CREATE TABLE` DDL. Operators preferring strict change-control can pre-run this and set `EnsureWatermarkTableAtRuntime = false`.");
            }
            if (options.IncrementalCopy.EnsureWatermarkTableAtRuntime)
            {
                readme.AppendLine("- Each per-database pipeline begins with an `EnsureWatermarkTable` Script activity (running the same idempotent DDL) so the pipeline self-bootstraps on first run.");
            }
            if (options.IncrementalCopy.ContainerAllowList.Count > 0)
            {
                readme.AppendLine($"- Incremental allow-list: **{string.Join(", ", options.IncrementalCopy.ContainerAllowList)}**. All other containers stay full-load.");
            }
            readme.AppendLine("- Initial bootstrap value per mapping: `incrementalInitialWatermark_<mappingKey>` parameter (default = `IncrementalCopyOptions.InitialWatermarkSeconds`, currently `" + options.IncrementalCopy.InitialWatermarkSeconds + "` = from beginning). Override per environment via the parameter template.");
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
        DataFactoryGenerationOptions Options,
        IReadOnlyList<IncrementalGroup> IncrementalGroups);

    private readonly record struct ParameterTemplateEntry(string Name, object? Value, string Description, bool IsPlaceholder)
    {
        public static ParameterTemplateEntry Placeholder(string name, string placeholder) =>
            new(name, placeholder, $"Operator must replace this placeholder before deployment.", IsPlaceholder: true);

        public static ParameterTemplateEntry Default(string name, string defaultValue) =>
            new(name, defaultValue, $"Default value derived from the assessment; override per environment if needed.", IsPlaceholder: false);
    }
}
