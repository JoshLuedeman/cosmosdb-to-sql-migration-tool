using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Tests.Cli;

public class CliArgumentParserTests
{
    // ---- Parse: empty + help ------------------------------------------------

    [Fact]
    public void Parse_EmptyArgs_ReturnsDefaultOptions()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(Array.Empty<string>(), output);

        options.Should().NotBeNull();
        options!.AnalyzeAllDatabases.Should().BeFalse();
        options.DatabaseName.Should().BeNull();
        options.OutputDirectory.Should().BeNull();
        options.AutoDiscoverMonitoring.Should().BeFalse();
        options.AccountEndpoint.Should().BeNull();
        options.WorkspaceId.Should().BeNull();
        options.AssessmentOnly.Should().BeFalse();
        options.ProjectOnly.Should().BeFalse();
        options.TestConnection.Should().BeFalse();
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--HELP")]
    [InlineData("-H")]
    public void Parse_HelpFlag_ReturnsNullAndWritesHelp(string helpFlag)
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { helpFlag }, output);

        options.Should().BeNull();
        var text = output.ToString();
        text.Should().Contain("Cosmos DB to SQL Migration Assessment Tool");
        text.Should().Contain("Usage:");
    }

    // ---- Parse: boolean flags ----------------------------------------------

    [Theory]
    [InlineData("--all-databases")]
    [InlineData("-a")]
    public void Parse_AllDatabasesFlag_SetsAnalyzeAllDatabases(string flag)
    {
        var options = CliArgumentParser.Parse(new[] { flag }, new StringWriter());

        options.Should().NotBeNull();
        options!.AnalyzeAllDatabases.Should().BeTrue();
    }

    [Fact]
    public void Parse_AutoDiscoverFlag_SetsAutoDiscoverMonitoring()
    {
        var options = CliArgumentParser.Parse(new[] { "--auto-discover" }, new StringWriter());

        options!.AutoDiscoverMonitoring.Should().BeTrue();
    }

    [Fact]
    public void Parse_SkipAutoDiscoveryFlag_SetsSkipAutoDiscovery()
    {
        var options = CliArgumentParser.Parse(new[] { "--skip-auto-discovery" }, new StringWriter());

        options!.SkipAutoDiscovery.Should().BeTrue();
    }

    [Fact]
    public void Parse_DefaultOptions_SkipAutoDiscoveryIsFalse()
    {
        var options = CliArgumentParser.Parse(Array.Empty<string>(), new StringWriter());

        options!.SkipAutoDiscovery.Should().BeFalse();
    }

    [Fact]
    public void Parse_AssessmentOnlyFlag_SetsAssessmentOnly()
    {
        var options = CliArgumentParser.Parse(new[] { "--assessment-only" }, new StringWriter());

        options!.AssessmentOnly.Should().BeTrue();
    }

    [Fact]
    public void Parse_ProjectOnlyFlag_SetsProjectOnly()
    {
        var options = CliArgumentParser.Parse(new[] { "--project-only" }, new StringWriter());

        options!.ProjectOnly.Should().BeTrue();
    }

    [Fact]
    public void Parse_TestConnectionFlag_SetsTestConnection()
    {
        var options = CliArgumentParser.Parse(new[] { "--test-connection" }, new StringWriter());

        options!.TestConnection.Should().BeTrue();
    }

    // ---- Parse: value-taking flags -----------------------------------------

    [Theory]
    [InlineData("--database", "MyDatabase")]
    [InlineData("-d", "MyDatabase")]
    public void Parse_DatabaseFlag_CapturesNextArgIntoDatabaseName(string flag, string value)
    {
        var options = CliArgumentParser.Parse(new[] { flag, value }, new StringWriter());

        options!.DatabaseName.Should().Be(value);
    }

    [Theory]
    [InlineData("--output", "C:/reports")]
    [InlineData("-o", "C:/reports")]
    public void Parse_OutputFlag_CapturesNextArgIntoOutputDirectory(string flag, string value)
    {
        var options = CliArgumentParser.Parse(new[] { flag, value }, new StringWriter());

        options!.OutputDirectory.Should().Be(value);
    }

    [Theory]
    [InlineData("--endpoint", "https://example.documents.azure.com:443/")]
    [InlineData("-e", "https://example.documents.azure.com:443/")]
    public void Parse_EndpointFlag_CapturesNextArgIntoAccountEndpoint(string flag, string value)
    {
        var options = CliArgumentParser.Parse(new[] { flag, value }, new StringWriter());

        options!.AccountEndpoint.Should().Be(value);
    }

    [Theory]
    [InlineData("--workspace-id", "12345678-1234-1234-1234-123456789012")]
    [InlineData("-w", "12345678-1234-1234-1234-123456789012")]
    public void Parse_WorkspaceIdFlag_CapturesNextArgIntoWorkspaceId(string flag, string value)
    {
        var options = CliArgumentParser.Parse(new[] { flag, value }, new StringWriter());

        options!.WorkspaceId.Should().Be(value);
    }

    // ---- Parse: unknown args -----------------------------------------------

    [Fact]
    public void Parse_UnknownArg_ReturnsNullAndWritesUnknownArgumentAndHelp()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "--not-a-real-flag" }, output);

        options.Should().BeNull();
        var text = output.ToString();
        text.Should().Contain("Unknown argument: --not-a-real-flag");
        text.Should().Contain("Usage:");
    }

    // ---- Parse: pre-existing quirks (preserved 1-for-1) --------------------

    [Fact]
    public void Parse_ValueFlagAtEndWithoutValue_SilentlyIgnoresFlag()
    {
        // Pre-existing parser quirk preserved by #186 refactor:
        // a value-taking flag at the end of args (no following token) is silently dropped.
        var options = CliArgumentParser.Parse(new[] { "--database" }, new StringWriter());

        options.Should().NotBeNull();
        options!.DatabaseName.Should().BeNull();
    }

    [Fact]
    public void Parse_ValueFlagFollowedByAnotherFlag_ConsumesNextFlagAsValue()
    {
        // Pre-existing parser quirk preserved by #186 refactor:
        // value-taking flags greedily consume the next arg even if it looks like a flag.
        // Note: a trailing bare value (`C:/reports`) after this would hit the
        // default branch and return null — we test the two-token case here to
        // isolate the "next token consumed as value" contract.
        var options = CliArgumentParser.Parse(
            new[] { "--database", "--output" },
            new StringWriter());

        options.Should().NotBeNull();
        options!.DatabaseName.Should().Be("--output");
        options.OutputDirectory.Should().BeNull();
    }

    [Fact]
    public void Parse_CaseInsensitiveFlagName_IsAccepted()
    {
        // Parser ToLower()s each arg before matching, so --DATABASE works.
        var options = CliArgumentParser.Parse(new[] { "--DATABASE", "MyDb" }, new StringWriter());

        options!.DatabaseName.Should().Be("MyDb");
    }

    [Fact]
    public void Parse_MultipleFlagsCombined_AllSet()
    {
        var args = new[]
        {
            "--all-databases",
            "--endpoint", "https://example.documents.azure.com:443/",
            "--workspace-id", "12345678-1234-1234-1234-123456789012",
            "--output", "C:/reports",
            "--auto-discover",
            "--assessment-only"
        };

        var options = CliArgumentParser.Parse(args, new StringWriter());

        options.Should().NotBeNull();
        options!.AnalyzeAllDatabases.Should().BeTrue();
        options.AccountEndpoint.Should().Be("https://example.documents.azure.com:443/");
        options.WorkspaceId.Should().Be("12345678-1234-1234-1234-123456789012");
        options.OutputDirectory.Should().Be("C:/reports");
        options.AutoDiscoverMonitoring.Should().BeTrue();
        options.AssessmentOnly.Should().BeTrue();
        options.ProjectOnly.Should().BeFalse();
    }

    // ---- Validate -----------------------------------------------------------

    [Fact]
    public void Validate_AssessmentOnlyAndProjectOnlyBothTrue_ReturnsFalseAndWritesError()
    {
        var output = new StringWriter();
        var options = new CliOptions { AssessmentOnly = true, ProjectOnly = true };

        var result = CliArgumentParser.Validate(options, output);

        result.Should().BeFalse();
        var text = output.ToString();
        text.Should().Contain("Cannot specify both");
        text.Should().Contain("--assessment-only");
        text.Should().Contain("--project-only");
    }

    [Fact]
    public void Validate_AssessmentOnlyAlone_ReturnsTrue()
    {
        var result = CliArgumentParser.Validate(
            new CliOptions { AssessmentOnly = true },
            new StringWriter());

        result.Should().BeTrue();
    }

    [Fact]
    public void Validate_ProjectOnlyAlone_ReturnsTrue()
    {
        var result = CliArgumentParser.Validate(
            new CliOptions { ProjectOnly = true },
            new StringWriter());

        result.Should().BeTrue();
    }

    [Fact]
    public void Validate_DefaultCliOptions_ReturnsTrue()
    {
        var result = CliArgumentParser.Validate(new CliOptions(), new StringWriter());

        result.Should().BeTrue();
    }

    // ---- DisplayHelp -------------------------------------------------------

    [Fact]
    public void DisplayHelp_WritesUsageAndAllFlagNames()
    {
        var output = new StringWriter();

        CliArgumentParser.DisplayHelp(output);

        var text = output.ToString();
        text.Should().Contain("Cosmos DB to SQL Migration Assessment Tool");
        text.Should().Contain("Usage:");
        text.Should().Contain("--help");
        text.Should().Contain("--all-databases");
        text.Should().Contain("--database");
        text.Should().Contain("--endpoint");
        text.Should().Contain("--workspace-id");
        text.Should().Contain("--output");
        text.Should().Contain("--auto-discover");
        text.Should().Contain("--assessment-only");
        text.Should().Contain("--project-only");
        text.Should().Contain("--test-connection");
        text.Should().Contain("Examples:");
    }
}
