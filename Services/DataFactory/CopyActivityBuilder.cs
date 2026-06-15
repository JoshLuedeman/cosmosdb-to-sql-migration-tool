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
    public const string CosmosSourceType = "CosmosDbSqlApiSource";
    public const string AzureSqlSinkType = "AzureSqlSink";
    public const string TabularTranslatorType = "TabularTranslator";

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
        AdfNameRegistry registry)
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
                new() { ReferenceName = sourceDatasetName, Type = "DatasetReference" },
            },
            Outputs = new List<ResourceReference>
            {
                new() { ReferenceName = sinkDatasetName, Type = "DatasetReference" },
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

        return new BuildResult(activity, warnings);
    }

    public readonly record struct BuildResult(PipelineActivity Activity, IReadOnlyList<string> Warnings);
}
