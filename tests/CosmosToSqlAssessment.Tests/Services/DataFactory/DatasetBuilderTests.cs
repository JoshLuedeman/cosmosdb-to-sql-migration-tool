using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class DatasetBuilderTests
{
    [Fact]
    public void BuildCosmosCollectionDataset_UsesCosmosDbSqlApiCollectionType_AndLinkedServiceReference()
    {
        var registry = new AdfNameRegistry();
        var builder = new DatasetBuilder();
        var mapping = new ContainerMapping { SourceContainer = "users", TargetSchema = "dbo", TargetTable = "Users" };

        var ds = builder.BuildCosmosCollectionDataset("MyDb", mapping, "CosmosDb_MyDb_LinkedService", registry);

        ds.Name.Should().Be("Cosmos_MyDb_users");
        ds.Properties.Type.Should().Be("CosmosDbSqlApiCollection");
        ds.Properties.LinkedServiceName.ReferenceName.Should().Be("CosmosDb_MyDb_LinkedService");
        ds.Properties.LinkedServiceName.Type.Should().Be("LinkedServiceReference");
        // collectionName must flow from the dataset-level parameter so the dataset is reusable.
        ds.Properties.TypeProperties["collectionName"].Should().Be($"@dataset().{DatasetBuilder.DatasetParamCollectionName}");
        // Linked-service ref forwards the per-db Cosmos database name via @dataset().
        ds.Properties.LinkedServiceName.Parameters.Should().NotBeNull();
        ds.Properties.LinkedServiceName.Parameters![ParameterCatalog.CosmosDatabaseName]
            .Should().Be($"@dataset().{DatasetBuilder.DatasetParamDatabaseName}");
        // Parameters block on dataset itself.
        var parameters = (IDictionary<string, object?>)ds.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(DatasetBuilder.DatasetParamDatabaseName);
        parameters.Should().ContainKey(DatasetBuilder.DatasetParamCollectionName);
    }

    [Fact]
    public void BuildAzureSqlTableDataset_UsesAzureSqlTableType_AndCarriesSchemaAndTable()
    {
        var registry = new AdfNameRegistry();
        var builder = new DatasetBuilder();
        var mapping = new ContainerMapping { SourceContainer = "users", TargetSchema = "sales", TargetTable = "Customers" };

        var ds = builder.BuildAzureSqlTableDataset(mapping, "AzureSqlDatabaseLinkedService", registry);

        ds.Name.Should().Be("AzureSql_sales_Customers");
        ds.Properties.Type.Should().Be("AzureSqlTable");
        ds.Properties.TypeProperties["schema"].Should().Be($"@dataset().{DatasetBuilder.DatasetParamSchema}");
        ds.Properties.TypeProperties["table"].Should().Be($"@dataset().{DatasetBuilder.DatasetParamTable}");
        ds.Properties.LinkedServiceName.Parameters![ParameterCatalog.SqlDatabaseName]
            .Should().Be($"@dataset().{DatasetBuilder.DatasetParamSqlDatabaseName}");
        var parameters = (IDictionary<string, object?>)ds.Properties.AdditionalProperties!["parameters"]!;
        parameters.Should().ContainKey(DatasetBuilder.DatasetParamSqlDatabaseName);
        parameters.Should().ContainKey(DatasetBuilder.DatasetParamSchema);
        parameters.Should().ContainKey(DatasetBuilder.DatasetParamTable);
    }

    [Fact]
    public void BuildCosmosCollectionDataset_IncludesDatabaseInName_AvoidsCollisionAcrossDatabases()
    {
        var registry = new AdfNameRegistry();
        var builder = new DatasetBuilder();
        var mapping = new ContainerMapping { SourceContainer = "users", TargetTable = "Users" };

        var dsA = builder.BuildCosmosCollectionDataset("dbA", mapping, "CosmosDb_dbA_LinkedService", registry);
        var dsB = builder.BuildCosmosCollectionDataset("dbB", mapping, "CosmosDb_dbB_LinkedService", registry);

        dsA.Name.Should().NotBe(dsB.Name);
    }

    [Fact]
    public void BuildAzureSqlTableDataset_BlankSchema_DefaultsToDbo()
    {
        var registry = new AdfNameRegistry();
        var builder = new DatasetBuilder();
        var mapping = new ContainerMapping { SourceContainer = "users", TargetSchema = "", TargetTable = "Users" };

        var ds = builder.BuildAzureSqlTableDataset(mapping, "AzureSqlDatabaseLinkedService", registry);

        // With parameterisation in place the literal schema value is supplied per-mapping
        // by the copy activity ref; the dataset itself flows the value through @dataset().
        ds.Properties.TypeProperties["schema"].Should().Be($"@dataset().{DatasetBuilder.DatasetParamSchema}");
    }
}
