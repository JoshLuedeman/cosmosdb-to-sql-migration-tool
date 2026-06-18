using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Monitor;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Concrete implementation of <see cref="IDiagnosticSettingsClient"/> using the ARM SDK.
/// </summary>
internal sealed class ArmDiagnosticSettingsClient : IDiagnosticSettingsClient
{
    private readonly ArmClient _armClient;

    public ArmDiagnosticSettingsClient(ArmClient armClient)
    {
        _armClient = armClient ?? throw new ArgumentNullException(nameof(armClient));
    }

    public async Task<IReadOnlyList<DiagnosticSettingInfo>> GetDiagnosticSettingsAsync(
        ResourceIdentifier resourceId, CancellationToken cancellationToken = default)
    {
        var collection = _armClient.GetDiagnosticSettings(resourceId);
        var results = new List<DiagnosticSettingInfo>();

        await foreach (var setting in collection.GetAllAsync(cancellationToken))
        {
            results.Add(new DiagnosticSettingInfo(
                setting.Data.Name,
                setting.Data.WorkspaceId?.ToString()));
        }

        return results;
    }
}
