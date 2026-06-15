using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// ADF Dataset artifact. Stand-alone JSON file under <c>ADF/Datasets/</c>.
/// </summary>
public class DatasetResource : DataFactoryArtifact<DatasetProperties>
{
}

public class DatasetProperties : PropertiesBase
{
    /// <summary>ADF dataset type discriminator, e.g. <c>CosmosDbSqlApiCollection</c> or <c>AzureSqlTable</c>.</summary>
    [JsonPropertyOrder(-50)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("linkedServiceName")]
    public ResourceReference LinkedServiceName { get; set; } = new();

    [JsonPropertyName("typeProperties")]
    public Dictionary<string, object?> TypeProperties { get; set; } = new();

    /// <summary>Optional schema columns (kept lightweight for #141 — populated in later issues).</summary>
    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Schema { get; set; }
}
