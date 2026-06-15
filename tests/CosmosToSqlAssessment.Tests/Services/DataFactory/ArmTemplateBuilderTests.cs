using System.Text.Json;
using System.Text.Json.Nodes;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

/// <summary>
/// Tests for <see cref="ArmTemplateBuilder"/> (#146). The builder is pure: given a flat
/// list of (kind, name, properties) inputs + a parameter dictionary, it produces a
/// deterministic ARM template root. Each test exercises one rubber-duck-derived concern
/// (resource shape, parameter shape, reference walker, defaultValue rewrite, dependency
/// closure, sorting).
/// </summary>
public class ArmTemplateBuilderTests
{
    private readonly ArmTemplateBuilder _builder = new();
    private const string Factory = "dataFactoryName";

    private static JsonObject Properties(string jsonBody) =>
        (JsonObject)JsonNode.Parse(jsonBody)!;

    private static IReadOnlyDictionary<string, ArmParameterDefinition> WithFactoryOnly() =>
        new Dictionary<string, ArmParameterDefinition>(StringComparer.Ordinal)
        {
            [Factory] = ArmParameterDefinition.Required("string", "Target factory."),
        };

    [Fact]
    public void Build_NoResources_ReturnsValidArmRoot()
    {
        var template = _builder.Build(Array.Empty<ArmResourceInput>(), WithFactoryOnly(), Factory);

        template.Should().ContainKey("$schema");
        template["$schema"]!.ToString().Should().Contain("deploymentTemplate.json");
        template["contentVersion"].Should().Be("1.0.0.0");
        template["parameters"].Should().BeAssignableTo<IDictionary<string, object?>>();
        ((IList<Dictionary<string, object?>>)template["resources"]!).Should().BeEmpty();
    }

    [Fact]
    public void Build_FactoryParameter_NoDefaultValue_OmitsDefaultValueField()
    {
        var template = _builder.Build(Array.Empty<ArmResourceInput>(), WithFactoryOnly(), Factory);

        var parameters = (IDictionary<string, object?>)template["parameters"]!;
        parameters.Should().ContainKey(Factory);
        var def = (IDictionary<string, object?>)parameters[Factory]!;
        def["type"].Should().Be("string");
        def.Should().NotContainKey("defaultValue", "Required() parameters MUST be supplied by the operator");
    }

    [Fact]
    public void Build_StringParameter_EmitsTypeDefaultValueAndDescription()
    {
        var p = new Dictionary<string, ArmParameterDefinition>(StringComparer.Ordinal)
        {
            [Factory] = ArmParameterDefinition.Required("string", "Factory."),
            ["myParam"] = ArmParameterDefinition.String("hello", "A friendly default."),
        };
        var template = _builder.Build(Array.Empty<ArmResourceInput>(), p, Factory);
        var parameters = (IDictionary<string, object?>)template["parameters"]!;
        var def = (IDictionary<string, object?>)parameters["myParam"]!;

        def["type"].Should().Be("string");
        def["defaultValue"].Should().Be("hello");
        ((IDictionary<string, object?>)def["metadata"]!)["description"].Should().Be("A friendly default.");
    }

    [Fact]
    public void Build_SingleLinkedService_EmitsCorrectShape()
    {
        var input = new ArmResourceInput(
            ArmTemplateBuilder.LinkedServiceKind,
            "AzureSqlDatabaseLinkedService",
            Properties("""{ "type": "AzureSqlDatabase", "typeProperties": { "connectionString": "..." } }"""));

        var template = _builder.Build(new[] { input }, WithFactoryOnly(), Factory);

        var resources = ((IList<Dictionary<string, object?>>)template["resources"]!);
        resources.Should().HaveCount(1);
        var r = resources[0];

        r["type"].Should().Be("Microsoft.DataFactory/factories/linkedservices");
        r["apiVersion"].Should().Be(ArmTemplateBuilder.ApiVersion);
        r["name"].Should().Be("[concat(parameters('dataFactoryName'), '/AzureSqlDatabaseLinkedService')]");
        r.Should().NotContainKey("dependsOn", "single resource has nothing to depend on");
        r["properties"].Should().BeOfType<JsonObject>();
    }

