using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds ADF source / sink datasets for every Cosmos container and every target SQL table.
/// Datasets declare parameters (e.g. <c>databaseName</c>, <c>collectionName</c>) and
/// forward them into the linked-service reference and <c>typeProperties</c> so the same
/// dataset can be reused across environments by passing parameter values from the
/// invoking pipeline (#142).
/// </summary>
public sealed class DatasetBuilder
{
    /// <summary>ADF dataset type discriminator for Cosmos DB SQL API container datasets.</summary>
    public const string CosmosDatasetType = "CosmosDbSqlApiCollection";
    /// <summary>ADF dataset type discriminator for Azure SQL table datasets.</summary>
    public const string AzureSqlDatasetType = "AzureSqlTable";

    /// <summary>Dataset parameter name forwarded into the Cosmos linked-service <c>database</c> property at pipeline invocation time.</summary>
    public const string DatasetParamDatabaseName = "databaseName";
    /// <summary>Dataset parameter name for the Cosmos source container (collection) name, forwarded into <c>typeProperties.collectionName</c>.</summary>
    public const string DatasetParamCollectionName = "collectionName";
    /// <summary>Dataset parameter name for the Azure SQL target schema, forwarded into <c>typeProperties.schema</c>.</summary>
    public const string DatasetParamSchema = "schema";
    /// <summary>Dataset parameter name for the Azure SQL target table name, forwarded into <c>typeProperties.table</c>.</summary>
    public const string DatasetParamTable = "table";
    /// <summary>Dataset parameter name for the Azure SQL database name, forwarded into the SQL linked-service reference at pipeline invocation time.</summary>
    public const string DatasetParamSqlDatabaseName = "sqlDatabaseName";

