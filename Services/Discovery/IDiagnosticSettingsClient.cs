using Azure.Core;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Represents a diagnostic setting's Log Analytics workspace link.
/// </summary>
internal sealed record DiagnosticSettingInfo(string Name, string? WorkspaceResourceId);

/// <summary>
/// Thin abstraction over Azure diagnostic settings for testability.
/// </summary>
internal interface IDiagnosticSettingsClient
{
    /// <summary>
    /// Lists all diagnostic settings for the given resource.
    /// </summary>
    Task<IReadOnlyList<DiagnosticSettingInfo>> GetDiagnosticSettingsAsync(
        ResourceIdentifier resourceId, CancellationToken cancellationToken = default);
}
