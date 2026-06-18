using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Translates a <see cref="ContainerMapping"/> into a fully-formed ADF Copy activity.
/// Field mappings flagged <c>RequiresTransformation</c> are deliberately skipped here
/// (annotated on the activity + surfaced as a warning) so #145 (data validation /
/// mapping data flows) can take over without rewriting this stage.
/// </summary>
public sealed class CopyActivityBuilder
{
    /// <summary>ADF source type discriminator for reading from a Cosmos DB SQL API collection.</summary>
    public const string CosmosSourceType = "CosmosDbSqlApiSource";
    /// <summary>ADF sink type discriminator for writing to an Azure SQL table.</summary>
    public const string AzureSqlSinkType = "AzureSqlSink";
    /// <summary>ADF translator type that maps Cosmos JSON paths to SQL column names.</summary>
    public const string TabularTranslatorType = "TabularTranslator";

    private readonly UserPropertiesBuilder _userPropertiesBuilder;

    /// <summary>
    /// Initialises a <see cref="CopyActivityBuilder"/> with an optional custom
    /// <see cref="UserPropertiesBuilder"/>. When <c>null</c>, a default instance is created.
    /// </summary>
    /// <param name="userPropertiesBuilder">Builder for the monitoring <c>userProperties</c> block; defaults to a new <see cref="UserPropertiesBuilder"/> when <c>null</c>.</param>
    public CopyActivityBuilder(UserPropertiesBuilder? userPropertiesBuilder = null)
    {
        _userPropertiesBuilder = userPropertiesBuilder ?? new UserPropertiesBuilder();
    }

