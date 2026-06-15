using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// ADF Pipeline artifact. Stand-alone JSON file under <c>ADF/Pipelines/</c>.
/// </summary>
public class PipelineResource : DataFactoryArtifact<PipelineProperties>
{
}

public class PipelineProperties : PropertiesBase
{
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
    [JsonPropertyOrder(-100)]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyOrder(-90)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResourceReference>? Inputs { get; set; }

    [JsonPropertyName("outputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResourceReference>? Outputs { get; set; }

    [JsonPropertyName("typeProperties")]
    public Dictionary<string, object?> TypeProperties { get; set; } = new();

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Annotations { get; set; }

    /// <summary>Open bag for later sub-issues (<c>policy</c>, <c>dependsOn</c>, <c>linkedServiceName</c>, …).</summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
