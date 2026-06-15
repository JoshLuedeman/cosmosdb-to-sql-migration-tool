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
/// via <c>ExecutePipeline</c> activities.
/// </summary>
public sealed class DataFactoryPipelineGenerationService : IDataFactoryPipelineGenerator
{
    private const string AdfRootFolder = "ADF";
    private const string LinkedServicesFolder = "LinkedServices";
    private const string DatasetsFolder = "Datasets";
    private const string PipelinesFolder = "Pipelines";
    private const string MasterPipelineName = "MasterMigrationPipeline";

    private readonly ILogger<DataFactoryPipelineGenerationService> _logger;
    private readonly LinkedServiceBuilder _linkedServiceBuilder;
    private readonly DatasetBuilder _datasetBuilder;
    private readonly CopyActivityBuilder _copyActivityBuilder;

    public DataFactoryPipelineGenerationService(
        ILogger<DataFactoryPipelineGenerationService> logger,
        LinkedServiceBuilder? linkedServiceBuilder = null,
        DatasetBuilder? datasetBuilder = null,
        CopyActivityBuilder? copyActivityBuilder = null)
    {
        _logger = logger;
        _linkedServiceBuilder = linkedServiceBuilder ?? new LinkedServiceBuilder();
        _datasetBuilder = datasetBuilder ?? new DatasetBuilder();
        _copyActivityBuilder = copyActivityBuilder ?? new CopyActivityBuilder();
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

        var databaseMappings = assessment.SqlAssessment?.DatabaseMappings ?? new List<DatabaseMapping>();
        if (databaseMappings.Count == 0)
        {
            _logger.LogWarning("No database mappings present in assessment — ADF generation produced no copy artifacts.");
            result.Warnings.Add("Assessment contained no DatabaseMappings — no ADF pipelines were generated.");
            // Still create the directory + README so the operator sees the section exists.
            var emptyAdfRoot = EnsureDirectory(outputDirectory, AdfRootFolder);
            await WriteReadmeAsync(emptyAdfRoot, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var adfRoot = EnsureDirectory(outputDirectory, AdfRootFolder);
        var linkedServicesDir = EnsureDirectory(adfRoot, LinkedServicesFolder);
        var datasetsDir = EnsureDirectory(adfRoot, DatasetsFolder);
        var pipelinesDir = EnsureDirectory(adfRoot, PipelinesFolder);

        // 1) Azure SQL linked service (single, shared across all target tables).
        var azureSqlLs = _linkedServiceBuilder.BuildAzureSqlLinkedService(registry);
        await WriteArtifactAsync(linkedServicesDir, azureSqlLs.Name, azureSqlLs, result, cancellationToken).ConfigureAwait(false);
        result.LinkedServiceCount++;

        // 2) Per-database linked service + per-mapping datasets + per-database pipeline(s).
        var perDatabasePipelineNames = new List<string>();

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

            var cosmosLs = _linkedServiceBuilder.BuildCosmosLinkedService(databaseName, registry);
            await WriteArtifactAsync(linkedServicesDir, cosmosLs.Name, cosmosLs, result, cancellationToken).ConfigureAwait(false);
            result.LinkedServiceCount++;

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

                var built = _copyActivityBuilder.Build(mapping, sourceDataset.Name, sinkDataset.Name, options.WriteBehavior, registry);
                copyActivities.Add(built.Activity);
                result.CopyActivityCount++;
                foreach (var warning in built.Warnings)
                {
                    result.Warnings.Add(warning);
                }
            }

            // Chunk activities into pipeline files respecting the ADF per-pipeline limit.
            var chunkSize = Math.Max(1, options.MaxActivitiesPerPipeline);
            var totalChunks = (int)Math.Ceiling(copyActivities.Count / (double)chunkSize);
            for (var chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
            {
                var slice = copyActivities
                    .Skip(chunkIdx * chunkSize)
                    .Take(chunkSize)
                    .ToList();
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
                        Annotations = new List<string>
                        {
                            $"Migration pipeline for Cosmos database '{databaseName}'.",
                            totalChunks > 1
                                ? $"Chunk {chunkIdx + 1} of {totalChunks} (per-pipeline activity cap: {chunkSize})."
                                : "Single-chunk pipeline.",
                            "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issue #141).",
                        },
                    },
                };
                await WriteArtifactAsync(pipelinesDir, pipeline.Name, pipeline, result, cancellationToken).ConfigureAwait(false);
                result.PipelineCount++;
                perDatabasePipelineNames.Add(pipeline.Name);

                if (totalChunks > 1)
                {
                    result.Warnings.Add($"Database '{databaseName}' had {copyActivities.Count} copy activities — split into {totalChunks} pipeline files to honour the ADF per-pipeline activity limit ({chunkSize}).");
                }
            }
        }

        // 3) Master orchestrator pipeline — invokes every per-database pipeline.
        if (perDatabasePipelineNames.Count > 0)
        {
            var masterName = registry.Allocate(MasterPipelineName, "pipeline|master");
            var master = new PipelineResource
            {
                Name = masterName,
                Properties = new PipelineProperties
                {
                    Activities = perDatabasePipelineNames
                        .Select((pipelineName, idx) => new PipelineActivity
                        {
                            Name = registry.Allocate($"Run_{pipelineName}", $"activity|execute|{pipelineName}|{idx}"),
                            Type = "ExecutePipeline",
                            TypeProperties =
                            {
                                ["pipeline"] = new Dictionary<string, object?>
                                {
                                    ["referenceName"] = pipelineName,
                                    ["type"] = "PipelineReference",
                                },
                                ["waitOnCompletion"] = true,
                            },
                        })
                        .ToList(),
                    Annotations = new List<string>
                    {
                        "Master orchestrator pipeline. Invokes every per-database migration pipeline sequentially.",
                        "Generated by Cosmos to SQL Assessment tool (parent #70, sub-issue #141).",
                    },
                },
            };
            await WriteArtifactAsync(pipelinesDir, master.Name, master, result, cancellationToken).ConfigureAwait(false);
            result.PipelineCount++;
        }

        await WriteReadmeAsync(adfRoot, result, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ADF generation complete — {Pipelines} pipeline(s), {CopyActivities} copy activity(ies), {Datasets} dataset(s), {LinkedServices} linked service(s), {Warnings} warning(s).",
            result.PipelineCount, result.CopyActivityCount, result.DatasetCount, result.LinkedServiceCount, result.Warnings.Count);

        return result;
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

    private static async Task WriteReadmeAsync(string adfRoot, DataFactoryGenerationResult result, CancellationToken ct)
    {
        var readme = new StringBuilder();
        readme.AppendLine("# Generated Azure Data Factory artifacts");
        readme.AppendLine();
        readme.AppendLine("These files were generated by the Cosmos DB → SQL Migration Assessment tool.");
        readme.AppendLine();
        readme.AppendLine("## Layout");
        readme.AppendLine();
        readme.AppendLine("- `LinkedServices/` — one Cosmos DB linked service per source database, plus a single Azure SQL Database linked service.");
        readme.AppendLine("- `Datasets/` — one Cosmos collection dataset per source container, one Azure SQL table dataset per target table.");
        readme.AppendLine("- `Pipelines/` — per-database migration pipelines (chunked at 40 activities each) plus a `MasterMigrationPipeline` orchestrator.");
        readme.AppendLine();
        readme.AppendLine("## Customization");
        readme.AppendLine();
        readme.AppendLine("Connection strings in `LinkedServices/` are placeholders. Replace them with parameter references before deploying (parameterization & ARM deployment land in sub-issues #142 and #146).");
        readme.AppendLine();
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
}
