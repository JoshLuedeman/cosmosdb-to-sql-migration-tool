using System.Text.Json;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Concrete implementation of <see cref="IResourceGraphQueryClient"/> that uses
/// the Azure Resource Manager SDK to execute Resource Graph queries.
/// </summary>
internal sealed class ArmResourceGraphQueryClient : IResourceGraphQueryClient
{
    private readonly ArmClient _armClient;

    public ArmResourceGraphQueryClient(ArmClient armClient)
    {
        _armClient = armClient ?? throw new ArgumentNullException(nameof(armClient));
    }

    public async Task<IReadOnlyList<JsonElement>> QueryAsync(string kqlQuery, CancellationToken cancellationToken = default)
    {
        var tenant = _armClient.GetTenants().First();

        var queryContent = new ResourceQueryContent(kqlQuery);

        var response = await tenant.GetResourcesAsync(queryContent, cancellationToken);
        var result = response.Value;

        // Result.Data is BinaryData; parse it as JSON
        var dataString = result.Data.ToString();
        if (string.IsNullOrEmpty(dataString))
            return Array.Empty<JsonElement>();

        using var doc = JsonDocument.Parse(dataString);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var rows = new List<JsonElement>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                rows.Add(element.Clone());
            }
            return rows;
        }

        return Array.Empty<JsonElement>();
    }
}
