using System.Text.Json;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class IncrementalCopyActivityBuilderTests
{
    private static ContainerMapping Mapping(string container = "users", string schema = "dbo", string table = "Users") => new()
    {
        SourceContainer = container,
        TargetSchema = schema,
        TargetTable = table,
        FieldMappings =
        {
            new() { SourceField = "id", SourceType = "string", TargetColumn = "Id", TargetType = "NVARCHAR(100)" },
        },
    };

    private static PipelineActivity FakeCopy(string name = "Copy_users_to_dbo_Users") => new()
    {
        Name = name,
        Type = "Copy",
        TypeProperties =
        {
            ["source"] = new Dictionary<string, object?>
            {
                ["type"] = "CosmosDbSqlApiSource",
            },
        },
    };

    private static (IncrementalGroup group, AdfNameRegistry registry, IncrementalCopyOptions options) Build(
        IncrementalCopyOptions? options = null,
        ContainerMapping? mapping = null,
        PipelineActivity? copy = null)
    {
        var registry = new AdfNameRegistry();
        var opts = options ?? new IncrementalCopyOptions { Enabled = true };
        var builder = new IncrementalCopyActivityBuilder();
        var group = builder.BuildGroup(
            mapping ?? Mapping(),
            sourceDatabaseName: "MyDb",
            targetDatabaseName: "MyDb_SQL",
            azureSqlLinkedServiceName: "AzureSqlLs",
            watermarkDatasetName: "AzureSql_AdfWatermark",
            copy: copy ?? FakeCopy(),
            registry,
            opts);
        return (group, registry, opts);
    }

    // ---- Mapping key shape (rubber-duck blocker B5) ----

    [Fact]
    public void BuildMappingKey_HasDeterministicShape()
    {
        var key = IncrementalCopyActivityBuilder.BuildMappingKey("Src", "users", "Tgt", "dbo", "Users");
        key.Should().Be("Src::users->[Tgt].[dbo].[Users]");
    }

    [Fact]
    public void BuildMappingKey_TruncatesEachComponentTo80Chars()
    {
        var long100 = new string('x', 100);
        var key = IncrementalCopyActivityBuilder.BuildMappingKey(long100, long100, long100, "dbo", "T");
        // The 4 truncated components (80 chars each) + separators + schema/table + brackets.
        // The point is the key length stays well under NVARCHAR(450) PK budget.
        key.Length.Should().BeLessThanOrEqualTo(WatermarkSchemaBuilder.MappingKeyMaxLength);
    }

    [Fact]
    public void BuildMappingKey_RejectsBlankComponents()
    {
        var act = () => IncrementalCopyActivityBuilder.BuildMappingKey("", "c", "t", "s", "tbl");
        act.Should().Throw<ArgumentException>();
    }

    // ---- Query builders ----

    [Fact]
    public void BuildIncrementalCosmosQuery_UsesIntCoercedAdfExpressionInterpolation()
    {
        var q = IncrementalCopyActivityBuilder.BuildIncrementalCosmosQuery("_ts", "lastWatermark_x", "newWatermark_x");

        // int() coercion prevents lexicographic compare bugs (10- vs 11-digit boundary in 2286)
        // and the @{...} syntax is the documented ADF Cosmos source-query interpolation.
        q.Should().Be(
            "SELECT * FROM c WHERE c._ts > @{int(variables('lastWatermark_x'))} "
            + "AND c._ts <= @{int(variables('newWatermark_x'))}");
    }

    [Fact]
    public void BuildIncrementalCountQuery_AliasesCount_AndUsesSameWindow()
    {
        var q = IncrementalCopyActivityBuilder.BuildIncrementalCountQuery("_ts", "lwm", "nwm", "docCount");
        q.Should().Contain("SELECT COUNT(1) AS docCount FROM c WHERE c._ts > @{int(variables('lwm'))}");
        q.Should().Contain("AND c._ts <= @{int(variables('nwm'))}");
    }

    // ---- newWatermark expression (rubber-duck B2: safety lag + clamp) ----

    [Fact]
    public void BuildNewWatermarkExpression_AppliesSafetyLag_AndClampsToLast()
    {
        var expr = IncrementalCopyActivityBuilder.BuildNewWatermarkExpression("lastWatermark_x", safetyLagSeconds: 60);

        // unix-now = (ticks(utcnow) - ticks(epoch)) / 10_000_000
        expr.Should().Contain("ticks(utcnow())");
        expr.Should().Contain("ticks('1970-01-01')");
        expr.Should().Contain("10000000");
        expr.Should().Contain("sub(div(sub(ticks(utcnow()), ticks('1970-01-01')), 10000000), 60)");
        // Clamp guarantees newTs >= lastTs (defends against safety-lag pushing window backwards).
        expr.Should().Contain("max(");
        expr.Should().Contain("int(variables('lastWatermark_x'))");
        expr.Should().StartWith("@string(");
    }

    [Fact]
    public void BuildNewWatermarkExpression_RejectsNegativeLag()
    {
        var act = () => IncrementalCopyActivityBuilder.BuildNewWatermarkExpression("x", -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- Group shape ----

    [Fact]
    public void BuildGroup_ProducesFourActivities_WithExpectedTypes()
    {
        var (g, _, _) = Build();

        g.LookupWatermark.Type.Should().Be("Lookup");
        g.SetLastTs.Type.Should().Be("SetVariable");
        g.SetNewTs.Type.Should().Be("SetVariable");
        g.ScriptUpdateWatermark.Type.Should().Be("Script");
    }

    [Fact]
    public void BuildGroup_LookupReferencesWatermarkDataset_WithSqlReaderQueryExpression()
    {
        var (g, _, _) = Build();

        var ds = (Dictionary<string, object?>)g.LookupWatermark.TypeProperties["dataset"]!;
        ds["referenceName"].Should().Be("AzureSql_AdfWatermark");
        ds["type"].Should().Be("DatasetReference");

        var source = (Dictionary<string, object?>)g.LookupWatermark.TypeProperties["source"]!;
        source["type"].Should().Be("AzureSqlSource");
        var sqlReaderQuery = (Dictionary<string, object?>)source["sqlReaderQuery"]!;
        sqlReaderQuery["type"].Should().Be("Expression");
        sqlReaderQuery["value"].Should().BeOfType<string>()
            .Which.Should().StartWith("@concat(");
    }

    [Fact]
    public void BuildGroup_DependsOnChain_LookupToSetLastToSetNewToCopyToScript()
    {
        var copy = FakeCopy();
        var (g, _, _) = Build(copy: copy);

        // SetLast depends on Lookup.
        var setLastDeps = ExtractDeps(g.SetLastTs).Single();
        setLastDeps.Should().Be(g.LookupActivityName);

        // SetNew depends on SetLast.
        var setNewDeps = ExtractDeps(g.SetNewTs).Single();
        setNewDeps.Should().Be(g.SetLastTsActivityName);

        // Copy now has dependsOn pointing at SetNewTs (merged into AdditionalProperties).
        var copyDeps = ExtractDeps(copy);
        copyDeps.Should().Contain(g.SetNewTsActivityName);

        // ScriptUpdate depends on Copy.
        var scriptDeps = ExtractDeps(g.ScriptUpdateWatermark).Single();
        scriptDeps.Should().Be(copy.Name);
    }

    [Fact]
    public void BuildGroup_OverridesCopySourceQuery_WithIncrementalWindow()
    {
        var copy = FakeCopy();
        Build(copy: copy);

        var source = (Dictionary<string, object?>)copy.TypeProperties["source"]!;
        var query = source["query"]!.ToString()!;
        query.Should().Contain("WHERE c._ts >");
        query.Should().Contain("AND c._ts <=");
    }

    [Fact]
    public void BuildGroup_MappingKeyAndDerivedNames_AreDeterministic()
    {
        var (g, _, _) = Build();

        g.MappingKey.Should().Be("MyDb::users->[MyDb_SQL].[dbo].[Users]");
        g.SanitisedMappingKey.Should().NotBeNullOrEmpty();
        g.InitialWatermarkParameterName.Should().Be($"incrementalInitialWatermark_{g.SanitisedMappingKey}");
        g.LastWatermarkVariableName.Should().Be($"lastWatermark_{g.SanitisedMappingKey}");
        g.NewWatermarkVariableName.Should().Be($"newWatermark_{g.SanitisedMappingKey}");
    }

    // ---- OverrideCopySourceQuery (helper, also called externally for #145 query rewriting) ----

    [Fact]
    public void OverrideCopySourceQuery_ReplacesExistingQuery()
    {
        var copy = FakeCopy();
        ((Dictionary<string, object?>)copy.TypeProperties["source"]!)["query"] = "OLD";

        IncrementalCopyActivityBuilder.OverrideCopySourceQuery(copy, "NEW");

        var query = ((Dictionary<string, object?>)copy.TypeProperties["source"]!)["query"];
        query.Should().Be("NEW");
    }

    [Fact]
    public void OverrideCopySourceQuery_ThrowsWhenSourceMissing()
    {
        var copy = new PipelineActivity { Name = "C", Type = "Copy" };
        var act = () => IncrementalCopyActivityBuilder.OverrideCopySourceQuery(copy, "Q");
        act.Should().Throw<InvalidOperationException>().WithMessage("*has no `source`*");
    }

    // ---- MergeDependsOn (preserves existing entries — rubber-duck on #145) ----

    [Fact]
    public void MergeDependsOn_PreservesExistingDependsOn()
    {
        var act = new PipelineActivity
        {
            Name = "X",
            Type = "Copy",
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["dependsOn"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["activity"] = "Existing",
                        ["dependencyConditions"] = new[] { "Succeeded" },
                    },
                },
                ["policy"] = new Dictionary<string, object?> { ["retry"] = 0 },
            },
        };

        IncrementalCopyActivityBuilder.MergeDependsOn(act, "New");

        var deps = ExtractDeps(act).ToList();
        deps.Should().HaveCount(2);
        deps.Should().Contain("Existing");
        deps.Should().Contain("New");
        // Policy must stay untouched.
        act.AdditionalProperties!.Should().ContainKey("policy");
    }

    // ---- EnsureWatermarkTable preamble ----

    [Fact]
    public void BuildEnsureTableActivity_IsScriptActivityWithIdempotentCreateDdl()
    {
        var registry = new AdfNameRegistry();
        var builder = new IncrementalCopyActivityBuilder();
        var act = builder.BuildEnsureTableActivity("AzureSqlLs", registry, new IncrementalCopyOptions { Enabled = true });

        act.Type.Should().Be("Script");
        var scripts = (List<object?>)act.TypeProperties["scripts"]!;
        var first = (Dictionary<string, object?>)scripts.Single()!;
        first["type"].Should().Be("NonQuery");
        first["text"].Should().BeOfType<string>()
            .Which.Should().Contain("IF OBJECT_ID(");
    }

    // ---- helpers ----

    private static IEnumerable<string> ExtractDeps(PipelineActivity act)
    {
        if (act.AdditionalProperties is null
            || !act.AdditionalProperties.TryGetValue("dependsOn", out var depsObj)
            || depsObj is not IEnumerable<object?> depsList)
        {
            return Enumerable.Empty<string>();
        }
        return depsList
            .OfType<Dictionary<string, object?>>()
            .Select(d => d["activity"]!.ToString()!);
    }
}
