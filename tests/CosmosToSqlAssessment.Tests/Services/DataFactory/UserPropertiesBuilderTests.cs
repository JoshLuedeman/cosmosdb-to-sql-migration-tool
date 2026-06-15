using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class UserPropertiesBuilderTests
{
    private static ContainerMapping SampleMapping() => new()
    {
        SourceContainer = "users",
        TargetSchema = "dbo",
        TargetTable = "Users",
    };

    [Fact]
    public void Build_EmitsAllExpectedProperties()
    {
        var props = new UserPropertiesBuilder().Build(SampleMapping());

        props.Select(p => p["name"]).Should().BeEquivalentTo(new[]
        {
            UserPropertiesBuilder.PropSourceDatabase,
            UserPropertiesBuilder.PropTargetDatabase,
            UserPropertiesBuilder.PropSourceContainer,
            UserPropertiesBuilder.PropTargetSchema,
            UserPropertiesBuilder.PropTargetTable,
            UserPropertiesBuilder.PropMigrationTool,
        }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Build_DatabaseValuesAreExpressionsReferencingPipelineParameters()
    {
        var props = new UserPropertiesBuilder().Build(SampleMapping());

        var source = (IDictionary<string, object?>)props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropSourceDatabase)["value"]!;
        source["type"].Should().Be("Expression");
        source["value"].Should().Be($"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}");

        var target = (IDictionary<string, object?>)props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropTargetDatabase)["value"]!;
        target["type"].Should().Be("Expression");
        target["value"].Should().Be($"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}");
    }

    [Fact]
    public void Build_PerMappingValuesAreLiterals()
    {
        var props = new UserPropertiesBuilder().Build(SampleMapping());

        props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropSourceContainer)["value"].Should().Be("users");
        props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropTargetSchema)["value"].Should().Be("dbo");
        props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropTargetTable)["value"].Should().Be("Users");
        props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropMigrationTool)["value"].Should().Be(UserPropertiesBuilder.MigrationToolMarker);
    }

    [Fact]
    public void Build_MissingTargetSchema_DefaultsToDbo()
    {
        var mapping = SampleMapping();
        mapping.TargetSchema = string.Empty;

        var props = new UserPropertiesBuilder().Build(mapping);

        props.Single(p => (string)p["name"]! == UserPropertiesBuilder.PropTargetSchema)["value"].Should().Be("dbo");
    }

    [Fact]
    public void Build_NullMapping_Throws()
    {
        var act = () => new UserPropertiesBuilder().Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
