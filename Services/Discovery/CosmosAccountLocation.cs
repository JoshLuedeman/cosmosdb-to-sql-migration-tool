namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Represents the location of a Cosmos DB account in Azure.
/// </summary>
internal sealed record CosmosAccountLocation(string SubscriptionId, string ResourceGroup, string AccountName);
