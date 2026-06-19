using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Discovers Azure subscription and resource group for a Cosmos DB account
/// by querying Azure Resource Graph.
/// </summary>
internal sealed partial class ResourceGraphDiscoveryService : IResourceGraphDiscoveryService
{
    private const string QueryTemplate =
        "Resources " +
        "| where type =~ 'microsoft.documentdb/databaseaccounts' " +
        "| where name =~ '{0}' " +
        "| project subscriptionId, resourceGroup, name";

    private readonly IResourceGraphQueryClient _queryClient;
    private readonly ILogger<ResourceGraphDiscoveryService> _logger;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,42}[a-z0-9]$")]
    private static partial Regex AccountNamePattern();

    public ResourceGraphDiscoveryService(
        IResourceGraphQueryClient queryClient,
        ILogger<ResourceGraphDiscoveryService> logger)
    {
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CosmosAccountLocation?> FindCosmosAccountAsync(
        string accountName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            _logger.LogWarning("Account name is null or empty; cannot query Resource Graph");
            return null;
        }

        var normalizedName = accountName.ToLowerInvariant();
        if (!AccountNamePattern().IsMatch(normalizedName))
        {
            _logger.LogWarning("Account name '{AccountName}' does not match Cosmos DB naming rules", accountName);
            return null;
        }

        var kql = string.Format(QueryTemplate, normalizedName);

        try
        {
            var rows = await _queryClient.QueryAsync(kql, cancellationToken);

            if (rows.Count == 0)
            {
                _logger.LogInformation("No Cosmos DB account '{AccountName}' found in Resource Graph", normalizedName);
                return null;
            }

            if (rows.Count > 1)
            {
                _logger.LogWarning(
                    "Multiple Cosmos DB accounts found matching '{AccountName}' ({Count} results); using first match",
                    normalizedName, rows.Count);
            }

            var row = rows[0];
            var subscriptionId = row.GetProperty("subscriptionId").GetString();
            var resourceGroup = row.GetProperty("resourceGroup").GetString();
            var name = row.GetProperty("name").GetString();

            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceGroup))
            {
                _logger.LogWarning("Resource Graph returned incomplete data for account '{AccountName}'", normalizedName);
                return null;
            }

            _logger.LogInformation(
                "Discovered Cosmos DB account '{AccountName}' in subscription '{SubscriptionId}', resource group '{ResourceGroup}'",
                name, subscriptionId, resourceGroup);

            return new CosmosAccountLocation(subscriptionId, resourceGroup, name ?? normalizedName);
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Resource Graph for Cosmos DB account '{AccountName}'", normalizedName);
            return null;
        }
    }
}
