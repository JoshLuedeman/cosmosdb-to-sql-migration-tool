using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class CopyActivityBuilderTests
{
    private static ContainerMapping SampleMapping()
    {
        return new ContainerMapping
        {
            SourceContainer = "users",
            TargetSchema = "dbo",
            TargetTable = "Users",
            FieldMappings = new List<FieldMapping>
            {
                new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" },
                new() { SourceField = "email", SourceType = "string", TargetColumn = "Email", TargetType = "NVARCHAR(255)" },
                new() { SourceField = "address.city", SourceType = "string", TargetColumn = "City", TargetType = "NVARCHAR(100)" },
                new() { SourceField = "computed", SourceType = "string", TargetColumn = "Computed", TargetType = "NVARCHAR(50)", RequiresTransformation = true },
            },
        };
    }

    [Fact]
    public void Build_NamesActivity_FromContainerAndTable()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "Cosmos_users", "AzureSql_dbo_Users", SinkWriteBehavior.Insert, registry);

        result.Activity.Name.Should().StartWith("Copy_users_to_dbo_Users");
        result.Activity.Type.Should().Be("Copy");
        result.Activity.Inputs.Should().ContainSingle().Which.ReferenceName.Should().Be("Cosmos_users");
        result.Activity.Outputs.Should().ContainSingle().Which.ReferenceName.Should().Be("AzureSql_dbo_Users");
    }

    [Fact]
    public void Build_SourceAndSinkUseCorrectAdfTypes()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry);

        var source = (Dictionary<string, object?>)result.Activity.TypeProperties["source"]!;
        var sink = (Dictionary<string, object?>)result.Activity.TypeProperties["sink"]!;

        source["type"].Should().Be("CosmosDbSqlApiSource");
        sink["type"].Should().Be("AzureSqlSink");
        sink["writeBehavior"].Should().Be("insert");
    }

    [Fact]
    public void Build_TranslatorEmitsJsonPathSourceMappings_SkippingTransformed()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry);

        var translator = (Dictionary<string, object?>)result.Activity.TypeProperties["translator"]!;
        translator["type"].Should().Be("TabularTranslator");

        var mappings = (List<Dictionary<string, object?>>)translator["mappings"]!;
        // 3 non-transformed fields out of 4
        mappings.Should().HaveCount(3);

        var firstSource = (Dictionary<string, object?>)mappings[0]["source"]!;
        firstSource["path"].Should().Be("$['id']");

        var nestedSource = (Dictionary<string, object?>)mappings[2]["source"]!;
        nestedSource["path"].Should().Be("$['address']['city']");
    }

    [Fact]
    public void Build_TransformedField_AddsWarningAndAnnotation()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry);

        result.Warnings.Should().ContainSingle(w => w.Contains("computed"));
        result.Activity.Annotations.Should().Contain(a => a.Contains("TODO") && a.Contains("computed"));
    }

    [Fact]
    public void Build_ChildTableMappings_AddWarning()
    {
        var mapping = SampleMapping();
        mapping.ChildTableMappings.Add(new ChildTableMapping
        {
            SourceFieldPath = "addresses",
            ChildTableType = "Array",
            TargetTable = "UserAddresses",
        });

        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(mapping, "src", "sink", SinkWriteBehavior.Insert, registry);

        result.Warnings.Should().Contain(w => w.Contains("child-table"));
    }

    [Fact]
    public void Build_UpsertWriteBehavior_LowercasedOnSink()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Upsert, registry);

        var sink = (Dictionary<string, object?>)result.Activity.TypeProperties["sink"]!;
        sink["writeBehavior"].Should().Be("upsert");
    }

    [Fact]
    public void Build_InputAndOutputDatasetRefs_CarryParametersForEnvParameterisation()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "Cosmos_users", "AzureSql_dbo_Users", SinkWriteBehavior.Insert, registry);

        var input = result.Activity.Inputs.Single();
        input.Parameters.Should().NotBeNull();
        input.Parameters![DatasetBuilder.DatasetParamCollectionName].Should().Be("users");
        input.Parameters[DatasetBuilder.DatasetParamDatabaseName].Should().Be(
            $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}");

        var output = result.Activity.Outputs.Single();
        output.Parameters.Should().NotBeNull();
        output.Parameters![DatasetBuilder.DatasetParamSchema].Should().Be("dbo");
        output.Parameters[DatasetBuilder.DatasetParamTable].Should().Be("Users");
        output.Parameters[DatasetBuilder.DatasetParamSqlDatabaseName].Should().Be(
            $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}");
    }

    [Fact]
    public void Build_DefaultOptions_AttachesPolicyBlockWithInsertSafeRetry()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry);

        result.Activity.AdditionalProperties.Should().NotBeNull();
        var policy = (System.Collections.Generic.IDictionary<string, object?>)result.Activity.AdditionalProperties!["policy"]!;
        // Insert: retry must default to 0 (non-idempotent — see #143 rubber-duck blocker #5).
        policy["retry"].Should().Be(0);
        policy["timeout"].Should().Be("12:00:00");
    }

    [Fact]
    public void Build_UpsertWriteBehavior_PolicyRetryDefaultsToThree()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Upsert, registry);

        var policy = (System.Collections.Generic.IDictionary<string, object?>)result.Activity.AdditionalProperties!["policy"]!;
        policy["retry"].Should().Be(3);
    }

    [Fact]
    public void Build_CustomPolicy_OverridesDefaults()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();
        var options = new DataFactoryGenerationOptions
        {
            CopyPolicy = new CopyActivityPolicy { Retry = 5, Timeout = "06:00:00", RetryIntervalInSeconds = 90 },
        };

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry, options);

        var policy = (System.Collections.Generic.IDictionary<string, object?>)result.Activity.AdditionalProperties!["policy"]!;
        policy["retry"].Should().Be(5);
        policy["timeout"].Should().Be("06:00:00");
        policy["retryIntervalInSeconds"].Should().Be(90);
    }

    [Fact]
    public void Build_FaultToleranceEnabled_WithLinkedService_EmitsLogSettings()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();
        var options = new DataFactoryGenerationOptions
        {
            FaultTolerance = new FaultToleranceOptions
            {
                Enabled = true,
                LogStorageLinkedServiceName = "MyStorageLS",
            },
        };

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry, options);

        result.Activity.TypeProperties["enableSkipIncompatibleRow"].Should().Be(true);
        var logSettings = (System.Collections.Generic.IDictionary<string, object?>)result.Activity.TypeProperties["logSettings"]!;
        var logLocation = (System.Collections.Generic.IDictionary<string, object?>)logSettings["logLocationSettings"]!;
        var lsRef = (System.Collections.Generic.IDictionary<string, object?>)logLocation["linkedServiceName"]!;
        lsRef["referenceName"].Should().Be("MyStorageLS"); // literal, not parameter
        lsRef["type"].Should().Be("LinkedServiceReference");
        logLocation["path"].Should().Be($"@pipeline().parameters.{ParameterCatalog.PipelineParamFaultToleranceLogPath}");
        result.Warnings.Should().NotContain(w => w.Contains("logSettings"));
    }

    [Fact]
    public void Build_FaultToleranceEnabled_NoLinkedService_OmitsLogSettings_WarnsAboutDataLoss()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();
        var options = new DataFactoryGenerationOptions
        {
            FaultTolerance = new FaultToleranceOptions { Enabled = true },
        };

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry, options);

        result.Activity.TypeProperties["enableSkipIncompatibleRow"].Should().Be(true);
        result.Activity.TypeProperties.Should().NotContainKey("logSettings");
        result.Warnings.Should().Contain(w => w.Contains("logSettings"));
    }

    [Fact]
    public void Build_FaultToleranceDisabled_DoesNotMutateTypeProperties()
    {
        var registry = new AdfNameRegistry();
        var builder = new CopyActivityBuilder();

        var result = builder.Build(SampleMapping(), "src", "sink", SinkWriteBehavior.Insert, registry);

        result.Activity.TypeProperties.Should().NotContainKey("enableSkipIncompatibleRow");
        result.Activity.TypeProperties.Should().NotContainKey("logSettings");
    }
}
