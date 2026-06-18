using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Serializable wizard configuration that can be saved/loaded from JSON.
/// </summary>
internal sealed class WizardConfiguration
{
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("analyzeAllDatabases")]
    public bool AnalyzeAllDatabases { get; set; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("autoDiscover")]
    public bool AutoDiscover { get; set; }

    [JsonPropertyName("outputDirectory")]
    public string? OutputDirectory { get; set; }

    [JsonPropertyName("assessmentOnly")]
    public bool AssessmentOnly { get; set; }

    [JsonPropertyName("projectOnly")]
    public bool ProjectOnly { get; set; }
}
