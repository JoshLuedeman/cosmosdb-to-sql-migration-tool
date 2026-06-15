using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds ADF source / sink datasets for every Cosmos container and every target SQL table.
/// Dataset names include the source database so distinct Cosmos databases with identically
/// named containers do not collide.
/// </summary>
public sealed class DatasetBuilder
{
    public const string CosmosDatasetType = "CosmosDbSqlApiCollection";
    public const string AzureSqlDatasetType = "AzureSqlTable";

    public DatasetResource BuildCosmosCollectionDataset(
        string databaseName,
        ContainerMapping mapping,
        string cosmosLinkedServiceName,
        AdfNameRegistry registry)
    {
        var desiredName = $"Cosmos_{databaseName}_{mapping.SourceContainer}";
        var allocated = registry.Allocate(desiredName, $"dataset|cosmos|{databaseName}|{mapping.SourceContainer}");

        return new DatasetResource
        {
            Name = allocated,
            Properties = new DatasetProperties
            {
                Type = CosmosDatasetType,
                LinkedServiceName = new ResourceReference
                {
                    ReferenceName = cosmosLinkedServiceName,
                    Type = "LinkedServiceReference",
                },
                TypeProperties =
                {
                    ["collectionName"] = mapping.SourceContainer,
                },
                Annotations = new List<string>
                {
                    $"Source container '{mapping.SourceContainer}' in database '{databaseName}'.",
                },
            },
        };
    }

    public DatasetResource BuildAzureSqlTableDataset(
        ContainerMapping mapping,
        string azureSqlLinkedServiceName,
        AdfNameRegistry registry)
    {
        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;
        var desiredName = $"AzureSql_{schema}_{mapping.TargetTable}";
        var allocated = registry.Allocate(desiredName, $"dataset|azureSql|{schema}|{mapping.TargetTable}");

        return new DatasetResource
        {
            Name = allocated,
            Properties = new DatasetProperties
            {
                Type = AzureSqlDatasetType,
                LinkedServiceName = new ResourceReference
                {
                    ReferenceName = azureSqlLinkedServiceName,
                    Type = "LinkedServiceReference",
                },
                TypeProperties =
                {
                    ["schema"] = schema,
                    ["table"] = mapping.TargetTable,
                },
                Annotations = new List<string>
                {
                    $"Target table '{schema}.{mapping.TargetTable}'.",
                },
            },
        };
    }
}