    [Fact]
    public void Build_DatasetReferencingLinkedService_EmitsDependsOnLinkedService()
    {
        var ls = new ArmResourceInput(
            ArmTemplateBuilder.LinkedServiceKind, "CosmosDb_MyDb_LinkedService",
            Properties("""{ "type": "CosmosDb" }"""));
        var ds = new ArmResourceInput(
            ArmTemplateBuilder.DatasetKind, "Cosmos_users",
            Properties("""
                {
                  "type": "CosmosDbSqlApiCollection",
                  "linkedServiceName": { "referenceName": "CosmosDb_MyDb_LinkedService", "type": "LinkedServiceReference" }
                }
                """));

        var template = _builder.Build(new[] { ds, ls }, WithFactoryOnly(), Factory);

        var resources = ((IList<Dictionary<string, object?>>)template["resources"]!);
        // Linked service first (kind ordering).
        ((string)resources[0]["type"]!).Should().EndWith("/linkedservices");
        ((string)resources[1]["type"]!).Should().EndWith("/datasets");

        // Dataset has a dependsOn on the LS.
        var dsDeps = (IList<object?>)resources[1]["dependsOn"]!;
        dsDeps.Should().ContainSingle()
            .Which.Should().Be("[resourceId('Microsoft.DataFactory/factories/linkedservices', parameters('dataFactoryName'), 'CosmosDb_MyDb_LinkedService')]");
    }

