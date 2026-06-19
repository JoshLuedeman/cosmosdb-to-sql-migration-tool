using System.Text.Json;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Thin abstraction over Azure Resource Graph queries for testability.
/// </summary>
internal interface IResourceGraphQueryClient
{
    /// <summary>
    /// Executes a Resource Graph KQL query and returns the result rows as JSON elements.
    /// </summary>
    Task<IReadOnlyList<JsonElement>> QueryAsync(string kqlQuery, CancellationToken cancellationToken = default);
}
