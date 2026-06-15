using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// Generic ADF resource envelope: <c>{ "name": ..., "properties": { "type": ..., ... } }</c>.
/// The envelope is strongly typed; <see cref="PropertiesBase.AdditionalProperties"/> on the
/// concrete <c>Properties</c> bag keeps everything extensible so later sub-issues (#142–#147)
/// can layer parameters, retry policies, monitoring, etc. without breaking #141's shape.
/// </summary>
/// <typeparam name="TProps">The strongly-typed <c>properties</c> bag for the resource.</typeparam>
public class DataFactoryArtifact<TProps> where TProps : PropertiesBase
{
    [JsonPropertyOrder(-100)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyOrder(-90)]
    public TProps Properties { get; set; } = default!;
}

/// <summary>
/// Base for the <c>properties</c> bag of every ADF artifact. Provides a free-form
/// <see cref="AdditionalProperties"/> extension dictionary plus the universally
/// applicable <see cref="Annotations"/> list.
/// </summary>
public abstract class PropertiesBase
{
    /// <summary>User-readable annotations attached to the artifact.</summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Annotations { get; set; }

    /// <summary>
    /// Catch-all bag for fields not yet modeled in C#. Serialized inline alongside the
    /// declared properties — this is how later sub-issues add `parameters`, `policy`,
    /// `logSettings`, etc. without having to touch the model classes.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}

/// <summary>
/// Reference to a sibling artifact by logical name. ADF uses this shape everywhere
/// (datasets in inputs/outputs, linked services on datasets, pipelines in
/// <c>ExecutePipeline</c> activities, etc.). The optional <see cref="Parameters"/>
/// bag is used by callers that pass values into a parameterised dataset, linked
/// service, or pipeline reference.
/// </summary>
public class ResourceReference
{
    [JsonPropertyName("referenceName")]
    public string ReferenceName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Parameter values forwarded into the referenced artifact. Each entry is either a
    /// plain literal (string/number/bool) or an ADF expression. For <c>ExecutePipeline</c>
    /// references the canonical shape is <c>{ "value": "@…", "type": "Expression" }</c>;
    /// for dataset references a bare string expression is accepted.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Parameters { get; set; }
}
