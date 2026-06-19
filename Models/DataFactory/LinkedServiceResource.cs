using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models.DataFactory;

/// <summary>
/// ADF Linked Service artifact. Stand-alone JSON file under <c>ADF/LinkedServices/</c>.
/// </summary>
public class LinkedServiceResource : DataFactoryArtifact<LinkedServiceProperties>
{
}

/// <summary>
/// Properties bag for an ADF Linked Service artifact, extending <see cref="PropertiesBase"/>
/// with the type discriminator and connector-specific type properties such as connection
/// strings, account endpoints, and authentication settings.
/// </summary>
public class LinkedServiceProperties : PropertiesBase
{
    /// <summary>ADF linked-service type discriminator, e.g. <c>CosmosDb</c> or <c>AzureSqlDatabase</c>.</summary>
    [JsonPropertyOrder(-50)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Connector-specific properties (e.g. <c>connectionString</c>, <c>accountEndpoint</c>).</summary>
    [JsonPropertyName("typeProperties")]
    public Dictionary<string, object?> TypeProperties { get; set; } = new();
}
