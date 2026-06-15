namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Single source of truth for every ADF parameter name used by the generated artifacts.
/// Centralising the names means linked-service builders, dataset builders, copy-activity
/// builders, the orchestrator's <c>adf-parameters.template.json</c>, and the unit tests
/// all reference identical strings — typos cause test failures, not silent runtime errors.
/// </summary>
public static class ParameterCatalog
{
    // Linked-service-level parameters
    public const string CosmosAccountEndpoint = "cosmosAccountEndpoint";
    public const string CosmosDatabaseName = "cosmosDatabaseName";
    public const string CosmosAccountKeySecretName = "cosmosAccountKeySecretName";

    public const string SqlServerName = "sqlServerName";
    public const string SqlDatabaseName = "sqlDatabaseName";
    public const string SqlPasswordSecretName = "sqlPasswordSecretName";
    public const string SqlUserName = "sqlUserName";

    public const string KeyVaultBaseUrl = "keyVaultBaseUrl";

    // Pipeline-level parameter names (shared between per-db pipelines and the master).
    // The catalog deliberately exposes only "logical" names; chunked / multi-db scenarios
    // suffix the master-level parameter with a sanitised database name to keep each
    // child invocation independent.
    public const string PipelineParamCosmosDatabaseName = "cosmosDatabaseName";
    public const string PipelineParamSqlDatabaseName = "sqlDatabaseName";

    public const string ParameterTypeString = "string";

    public sealed record ParameterDefinition(string Name, string Type, string Description, object? DefaultValue = null);

    /// <summary>
    /// Linked-service parameters published on every parameterised Cosmos linked service.
    /// </summary>
    public static IReadOnlyList<ParameterDefinition> CosmosLinkedServiceParameters(bool useKeyVault)
    {
        var list = new List<ParameterDefinition>
        {
            new(CosmosAccountEndpoint, ParameterTypeString, "Cosmos DB account endpoint, e.g. https://<account>.documents.azure.com:443/."),
            new(CosmosDatabaseName, ParameterTypeString, "Cosmos DB database name."),
        };
        if (useKeyVault)
        {
            list.Add(new(CosmosAccountKeySecretName, ParameterTypeString, "Name of the Key Vault secret holding the Cosmos DB account key."));
        }
        return list;
    }

    /// <summary>
    /// Linked-service parameters published on every parameterised Azure SQL linked service.
    /// </summary>
    public static IReadOnlyList<ParameterDefinition> SqlLinkedServiceParameters(bool useKeyVault)
    {
        var list = new List<ParameterDefinition>
        {
            new(SqlServerName, ParameterTypeString, "Azure SQL logical server name (without .database.windows.net suffix)."),
            new(SqlDatabaseName, ParameterTypeString, "Azure SQL database name."),
        };
        if (useKeyVault)
        {
            list.Add(new(SqlUserName, ParameterTypeString, "SQL authentication user name."));
            list.Add(new(SqlPasswordSecretName, ParameterTypeString, "Name of the Key Vault secret holding the SQL password."));
        }
        return list;
    }

    /// <summary>
    /// Parameters always present on the Key Vault linked service (when emitted).
    /// </summary>
    public static IReadOnlyList<ParameterDefinition> KeyVaultLinkedServiceParameters() => new[]
    {
        new ParameterDefinition(KeyVaultBaseUrl, ParameterTypeString, "Azure Key Vault base URL, e.g. https://<vault>.vault.azure.net/."),
    };
}
