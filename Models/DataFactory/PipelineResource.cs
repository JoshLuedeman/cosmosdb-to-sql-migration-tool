using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// ADF Pipeline artifact. Stand-alone JSON file under <c>ADF/Pipelines/</c>.
/// </summary>
public class PipelineResource : DataFactoryArtifact<PipelineProperties>
{
}

/// <summary>
/// Properties bag for an ADF Pipeline artifact, extending <see cref="PropertiesBase"/> with
/// the ordered list of activities that define the pipeline's execution graph.
/// </summary>
public class PipelineProperties : PropertiesBase
{
    /// <summary>Ordered list of activities in this pipeline. ADF executes activities according to their <c>dependsOn</c> graphs, not list order, but ordering conventionally matches the logical execution flow.</summary>
    [JsonPropertyName("activities")]
    public List<PipelineActivity> Activities { get; set; } = new();
}

/// <summary>
/// Pipeline activity. Activity type (e.g. <c>Copy</c>, <c>ExecutePipeline</c>) is the
/// discriminator; activity-specific configuration lives under <see cref="TypeProperties"/>.
/// Retry policy / on-failure / depends-on / linked-service refs are layered in by
/// follow-on sub-issues via the extension bag.
/// </summary>
public class PipelineActivity
{
    /// <summary>Logical activity name; must be unique within the pipeline and is referenced in <c>dependsOn</c> arrays of downstream activities.</summary>
    [JsonPropertyOrder(-100)]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>ADF activity type discriminator (e.g. <c>Copy</c>, <c>ExecutePipeline</c>, <c>Lookup</c>, <c>IfCondition</c>, <c>Fail</c>, <c>Script</c>, <c>WebActivity</c>).</summary>
    [JsonPropertyOrder(-90)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Source dataset references consumed by this activity (only meaningful for Copy activities).</summary>
    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResourceReference>? Inputs { get; set; }

    /// <summary>Sink dataset references produced by this activity (only meaningful for Copy activities).</summary>
    [JsonPropertyName("outputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResourceReference>? Outputs { get; set; }

    /// <summary>Activity-type-specific configuration (source, sink, translator, expression, scripts, etc.).</summary>
    [JsonPropertyName("typeProperties")]
    public Dictionary<string, object?> TypeProperties { get; set; } = new();

    /// <summary>Human-readable annotations surfaced in ADF Studio for this activity.</summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Annotations { get; set; }

    /// <summary>Open bag for later sub-issues (<c>policy</c>, <c>dependsOn</c>, <c>linkedServiceName</c>, …).</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
