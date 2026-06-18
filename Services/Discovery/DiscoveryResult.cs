namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Aggregated result of the Azure Monitor auto-discovery pipeline.
/// </summary>
internal sealed record DiscoveryResult(
    CosmosAccountLocation AccountLocation,
    string? WorkspaceResourceId);
