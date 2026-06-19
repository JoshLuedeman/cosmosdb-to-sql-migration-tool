namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Discovers the Azure subscription and resource group for a Cosmos DB account.
/// </summary>
internal interface IResourceGraphDiscoveryService
{
    /// <summary>
    /// Finds the subscription and resource group for the given Cosmos DB account name.
    /// </summary>
    /// <param name="accountName">The Cosmos DB account name (lowercase, validated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account location, or null if not found.</returns>
    Task<CosmosAccountLocation?> FindCosmosAccountAsync(string accountName, CancellationToken cancellationToken = default);
}
