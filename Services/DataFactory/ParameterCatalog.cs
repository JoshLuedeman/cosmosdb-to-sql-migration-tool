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
    /// <summary>ADF parameter name forwarded into the Cosmos DB linked-service <c>accountEndpoint</c> (or connection string) property.</summary>
    public const string CosmosAccountEndpoint = "cosmosAccountEndpoint";
    /// <summary>ADF parameter name for the source Cosmos DB database name, forwarded into the linked-service <c>database</c> property.</summary>
    public const string CosmosDatabaseName = "cosmosDatabaseName";
    /// <summary>ADF parameter name for the Azure Key Vault secret that holds the Cosmos DB account key; emitted only when Key Vault auth is active and managed identity is off.</summary>
    public const string CosmosAccountKeySecretName = "cosmosAccountKeySecretName";

    /// <summary>ADF parameter name for the Azure SQL logical server name (without the <c>.database.windows.net</c> suffix), forwarded into the SQL linked service.</summary>
    public const string SqlServerName = "sqlServerName";
    /// <summary>ADF parameter name for the Azure SQL target database name, forwarded into the SQL linked service.</summary>
    public const string SqlDatabaseName = "sqlDatabaseName";
    /// <summary>ADF parameter name for the Key Vault secret that holds the SQL password; emitted only when key-based SQL auth is configured.</summary>
    public const string SqlPasswordSecretName = "sqlPasswordSecretName";
    /// <summary>ADF parameter name for the SQL authentication user name; emitted only when key-based SQL auth is configured.</summary>
    public const string SqlUserName = "sqlUserName";

    /// <summary>ADF parameter name for the Azure Key Vault base URL (e.g. <c>https://&lt;vault&gt;.vault.azure.net/</c>), forwarded into the Key Vault linked service.</summary>
    public const string KeyVaultBaseUrl = "keyVaultBaseUrl";

    // Pipeline-level parameters (#143).
    /// <summary>Pipeline-level parameter name for the webhook URL posted to by failure-notification <c>Web</c> activities; forwarded from master to every per-database pipeline.</summary>
    public const string PipelineParamFailureNotificationWebhookUrl = "failureNotificationWebhookUrl";
    /// <summary>Pipeline-level parameter name for the blob storage path prefix where the ADF Copy fault-tolerance log writes skipped-row details.</summary>
    public const string PipelineParamFaultToleranceLogPath = "faultToleranceLogPath";

    // Deployment-template-level parameters for monitoring (#144). These live in
    // the diagnostic-settings ARM template, not in pipeline JSON, so they get a
    // distinct prefix to avoid being mistaken for pipeline parameters.
    /// <summary>ARM-template parameter name for the Data Factory resource name in the diagnostic-settings template; must name an existing factory in the deployment resource group.</summary>
    public const string MonitoringParamDataFactoryName = "dataFactoryName";
    /// <summary>ARM-template parameter name for the full Log Analytics workspace resource ID to which the diagnostic setting routes ADF logs.</summary>
    public const string MonitoringParamLogAnalyticsWorkspaceId = "logAnalyticsWorkspaceId";
    /// <summary>ARM-template parameter name for the diagnostic setting name attached to the factory; must be unique per factory.</summary>
    public const string MonitoringParamDiagnosticSettingName = "diagnosticSettingName";

    // Pipeline-level parameter names (shared between per-db pipelines and the master).
    // The catalog deliberately exposes only "logical" names; chunked / multi-db scenarios
    // suffix the master-level parameter with a sanitised database name to keep each
    // child invocation independent.
    /// <summary>Pipeline-level parameter name for the source Cosmos DB database name; shared by per-database pipelines and the master orchestrator.</summary>
    public const string PipelineParamCosmosDatabaseName = "cosmosDatabaseName";
    /// <summary>Pipeline-level parameter name for the target Azure SQL database name; shared by per-database pipelines and the master orchestrator.</summary>
    public const string PipelineParamSqlDatabaseName = "sqlDatabaseName";

    /// <summary>Literal ADF type token used in every generated <c>{ "type": "string" }</c> parameter or variable declaration.</summary>
    public const string ParameterTypeString = "string";

    /// <summary>
    /// Typed descriptor for a single ADF parameter: the <paramref name="Name"/> surfaced
    /// in generated artifact JSON, the ADF <paramref name="Type"/> token (e.g. <c>"string"</c>),
    /// a human-readable <paramref name="Description"/>, and an optional <paramref name="DefaultValue"/>
    /// baked into the parameter block at generation time.
    /// </summary>
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
