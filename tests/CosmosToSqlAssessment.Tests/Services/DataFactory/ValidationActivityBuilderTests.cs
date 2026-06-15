using System.Text.Json;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class ValidationActivityBuilderTests
{
    private static ContainerMapping SampleMapping(string container = "users", string schema = "dbo", string table = "Users") => new()
    {
        SourceContainer = container,
        TargetSchema = schema,
        TargetTable = table,
        EstimatedRowCount = 1234,
        FieldMappings =
        {
            new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" },
        },
    };

    private static (ValidationActivityBuilder.ValidationTriplet triplet, AdfNameRegistry registry) Build(
        ValidationOptions? options = null,
        ContainerMapping? mapping = null,
        string copyActivityName = "Copy_users_to_dbo_Users")
    {
        var registry = new AdfNameRegistry();
        var builder = new ValidationActivityBuilder();
        var triplet = builder.Build(
            mapping ?? SampleMapping(),
            sourceDatasetName: "Cosmos_users",
            sinkDatasetName: "AzureSql_dbo_Users",
            copyActivityName: copyActivityName,
            registry,
            options ?? new ValidationOptions());
        return (triplet, registry);
    }

    // ---- Shape ----

    [Fact]
    public void Build_ProducesThreeActivities_WithExpectedTypes()
    {
        var (t, _) = Build();

        t.LookupSource.Type.Should().Be("Lookup");
        t.LookupTarget.Type.Should().Be("Lookup");
        t.IfCondition.Type.Should().Be("IfCondition");
    }

    [Fact]
    public void Build_LookupSourceQueriesCosmosWithAliasedCount()
    {
        var (t, _) = Build();

        var source = (Dictionary<string, object?>)t.LookupSource.TypeProperties["source"]!;
        source["type"].Should().Be("CosmosDbSqlApiSource");
        // CRITICAL: aliased projection, NOT `SELECT VALUE COUNT(1)` — caller relies
        // on `firstRow.docCount` being present (rubber-duck blocker on #145).
        source["query"].Should().Be("SELECT COUNT(1) AS docCount FROM c");

        t.LookupSource.TypeProperties["firstRowOnly"].Should().Be(true);
    }

    [Fact]
    public void Build_LookupSourceForwardsCosmosDatabaseAndCollectionParametersToDataset()
    {
        var (t, _) = Build();

        var dataset = (Dictionary<string, object?>)t.LookupSource.TypeProperties["dataset"]!;
        dataset["referenceName"].Should().Be("Cosmos_users");
        dataset["type"].Should().Be("DatasetReference");

        var parameters = (Dictionary<string, object?>)dataset["parameters"]!;
        parameters[DatasetBuilder.DatasetParamDatabaseName].Should().Be(
            $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}");
        parameters[DatasetBuilder.DatasetParamCollectionName].Should().Be("users");
    }

    [Fact]
    public void Build_LookupTargetUsesCountBigOnBracketEscapedSchemaTable()
    {
        var (t, _) = Build();

        var source = (Dictionary<string, object?>)t.LookupTarget.TypeProperties["source"]!;
        source["type"].Should().Be("AzureSqlSource");
        source["sqlReaderQuery"].Should().Be("SELECT COUNT_BIG(1) AS docCount FROM [dbo].[Users]");
    }

    [Fact]
    public void Build_LookupTargetEscapesBracketsInTableName()
    {
        var mapping = SampleMapping(table: "Tbl]Name");
        var (t, _) = Build(mapping: mapping);

        var source = (Dictionary<string, object?>)t.LookupTarget.TypeProperties["source"]!;
        // ] doubled and identifier still wrapped → "[Tbl]]Name]"
        ((string)source["sqlReaderQuery"]!).Should().Contain("[Tbl]]Name]");
    }

    [Fact]
    public void Build_LookupTargetDependsOnCopySucceeded()
    {
        var (t, _) = Build();

        var dependsOn = t.LookupTarget.AdditionalProperties!["dependsOn"];
        var json = JsonSerializer.Serialize(dependsOn);
        json.Should().Contain("\"activity\":\"Copy_users_to_dbo_Users\"");
        json.Should().Contain("\"Succeeded\"");
    }

    // ---- IfCondition + nested Fail ----

    [Fact]
    public void Build_IfConditionExpressionIsExpressionObject_NotBareString()
    {
        var (t, _) = Build();
        var expr = (Dictionary<string, object?>)t.IfCondition.TypeProperties["expression"]!;
        expr["type"].Should().Be("Expression");
        ((string)expr["value"]!).Should().StartWith("@");
    }

    [Fact]
    public void Build_IfConditionDependsOnLookupTargetSucceeded()
    {
        var (t, _) = Build();
        var dependsOn = t.IfCondition.AdditionalProperties!["dependsOn"];
        var json = JsonSerializer.Serialize(dependsOn);
        json.Should().Contain($"\"activity\":\"{t.LookupTargetName}\"");
        json.Should().Contain("\"Succeeded\"");
    }

    [Fact]
    public void Build_NestedFailActivity_IsInIfFalseActivities()
    {
        var (t, _) = Build();

        var falseActivities = (List<object?>)t.IfCondition.TypeProperties["ifFalseActivities"]!;
        falseActivities.Should().HaveCount(1);
        var fail = (PipelineActivity)falseActivities[0]!;
        fail.Type.Should().Be("Fail");
        fail.TypeProperties.Should().ContainKey("message");
        fail.TypeProperties.Should().ContainKey("errorCode");
        fail.TypeProperties["errorCode"].Should().Be(ValidationActivityBuilder.ValidationErrorCode);

        var message = (Dictionary<string, object?>)fail.TypeProperties["message"]!;
        message["type"].Should().Be("Expression");
        // Expression must use @concat — bare string interpolation would not be evaluated by ADF.
        ((string)message["value"]!).Should().StartWith("@concat(");
    }

    [Fact]
    public void Build_FailMessage_DoublesEmbeddedSingleQuotes()
    {
        var mapping = SampleMapping(container: "user's");
        var (t, _) = Build(mapping: mapping);

        var falseActivities = (List<object?>)t.IfCondition.TypeProperties["ifFalseActivities"]!;
        var fail = (PipelineActivity)falseActivities[0]!;
        var message = (Dictionary<string, object?>)fail.TypeProperties["message"]!;
        // 'user''s' (ADF doubles single-quotes for escaping inside string literals).
        ((string)message["value"]!).Should().Contain("user''s");
    }

    // ---- Strategy / Tolerance expression matrix ----

    [Fact]
    public void BuildValidationExpression_Exact_ZeroTolerance_UsesEquals()
    {
        var expr = ValidationActivityBuilder.BuildValidationExpression(
            "LookupSrc_users", "LookupTgt_dbo_Users",
            new ValidationOptions { Strategy = ValidationStrategy.RowCountExact, Tolerance = 0 });

        expr.Should().Be("@equals(int(activity('LookupSrc_users').output.firstRow.docCount), int(activity('LookupTgt_dbo_Users').output.firstRow.docCount))");
    }

    [Fact]
    public void BuildValidationExpression_Exact_PositiveTolerance_UsesAbsSub()
    {
        var expr = ValidationActivityBuilder.BuildValidationExpression(
            "LookupSrc_users", "LookupTgt_dbo_Users",
            new ValidationOptions { Strategy = ValidationStrategy.RowCountExact, Tolerance = 5 });

        // |src - tgt| <= 5
        expr.Should().StartWith("@lessOrEquals(int(abs(sub(");
        expr.Should().Contain(", 5)");
    }

    [Fact]
    public void BuildValidationExpression_AtLeast_UsesGreaterOrEqualsWithShortfall()
    {
        var expr = ValidationActivityBuilder.BuildValidationExpression(
            "LookupSrc_users", "LookupTgt_dbo_Users",
            new ValidationOptions { Strategy = ValidationStrategy.RowCountAtLeast, Tolerance = 100 });

        // tgt >= src - 100
        expr.Should().StartWith("@greaterOrEquals(int(activity('LookupTgt_dbo_Users')");
        expr.Should().Contain("sub(int(activity('LookupSrc_users').output.firstRow.docCount), 100)");
    }

    // ---- Misc ----

    [Fact]
    public void Build_AllocatesUniqueActivityNames_AndExposesThem()
    {
        var (t, _) = Build();

        var names = new[] { t.LookupSourceName, t.LookupTargetName, t.IfConditionName };
        names.Should().OnlyHaveUniqueItems();
        names.Should().NotContainNulls();
        names.Should().AllSatisfy(n => n.Length.Should().BeLessThanOrEqualTo(AdfNameRegistry.MaxNameLength));
    }

    [Fact]
    public void EscapeAdfStringLiteral_DoublesSingleQuotes()
    {
        ValidationActivityBuilder.EscapeAdfStringLiteral("plain").Should().Be("plain");
        ValidationActivityBuilder.EscapeAdfStringLiteral("user's order").Should().Be("user''s order");
        ValidationActivityBuilder.EscapeAdfStringLiteral("a'b'c").Should().Be("a''b''c");
    }
}