    [Fact]
    public void Build_PipelineReferencingDatasets_EmitsSortedDedupedDependsOn()
    {
        var dsA = new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "Cosmos_a", Properties("""{ "type": "CosmosDbSqlApiCollection" }"""));
        var dsB = new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "Cosmos_b", Properties("""{ "type": "CosmosDbSqlApiCollection" }"""));
        // Pipeline references both Cosmos_a twice and Cosmos_b once.
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Migrate_x",
            Properties("""
                {
                  "activities": [
                    { "name": "c1", "type": "Copy",
                      "inputs":  [ { "referenceName": "Cosmos_a", "type": "DatasetReference" } ],
                      "outputs": [ { "referenceName": "Cosmos_b", "type": "DatasetReference" } ] },
                    { "name": "c2", "type": "Copy",
                      "inputs":  [ { "referenceName": "Cosmos_a", "type": "DatasetReference" } ],
                      "outputs": [ { "referenceName": "Cosmos_b", "type": "DatasetReference" } ] }
                  ]
                }
                """));

        var template = _builder.Build(new[] { pl, dsA, dsB }, WithFactoryOnly(), Factory);
        var pipelineResource = ((IList<Dictionary<string, object?>>)template["resources"]!).Single(r => ((string)r["type"]!).EndsWith("/pipelines"));

        var deps = (IList<object?>)pipelineResource["dependsOn"]!;
        deps.Should().HaveCount(2, "duplicates must be removed");
        deps[0].Should().Be("[resourceId('Microsoft.DataFactory/factories/datasets', parameters('dataFactoryName'), 'Cosmos_a')]");
        deps[1].Should().Be("[resourceId('Microsoft.DataFactory/factories/datasets', parameters('dataFactoryName'), 'Cosmos_b')]");
    }

    [Fact]
    public void Build_MasterPipelineReferencingChildPipelines_EmitsPipelineDeps()
    {
        var child = new ArmResourceInput(ArmTemplateBuilder.PipelineKind, "Migrate_MyDb", Properties("""{ "activities": [] }"""));
        var master = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "MasterMigrationPipeline",
            Properties("""
                {
                  "activities": [
                    { "name": "Run_Migrate_MyDb", "type": "ExecutePipeline",
                      "typeProperties": { "pipeline": { "referenceName": "Migrate_MyDb", "type": "PipelineReference" } } }
                  ]
                }
                """));

        var template = _builder.Build(new[] { master, child }, WithFactoryOnly(), Factory);
        var masterRes = ((IList<Dictionary<string, object?>>)template["resources"]!).Single(r => (string)r["name"]! == "[concat(parameters('dataFactoryName'), '/MasterMigrationPipeline')]");
        var deps = (IList<object?>)masterRes["dependsOn"]!;
        deps.Should().ContainSingle()
            .Which.Should().Be("[resourceId('Microsoft.DataFactory/factories/pipelines', parameters('dataFactoryName'), 'Migrate_MyDb')]");
    }

    [Fact]
    public void Build_LinkedServiceReferencingKeyVault_EmitsLinkedServiceToLinkedServiceDep()
    {
        // Cosmos LS references KV LS via typeProperties.accountKey.store — this is the
        // exact shape LinkedServiceBuilder emits when UseAzureKeyVault is true.
        var kv = new ArmResourceInput(ArmTemplateBuilder.LinkedServiceKind, "KeyVaultLinkedService", Properties("""{ "type": "AzureKeyVault" }"""));
        var cosmos = new ArmResourceInput(
            ArmTemplateBuilder.LinkedServiceKind, "CosmosDb_MyDb_LinkedService",
            Properties("""
                {
                  "type": "CosmosDb",
                  "typeProperties": {
                    "connectionString": "...",
                    "accountKey": {
                      "type": "AzureKeyVaultSecret",
                      "store": { "referenceName": "KeyVaultLinkedService", "type": "LinkedServiceReference" },
                      "secretName": "..."
                    }
                  }
                }
                """));

        var template = _builder.Build(new[] { cosmos, kv }, WithFactoryOnly(), Factory);
        var cosmosRes = ((IList<Dictionary<string, object?>>)template["resources"]!).Single(r => (string)r["name"]! == "[concat(parameters('dataFactoryName'), '/CosmosDb_MyDb_LinkedService')]");
        var deps = (IList<object?>)cosmosRes["dependsOn"]!;
        deps.Should().ContainSingle()
            .Which.Should().Be("[resourceId('Microsoft.DataFactory/factories/linkedservices', parameters('dataFactoryName'), 'KeyVaultLinkedService')]");
    }

    [Fact]
    public void Build_ReferenceToArtifactNotInTemplate_DoesNotEmitDependsOn()
    {
        // Pipeline references an externally-managed log-storage LS that we did NOT emit.
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Migrate_x",
            Properties("""
                {
                  "activities": [
                    { "name": "c1", "type": "Copy",
                      "typeProperties": {
                        "source": { "type": "CosmosDbSqlApiSource" },
                        "sink": { "type": "AzureSqlSink" },
                        "enableStaging": false,
                        "logSettings": {
                          "enableCopyActivityLog": true,
                          "logLocationSettings": {
                            "linkedServiceName": { "referenceName": "OperatorOwnedLogStorage", "type": "LinkedServiceReference" }
                          }
                        }
                      }
                    }
                  ]
                }
                """));

        var template = _builder.Build(new[] { pl }, WithFactoryOnly(), Factory);
        var res = ((IList<Dictionary<string, object?>>)template["resources"]!).Single();
        res.Should().NotContainKey("dependsOn", "external LS not in template set => no dependsOn edge");
    }

    [Fact]
    public void Build_PipelineWithArmOverrides_RewritesDefaultValueToParametersExpression()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cosmosDatabaseName"] = "cosmosDatabaseName_MyDb",
            ["sqlDatabaseName"] = "sqlDatabaseName_MyDb",
        };
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Migrate_MyDb",
            Properties("""
                {
                  "activities": [],
                  "parameters": {
                    "cosmosDatabaseName": { "type": "string", "defaultValue": "MyDb" },
                    "sqlDatabaseName":    { "type": "string", "defaultValue": "MyDb_SQL" },
                    "unrelated":          { "type": "string", "defaultValue": "leave-alone" }
                  }
                }
                """),
            overrides);

        var template = _builder.Build(new[] { pl }, WithFactoryOnly(), Factory);
        var res = ((IList<Dictionary<string, object?>>)template["resources"]!).Single();
        var properties = (JsonObject)res["properties"]!;
        var pipelineParams = (JsonObject)properties["parameters"]!;

        ((string)pipelineParams["cosmosDatabaseName"]!["defaultValue"]!).Should().Be("[parameters('cosmosDatabaseName_MyDb')]");
        ((string)pipelineParams["sqlDatabaseName"]!["defaultValue"]!).Should().Be("[parameters('sqlDatabaseName_MyDb')]");
        ((string)pipelineParams["unrelated"]!["defaultValue"]!).Should().Be("leave-alone", "not in overrides map => unchanged");
    }

    [Fact]
    public void Build_PipelineWithoutArmOverrides_LeavesDefaultsAsLiteral()
    {
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Migrate_MyDb",
            Properties("""{ "parameters": { "cosmosDatabaseName": { "type": "string", "defaultValue": "MyDb" } } }"""));

        var template = _builder.Build(new[] { pl }, WithFactoryOnly(), Factory);
        var res = ((IList<Dictionary<string, object?>>)template["resources"]!).Single();
        var pipelineParams = (JsonObject)((JsonObject)res["properties"]!)["parameters"]!;
        ((string)pipelineParams["cosmosDatabaseName"]!["defaultValue"]!).Should().Be("MyDb", "no override map => literal preserved");
    }

    [Fact]
    public void Build_ArmOverrides_LinkedServiceParamWithoutDefaultValue_IsNotRewritten()
    {
        // Linked-service parameter blocks have no defaultValue. The rewrite must skip
        // these silently and not throw / not add a defaultValue key.
        var ls = new ArmResourceInput(
            ArmTemplateBuilder.LinkedServiceKind, "CosmosDb_MyDb_LinkedService",
            Properties("""
                {
                  "type": "CosmosDb",
                  "parameters": { "cosmosDatabaseName": { "type": "string" } }
                }
                """),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["cosmosDatabaseName"] = "cosmosDatabaseName_MyDb" });

        var template = _builder.Build(new[] { ls }, WithFactoryOnly(), Factory);
        var res = ((IList<Dictionary<string, object?>>)template["resources"]!).Single();
        var lsParams = (JsonObject)((JsonObject)res["properties"]!)["parameters"]!;
        lsParams["cosmosDatabaseName"]!.AsObject().Should().NotContainKey("defaultValue");
    }

    [Fact]
    public void Build_ResourcesSortedByKindThenName_IsDeterministic()
    {
        var inputs = new[]
        {
            new ArmResourceInput(ArmTemplateBuilder.PipelineKind, "Z_pipe", Properties("""{ }""")),
            new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "Z_ds", Properties("""{ }""")),
            new ArmResourceInput(ArmTemplateBuilder.LinkedServiceKind, "Z_ls", Properties("""{ }""")),
            new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "A_ds", Properties("""{ }""")),
            new ArmResourceInput(ArmTemplateBuilder.LinkedServiceKind, "A_ls", Properties("""{ }""")),
        };

        var template = _builder.Build(inputs, WithFactoryOnly(), Factory);
        var resources = ((IList<Dictionary<string, object?>>)template["resources"]!);
        var names = resources.Select(r => (string)r["name"]!).ToList();

        names.Should().BeEquivalentTo(new[]
        {
            "[concat(parameters('dataFactoryName'), '/A_ls')]",
            "[concat(parameters('dataFactoryName'), '/Z_ls')]",
            "[concat(parameters('dataFactoryName'), '/A_ds')]",
            "[concat(parameters('dataFactoryName'), '/Z_ds')]",
            "[concat(parameters('dataFactoryName'), '/Z_pipe')]",
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Build_SelfReference_DoesNotEmitDependsOn()
    {
        // Pathological case but the de-duper handles it: a pipeline accidentally referencing
        // itself by name (e.g. via ExecutePipeline) must not appear in its own dependsOn.
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Self",
            Properties("""
                {
                  "activities": [
                    { "name": "r", "type": "ExecutePipeline",
                      "typeProperties": { "pipeline": { "referenceName": "Self", "type": "PipelineReference" } } }
                  ]
                }
                """));

        var template = _builder.Build(new[] { pl }, WithFactoryOnly(), Factory);
        var res = ((IList<Dictionary<string, object?>>)template["resources"]!).Single();
        res.Should().NotContainKey("dependsOn");
    }

    [Fact]
    public void Build_NestedIfFalseActivities_RecursesAndFindsReferences()
    {
        // Validation triplet shape from #145: the nested Fail in ifFalseActivities should
        // be walked too. In practice Fail has no references, but a generic walker proves
        // we don't only inspect top-level activities.
        var ds = new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "AzureSql_dbo_Users", Properties("""{ "type": "AzureSqlTable" }"""));
        var pl = new ArmResourceInput(
            ArmTemplateBuilder.PipelineKind, "Migrate_x",
            Properties("""
                {
                  "activities": [
                    { "name": "If1", "type": "IfCondition",
                      "typeProperties": {
                        "ifFalseActivities": [
                          { "name": "LookupNested", "type": "Lookup",
                            "typeProperties": { "dataset": { "referenceName": "AzureSql_dbo_Users", "type": "DatasetReference" } } }
                        ]
                      } }
                  ]
                }
                """));

        var template = _builder.Build(new[] { pl, ds }, WithFactoryOnly(), Factory);
        var pipelineRes = ((IList<Dictionary<string, object?>>)template["resources"]!).Single(r => ((string)r["type"]!).EndsWith("/pipelines"));
        var deps = (IList<object?>)pipelineRes["dependsOn"]!;
        deps.Should().ContainSingle()
            .Which.Should().Be("[resourceId('Microsoft.DataFactory/factories/datasets', parameters('dataFactoryName'), 'AzureSql_dbo_Users')]");
    }

    [Fact]
    public void Build_FullRoundTrip_ProducesValidJson()
    {
        var ls = new ArmResourceInput(ArmTemplateBuilder.LinkedServiceKind, "MyLs", Properties("""{ "type": "AzureSqlDatabase" }"""));
        var ds = new ArmResourceInput(ArmTemplateBuilder.DatasetKind, "MyDs",
            Properties("""{ "type": "AzureSqlTable", "linkedServiceName": { "referenceName": "MyLs", "type": "LinkedServiceReference" } }"""));

        var template = _builder.Build(new[] { ls, ds }, WithFactoryOnly(), Factory);
        var json = AdfJsonSerializer.Serialize(template);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("$schema").GetString().Should().Contain("deploymentTemplate.json");
        doc.RootElement.GetProperty("contentVersion").GetString().Should().Be("1.0.0.0");
        doc.RootElement.GetProperty("parameters").TryGetProperty(Factory, out _).Should().BeTrue();
        doc.RootElement.GetProperty("resources").GetArrayLength().Should().Be(2);
        // No leftover "additionalProperties" key from the JsonExtensionData round-trip.
        json.Should().NotContain("additionalProperties");
    }
}
