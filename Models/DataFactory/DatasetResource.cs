using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// ADF Dataset artifact. Stand-alone JSON file under <c>ADF/Datasets/</c>.
/// </summary>
public class DatasetResource : DataFactoryArtifact<DatasetProperties>
{
}

/// <summary>
/// Properties bag for an ADF Dataset artifact, extending <see cref="PropertiesBase"/> with
/// the type discriminator, linked-service reference, and connector-specific type properties.
/// </summary>
public class DatasetProperties : PropertiesBase
{
    /// <summary>ADF dataset type discriminator, e.g. <c>CosmosDbSqlApiCollection</c> or <c>AzureSqlTable</c>.</summary>
    [JsonPropertyOrder(-50)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Reference to the linked service that provides the connection for this dataset, along with any parameter values passed into it.</summary>
    [JsonPropertyName("linkedServiceName")]
    public ResourceReference LinkedServiceName { get; set; } = new();

    /// <summary>Connector-specific type properties for this dataset (e.g. <c>collectionName</c> for Cosmos, <c>schema</c> and <c>table</c> for Azure SQL).</summary>
    [JsonPropertyName("typeProperties")]
    public Dictionary<string, object?> TypeProperties { get; set; } = new();

    /// <summary>Optional schema columns (kept lightweight for #141 — populated in later issues).</summary>
    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Schema { get; set; }
}