    /// <summary>
    /// Builds a parameterised <c>CosmosDbSqlApiCollection</c> dataset for <paramref name="mapping"/>'s
    /// source container. The <c>databaseName</c> and <c>collectionName</c> parameters are forwarded
    /// from the invoking pipeline so the same artifact serves all environments.
    /// </summary>
    /// <param name="databaseName">Name of the source Cosmos DB database; used in the dataset name and as default parameter value.</param>
    /// <param name="mapping">Container-to-table mapping that identifies the source container.</param>
    /// <param name="cosmosLinkedServiceName">Logical name of the Cosmos linked service to reference.</param>
    /// <param name="registry">Name registry used to allocate a collision-free artifact name.</param>
    /// <returns>A fully constructed <see cref="DatasetResource"/> ready for serialization.</returns>
    public DatasetResource BuildCosmosCollectionDataset(
        string databaseName,
        ContainerMapping mapping,
        string cosmosLinkedServiceName,
        AdfNameRegistry registry)
    {
        var desiredName = $"Cosmos_{databaseName}_{mapping.SourceContainer}";
        var allocated = registry.Allocate(desiredName, $"dataset|cosmos|{databaseName}|{mapping.SourceContainer}");

        var dataset = new DatasetResource
        {
            Name = allocated,
            Properties = new DatasetProperties
            {
                Type = CosmosDatasetType,
                LinkedServiceName = new ResourceReference
                {
                    ReferenceName = cosmosLinkedServiceName,
                    Type = "LinkedServiceReference",
                    Parameters = new Dictionary<string, object?>
                    {
                        [ParameterCatalog.CosmosDatabaseName] = $"@dataset().{DatasetParamDatabaseName}",
                    },
                },
                TypeProperties =
                {
                    ["collectionName"] = $"@dataset().{DatasetParamCollectionName}",
                },
                Annotations = new List<string>
                {
                    $"Source container '{mapping.SourceContainer}' in database '{databaseName}'.",
                },
            },
        };

        dataset.Properties.AdditionalProperties = new Dictionary<string, object?>
        {
            ["parameters"] = new Dictionary<string, object?>
            {
                [DatasetParamDatabaseName] = new Dictionary<string, object?> { ["type"] = "string" },
                [DatasetParamCollectionName] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        };
        return dataset;
    }

    /// <summary>
    /// Builds a parameterised <c>AzureSqlTable</c> dataset for <paramref name="mapping"/>'s
    /// target table. The <c>schema</c>, <c>table</c>, and <c>sqlDatabaseName</c> parameters
    /// are forwarded from the invoking pipeline so the same artifact serves all environments.
    /// </summary>
    /// <param name="mapping">Container-to-table mapping that identifies the target schema and table.</param>
    /// <param name="azureSqlLinkedServiceName">Logical name of the Azure SQL linked service to reference.</param>
    /// <param name="registry">Name registry used to allocate a collision-free artifact name.</param>
    /// <returns>A fully constructed <see cref="DatasetResource"/> ready for serialization.</returns>
    public DatasetResource BuildAzureSqlTableDataset(
        ContainerMapping mapping,
        string azureSqlLinkedServiceName,
        AdfNameRegistry registry)
    {
        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;
        var desiredName = $"AzureSql_{schema}_{mapping.TargetTable}";
        var allocated = registry.Allocate(desiredName, $"dataset|azureSql|{schema}|{mapping.TargetTable}");

        var dataset = new DatasetResource
        {
            Name = allocated,
            Properties = new DatasetProperties
            {
                Type = AzureSqlDatasetType,
                LinkedServiceName = new ResourceReference
                {
                    ReferenceName = azureSqlLinkedServiceName,
                    Type = "LinkedServiceReference",
                    Parameters = new Dictionary<string, object?>
                    {
                        [ParameterCatalog.SqlDatabaseName] = $"@dataset().{DatasetParamSqlDatabaseName}",
                    },
                },
                TypeProperties =
                {
                    ["schema"] = $"@dataset().{DatasetParamSchema}",
                    ["table"] = $"@dataset().{DatasetParamTable}",
                },
                Annotations = new List<string>
                {
                    $"Target table '{schema}.{mapping.TargetTable}'.",
                },
            },
        };

        dataset.Properties.AdditionalProperties = new Dictionary<string, object?>
        {
            ["parameters"] = new Dictionary<string, object?>
            {
                [DatasetParamSqlDatabaseName] = new Dictionary<string, object?> { ["type"] = "string" },
                [DatasetParamSchema] = new Dictionary<string, object?> { ["type"] = "string" },
                [DatasetParamTable] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        };
        return dataset;
    }

    /// <summary>
    /// Builds a dedicated dataset for the #147 watermark table. Schema and table
    /// names are baked as literals from <see cref="IncrementalCopyOptions"/>; only
    /// <see cref="DatasetParamSqlDatabaseName"/> is parameterised so the same dataset
    /// drives every incremental Lookup / Script regardless of which target database
    /// the surrounding pipeline is migrating into.
    /// </summary>
    public DatasetResource BuildWatermarkDataset(
        IncrementalCopyOptions options,
        string azureSqlLinkedServiceName,
        AdfNameRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(azureSqlLinkedServiceName);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkSchemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkTableName);

        var desiredName = $"AzureSql_AdfWatermark";
        var allocated = registry.Allocate(desiredName, "dataset|azureSql|adfWatermark|shared");

        var dataset = new DatasetResource
        {
            Name = allocated,
            Properties = new DatasetProperties
            {
                Type = AzureSqlDatasetType,
                LinkedServiceName = new ResourceReference
                {
                    ReferenceName = azureSqlLinkedServiceName,
                    Type = "LinkedServiceReference",
                    Parameters = new Dictionary<string, object?>
                    {
                        [ParameterCatalog.SqlDatabaseName] = $"@dataset().{DatasetParamSqlDatabaseName}",
                    },
                },
                TypeProperties =
                {
                    // Literal schema + table — every incremental Lookup overrides the
                    // table reference via sqlReaderQuery anyway, but ADF still validates
                    // the dataset's required typeProperties at deployment time.
                    ["schema"] = options.WatermarkSchemaName,
                    ["table"] = options.WatermarkTableName,
                },
                Annotations = new List<string>
                {
                    $"Shared watermark dataset for incremental migration (#147).",
                },
            },
        };

        dataset.Properties.AdditionalProperties = new Dictionary<string, object?>
        {
            ["parameters"] = new Dictionary<string, object?>
            {
                [DatasetParamSqlDatabaseName] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        };
        return dataset;
    }
}
