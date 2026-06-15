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
    public const string CosmosDatasetType = "CosmosDbSqlApiCollection";
    public const string AzureSqlDatasetType = "AzureSqlTable";

    public const string DatasetParamDatabaseName = "databaseName";
    public const string DatasetParamCollectionName = "collectionName";
    public const string DatasetParamSchema = "schema";
    public const string DatasetParamTable = "table";
    public const string DatasetParamSqlDatabaseName = "sqlDatabaseName";

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