    /// <summary>
    /// Builds a Copy activity referencing the previously allocated source / sink dataset
    /// names. Naming uses the same <see cref="AdfNameRegistry"/> instance to guarantee
    /// activity-name uniqueness across the entire generated pipeline set.
    /// </summary>
    public BuildResult Build(
        ContainerMapping mapping,
        string sourceDatasetName,
        string sinkDatasetName,
        SinkWriteBehavior writeBehavior,
        AdfNameRegistry registry,
        DataFactoryGenerationOptions? options = null)
    {
        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;
        var desiredName = $"Copy_{mapping.SourceContainer}_to_{schema}_{mapping.TargetTable}";
        var activityName = registry.Allocate(
            desiredName,
            $"activity|copy|{mapping.SourceContainer}|{schema}|{mapping.TargetTable}");

        var warnings = new List<string>();
        var annotations = new List<string>
        {
            $"Copy '{mapping.SourceContainer}' → '{schema}.{mapping.TargetTable}'.",
        };

        // Translator — only non-transformed field mappings are emitted here. Transformed
        // fields are recorded as activity annotations + warnings so the human operator
        // (and #145) knows work is pending.
        var translatorMappings = new List<Dictionary<string, object?>>();
        foreach (var field in mapping.FieldMappings)
        {
            if (field.RequiresTransformation)
            {
                var msg = $"Field '{field.SourceField}' on container '{mapping.SourceContainer}' requires transformation — skipped in copy activity (handled by #145).";
                annotations.Add($"TODO: {msg}");
                warnings.Add(msg);
                continue;
            }

            translatorMappings.Add(new Dictionary<string, object?>
            {
                ["source"] = new Dictionary<string, object?>
                {
                    ["path"] = JsonPathEscaper.ToJsonPath(field.SourceField),
                },
                ["sink"] = new Dictionary<string, object?>
                {
                    ["name"] = field.TargetColumn,
                    ["type"] = field.TargetType,
                },
            });
        }

        if (mapping.ChildTableMappings.Count > 0)
        {
            var msg = $"Container '{mapping.SourceContainer}' has {mapping.ChildTableMappings.Count} child-table mapping(s) deferred — handled by parent #70 follow-up.";
            annotations.Add($"TODO: {msg}");
            warnings.Add(msg);
        }

        var activity = new PipelineActivity
        {
            Name = activityName,
            Type = "Copy",
            Inputs = new List<ResourceReference>
            {
                new()
                {
                    ReferenceName = sourceDatasetName,
                    Type = "DatasetReference",
                    Parameters = new Dictionary<string, object?>
                    {
                        // Forward the pipeline-level Cosmos database name (env-varying);
                        // collection name is a per-mapping literal.
                        [DatasetBuilder.DatasetParamDatabaseName] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}",
                        [DatasetBuilder.DatasetParamCollectionName] = mapping.SourceContainer,
                    },
                },
            },
            Outputs = new List<ResourceReference>
            {
                new()
                {
                    ReferenceName = sinkDatasetName,
                    Type = "DatasetReference",
                    Parameters = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamSqlDatabaseName] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                        [DatasetBuilder.DatasetParamSchema] = schema,
                        [DatasetBuilder.DatasetParamTable] = mapping.TargetTable,
                    },
                },
            },
            TypeProperties =
            {
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = CosmosSourceType,
                },
                ["sink"] = new Dictionary<string, object?>
                {
                    ["type"] = AzureSqlSinkType,
                    ["writeBehavior"] = writeBehavior.ToString().ToLowerInvariant(),
                },
                ["enableStaging"] = false,
                ["translator"] = new Dictionary<string, object?>
                {
                    ["type"] = TabularTranslatorType,
                    ["mappings"] = translatorMappings,
                },
            },
            Annotations = annotations,
        };

        // #143: attach the ADF activity `policy` (retry / timeout) and optional
        // fault tolerance. Stored in the extension bag so the JSON shape matches
        // ADF's expected `{ ..., policy: {...} }` envelope without a model change.
        var opts = options ?? new DataFactoryGenerationOptions();
        activity.AdditionalProperties = new Dictionary<string, object?>
        {
            ["policy"] = ActivityPolicyBuilder.ForCopyActivity(opts.CopyPolicy, writeBehavior),
        };

        // #144: monitoring custom dimensions. MUST merge into AdditionalProperties
        // alongside `policy` — `AdditionalProperties = new { userProperties = ... }`
        // would drop the #143 policy block (caught by the rubber-duck review).
        if (opts.Monitoring.EmitUserProperties)
        {
            activity.AdditionalProperties["userProperties"] = _userPropertiesBuilder.Build(mapping);
        }

        if (opts.FaultTolerance.Enabled)
        {
            activity.TypeProperties["enableSkipIncompatibleRow"] = true;

            if (!string.IsNullOrWhiteSpace(opts.FaultTolerance.LogStorageLinkedServiceName))
            {
                activity.TypeProperties["logSettings"] = new Dictionary<string, object?>
                {
                    ["enableCopyActivityLog"] = true,
                    ["copyActivityLogSettings"] = new Dictionary<string, object?>
                    {
                        ["logLevel"] = opts.FaultTolerance.LogLevel,
                        ["enableReliableLogging"] = false,
                    },
                    ["logLocationSettings"] = new Dictionary<string, object?>
                    {
                        ["linkedServiceName"] = new Dictionary<string, object?>
                        {
                            ["referenceName"] = opts.FaultTolerance.LogStorageLinkedServiceName,
                            ["type"] = "LinkedServiceReference",
                        },
                        ["path"] = "@pipeline().parameters." + ParameterCatalog.PipelineParamFaultToleranceLogPath,
                    },
                };
            }
            else
            {
                var msg = $"FaultTolerance enabled for '{activityName}' but no LogStorageLinkedServiceName was supplied — logSettings omitted; skipped rows will not be persisted.";
                annotations.Add($"WARN: {msg}");
                warnings.Add(msg);
            }
        }

        return new BuildResult(activity, warnings);
    }

    /// <summary>
    /// Pairs the Copy activity produced by <see cref="CopyActivityBuilder.Build"/> with
    /// any warnings surfaced during translator-mapping construction (e.g. skipped
    /// transformed fields, deferred child tables).
    /// </summary>
    public readonly record struct BuildResult(PipelineActivity Activity, IReadOnlyList<string> Warnings);
}
