using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class WatermarkSchemaBuilderTests
{
    private static IncrementalCopyOptions Options(string schema = "dbo", string table = "__AdfWatermark") =>
        new() { Enabled = true, WatermarkSchemaName = schema, WatermarkTableName = table };

    [Fact]
    public void BuildCreateScript_IsIdempotent_WithObjectIdGuard()
    {
        var script = new WatermarkSchemaBuilder().BuildCreateScript(Options());

        script.Should().Contain("IF OBJECT_ID(N'[dbo].[__AdfWatermark]', N'U') IS NULL");
        script.Should().Contain("CREATE TABLE [dbo].[__AdfWatermark]");
        script.Should().Contain("[mappingKey] NVARCHAR(450) NOT NULL PRIMARY KEY");
        script.Should().Contain("[lastTs]     BIGINT");
        script.Should().Contain("[updatedUtc] DATETIME2(3)");
    }

    [Fact]
    public void BuildCreateScript_RespectsCustomSchemaAndTableNames()
    {
        var script = new WatermarkSchemaBuilder().BuildCreateScript(Options("etl", "Cosmos_Watermark"));

        script.Should().Contain("[etl].[Cosmos_Watermark]");
        script.Should().NotContain("[dbo].[__AdfWatermark]");
    }

    [Fact]
    public void BuildCreateScript_RejectsBlankSchemaName()
    {
        var act = () => new WatermarkSchemaBuilder().BuildCreateScript(Options(schema: " "));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildUpdateScript_EmitsRaceConditionGuard()
    {
        var script = new WatermarkSchemaBuilder().BuildUpdateScript(
            Options(), "mapping/key", "@{int(variables('newTs'))}");

        // Race-condition guard: only advance if new > current. Caught by rubber-duck.
        script.Should().Contain("WHEN MATCHED AND S.[lastTs] > T.[lastTs] THEN");
        script.Should().Contain("MERGE [dbo].[__AdfWatermark] AS T");
        script.Should().Contain("WHEN NOT MATCHED THEN");
    }

    [Fact]
    public void BuildUpdateScript_EscapesSingleQuotesInMappingKey()
    {
        var script = new WatermarkSchemaBuilder().BuildUpdateScript(
            Options(), "tenant'X::container->[Db].[dbo].[T]", "0");

        // Single-quote doubling — embedded `'X` becomes `''X` inside the SQL literal.
        script.Should().Contain("'tenant''X::container->[Db].[dbo].[T]'");
    }

    [Fact]
    public void BuildSelectScript_UsesIsNullSentinelForFirstRun()
    {
        var script = new WatermarkSchemaBuilder().BuildSelectScript(
            Options(), "MyDb::users->[MyDb_SQL].[dbo].[Users]", "0");

        // First-run path: ISNULL((SELECT TOP 1 ...), 0) so no row returns a bootstrap value
        // instead of NULL (which the ADF SetVariable cannot consume cleanly).
        script.Should().StartWith("SELECT ISNULL((SELECT TOP 1 [lastTs] FROM [dbo].[__AdfWatermark] WHERE [mappingKey] = ");
        script.Should().Contain("'MyDb::users->[MyDb_SQL].[dbo].[Users]'");
        script.Should().EndWith(" AS lastTs");
    }

    [Fact]
    public void BuildSelectScript_BakesInitialExpressionVerbatim_NoQuoting()
    {
        var script = new WatermarkSchemaBuilder().BuildSelectScript(
            Options(), "k", "CAST(@{pipeline().parameters.initialTs} AS BIGINT)");

        // The initialExpression is a SQL fragment (not a literal value), so it MUST NOT
        // be wrapped in quotes. This is what enables the IncrementalCopyActivityBuilder
        // to splice in an ADF @concat() against the pipeline parameter.
        script.Should().Contain("CAST(@{pipeline().parameters.initialTs} AS BIGINT)");
        script.Should().NotContain("'CAST(");
    }

    [Fact]
    public void MappingKeyMaxLength_MatchesSqlServerIndexLimit()
    {
        // 900-byte index limit / 2 bytes-per-NVARCHAR-char = 450. Anything larger would
        // require an INCLUDE column or a hash index — fixing the constant here would
        // change the watermark DDL without updating downstream consumers.
        WatermarkSchemaBuilder.MappingKeyMaxLength.Should().Be(450);
    }
}
