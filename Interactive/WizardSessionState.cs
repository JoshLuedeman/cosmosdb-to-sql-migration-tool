using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Tracks the wizard's progress through its steps so an interrupted
/// session can be resumed from where it left off.
/// </summary>
internal sealed class WizardSessionState
{
    [JsonPropertyName("currentStep")]
    public int CurrentStep { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("analyzeAllDatabases")]
    public bool? AnalyzeAllDatabases { get; set; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("includeMonitor")]
    public bool? IncludeMonitor { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("autoDiscover")]
    public bool? AutoDiscover { get; set; }

    [JsonPropertyName("outputDirectory")]
    public string? OutputDirectory { get; set; }

    [JsonPropertyName("reportType")]
    public string? ReportType { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
