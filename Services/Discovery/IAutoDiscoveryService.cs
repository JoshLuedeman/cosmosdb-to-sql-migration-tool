namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Top-level auto-discovery service that orchestrates endpoint parsing,
/// Resource Graph lookup, diagnostic settings query, and result caching.
/// </summary>
internal interface IAutoDiscoveryService
{
    /// <summary>
    /// Discovers Azure Monitor configuration for the given Cosmos DB endpoint.
    /// Results are cached for the lifetime of the service instance.
    /// </summary>
    /// <param name="endpointUrl">The Cosmos DB account endpoint URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovery result, or null if discovery failed.</returns>
    Task<DiscoveryResult?> DiscoverAsync(string? endpointUrl, CancellationToken cancellationToken = default);
}
