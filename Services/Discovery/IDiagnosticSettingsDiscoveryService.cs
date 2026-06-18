namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Discovers the Log Analytics workspace linked to a Cosmos DB account
/// via its diagnostic settings.
/// </summary>
internal interface IDiagnosticSettingsDiscoveryService
{
    /// <summary>
    /// Finds the Log Analytics workspace ID associated with the Cosmos DB account.
    /// </summary>
    /// <param name="location">The Cosmos DB account location from Resource Graph.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Log Analytics workspace ID (GUID), or null if not found.</returns>
    Task<string?> FindLinkedWorkspaceAsync(CosmosAccountLocation location, CancellationToken cancellationToken = default);
}
