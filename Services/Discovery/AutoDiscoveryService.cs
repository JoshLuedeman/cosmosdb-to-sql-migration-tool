using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Orchestrates the full auto-discovery pipeline with in-memory caching.
/// Registered as singleton so cached results persist for the app lifetime.
/// </summary>
internal sealed class AutoDiscoveryService : IAutoDiscoveryService
{
    private readonly IResourceGraphDiscoveryService _resourceGraphService;
    private readonly IDiagnosticSettingsDiscoveryService _diagnosticSettingsService;
    private readonly ILogger<AutoDiscoveryService> _logger;
    private readonly ConcurrentDictionary<string, DiscoveryResult?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public AutoDiscoveryService(
        IResourceGraphDiscoveryService resourceGraphService,
        IDiagnosticSettingsDiscoveryService diagnosticSettingsService,
        ILogger<AutoDiscoveryService> logger)
    {
        _resourceGraphService = resourceGraphService ?? throw new ArgumentNullException(nameof(resourceGraphService));
        _diagnosticSettingsService = diagnosticSettingsService ?? throw new ArgumentNullException(nameof(diagnosticSettingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DiscoveryResult?> DiscoverAsync(string? endpointUrl, CancellationToken cancellationToken = default)
    {
        if (!CosmosEndpointParser.TryParseAccountName(endpointUrl, out var accountName))
        {
            _logger.LogWarning("Cannot discover monitoring configuration: endpoint URL is invalid or unrecognized");
            return null;
        }

        var cacheKey = accountName!;

        // Fast path: already cached
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("Returning cached discovery result for account '{AccountName}'", cacheKey);
            return cached;
        }

        // Serialize concurrent requests for the same account
        var semaphore = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(cacheKey, out cached))
                return cached;

            _logger.LogInformation("Starting auto-discovery for Cosmos DB account '{AccountName}'", cacheKey);

            // Step 1: Find account in Resource Graph
            var location = await _resourceGraphService.FindCosmosAccountAsync(cacheKey, cancellationToken);
            if (location == null)
            {
                _logger.LogWarning("Could not locate account '{AccountName}' in Azure Resource Graph", cacheKey);
                _cache.TryAdd(cacheKey, null);
                return null;
            }

            // Step 2: Find linked Log Analytics workspace
            var workspaceId = await _diagnosticSettingsService.FindLinkedWorkspaceAsync(location, cancellationToken);

            var result = new DiscoveryResult(location, workspaceId);
            _cache.TryAdd(cacheKey, result);

            _logger.LogInformation(
                "Auto-discovery complete for '{AccountName}': subscription={SubscriptionId}, resourceGroup={ResourceGroup}, workspace={Workspace}",
                cacheKey, location.SubscriptionId, location.ResourceGroup, workspaceId ?? "(none)");

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
