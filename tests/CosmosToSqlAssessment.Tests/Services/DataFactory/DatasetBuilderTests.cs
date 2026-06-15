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
        ds.Properties.TypeProperties["collectionName"].Should().Be("users");
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
        ds.Properties.TypeProperties["schema"].Should().Be("sales");
        ds.Properties.TypeProperties["table"].Should().Be("Customers");
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

        ds.Properties.TypeProperties["schema"].Should().Be("dbo");
    }
}
