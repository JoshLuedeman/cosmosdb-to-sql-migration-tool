using Azure.Core;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Discovers the Log Analytics workspace linked to a Cosmos DB account
/// by reading its diagnostic settings.
/// </summary>
internal sealed class DiagnosticSettingsDiscoveryService : IDiagnosticSettingsDiscoveryService
{
    private readonly IDiagnosticSettingsClient _client;
    private readonly ILogger<DiagnosticSettingsDiscoveryService> _logger;

    public DiagnosticSettingsDiscoveryService(
        IDiagnosticSettingsClient client,
        ILogger<DiagnosticSettingsDiscoveryService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> FindLinkedWorkspaceAsync(
        CosmosAccountLocation location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        var resourceId = new ResourceIdentifier(
            $"/subscriptions/{location.SubscriptionId}" +
            $"/resourceGroups/{location.ResourceGroup}" +
            $"/providers/Microsoft.DocumentDB/databaseAccounts/{location.AccountName}");

        try
        {
            var settings = await _client.GetDiagnosticSettingsAsync(resourceId, cancellationToken);

            if (settings.Count == 0)
            {
                _logger.LogInformation(
                    "No diagnostic settings found for Cosmos DB account '{AccountName}'",
                    location.AccountName);
                return null;
            }

            foreach (var setting in settings)
            {
                if (!string.IsNullOrEmpty(setting.WorkspaceResourceId))
                {
                    // Extract the workspace resource ID — the Log Analytics workspace ID
                    // (GUID) is the last segment of the resource path
                    var workspaceId = ExtractWorkspaceId(setting.WorkspaceResourceId);
                    if (workspaceId != null)
                    {
                        _logger.LogInformation(
                            "Found Log Analytics workspace linked via diagnostic setting '{SettingName}': {WorkspaceId}",
                            setting.Name, workspaceId);
                        return workspaceId;
                    }
                }
            }

            _logger.LogInformation(
                "Diagnostic settings exist for '{AccountName}' but none link to a Log Analytics workspace",
                location.AccountName);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to query diagnostic settings for Cosmos DB account '{AccountName}'",
                location.AccountName);
            return null;
        }
    }

    /// <summary>
    /// Extracts the workspace name (last path segment) from a Log Analytics workspace resource ID.
    /// The resource ID format is: /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}
    /// </summary>
    internal static string? ExtractWorkspaceId(string workspaceResourceId)
    {
        if (string.IsNullOrEmpty(workspaceResourceId))
            return null;

        // Return the full resource ID — the Azure Monitor Query SDK accepts the workspace resource ID directly
        // but the tool uses workspace GUID from config. We return the resource ID and let the caller
        // decide how to use it.
        var segments = workspaceResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expected: subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}
        // We want the last segment (workspace name); the actual workspace ID (GUID) requires an additional API call
        // but for our purposes, we store the full resource ID which the Azure Monitor Query SDK can use
        if (segments.Length >= 8 &&
            segments[^2].Equals("workspaces", StringComparison.OrdinalIgnoreCase))
        {
            return workspaceResourceId; // Return full resource ID for Azure Monitor Query SDK
        }

        return null;
    }
}
