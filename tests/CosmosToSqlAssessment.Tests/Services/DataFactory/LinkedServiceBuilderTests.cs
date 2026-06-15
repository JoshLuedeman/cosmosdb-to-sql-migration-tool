using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class LinkedServiceBuilderTests
{
    [Fact]
    public void BuildCosmosLinkedService_UsesCosmosDbType_WithDatabaseInName()
    {
        var registry = new AdfNameRegistry();
        var builder = new LinkedServiceBuilder();

        var ls = builder.BuildCosmosLinkedService("MyDb", registry);

        ls.Name.Should().Be("CosmosDb_MyDb_LinkedService");
        ls.Properties.Type.Should().Be("CosmosDb");
        ls.Properties.TypeProperties["connectionString"].Should().BeOfType<string>()
            .Which.Should().Contain("Database=MyDb");
    }

    [Fact]
    public void BuildAzureSqlLinkedService_UsesAzureSqlDatabaseType()
    {
        var registry = new AdfNameRegistry();
        var builder = new LinkedServiceBuilder();

        var ls = builder.BuildAzureSqlLinkedService(registry);

        ls.Name.Should().Be("AzureSqlDatabaseLinkedService");
        ls.Properties.Type.Should().Be("AzureSqlDatabase");
        ls.Properties.TypeProperties.Should().ContainKey("connectionString");
    }
}
