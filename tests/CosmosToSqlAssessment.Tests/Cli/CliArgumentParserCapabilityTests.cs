using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Tests.Cli;

/// <summary>
/// Parser/validation tests for the additive CLI capabilities wired in by #248 (--agentic-mode),
/// #256 (migration generate-alerts), #257 (migration publish-metrics) and #259 (feedback record).
/// These complement the existing <see cref="CliArgumentParserMigrationStatusTests"/> coverage and assert
/// the new flags/subcommands are parsed and validated without disturbing the pre-existing surface.
/// </summary>
public class CliArgumentParserCapabilityTests
{
    // ---- #248 --agentic-mode --------------------------------------------------------------------

    [Theory]
    [InlineData("sequential", AgentExecutionMode.Sequential)]
    [InlineData("parallel", AgentExecutionMode.Parallel)]
    [InlineData("conditional", AgentExecutionMode.Conditional)]
    [InlineData("CONDITIONAL", AgentExecutionMode.Conditional)]
    public void Parse_AgenticMode_SetsModeAndSpecifiedFlag(string value, AgentExecutionMode expected)
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "--agentic", "--agentic-mode", value }, output);

        options.Should().NotBeNull();
        options!.Agentic.Should().BeTrue();
        options.AgenticMode.Should().Be(expected);
        options.AgenticModeSpecified.Should().BeTrue();
    }

    [Fact]
    public void Parse_AgenticMode_DefaultsToSequentialWhenUnset()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "--agentic" }, output);

        options.Should().NotBeNull();
        options!.AgenticMode.Should().Be(AgentExecutionMode.Sequential);
        options.AgenticModeSpecified.Should().BeFalse();
    }

    [Fact]
    public void Parse_AgenticMode_RejectsNumericValue()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "--agentic", "--agentic-mode", "2" }, output);

        options.Should().BeNull();
        output.ToString().Should().Contain("Invalid value for --agentic-mode");
    }

    [Fact]
    public void Parse_AgenticMode_RejectsUnknownValue()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "--agentic", "--agentic-mode", "turbo" }, output);

        options.Should().BeNull();
        output.ToString().Should().Contain("Invalid value for --agentic-mode");
    }

    [Fact]
    public void Validate_AgenticModeWithoutAgentic_Fails()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(new[] { "--agentic-mode", "parallel" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeFalse();
        output.ToString().Should().Contain("--agentic-mode requires --agentic");
    }

    [Fact]
    public void Validate_AgenticModeWithAgentic_Succeeds()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(new[] { "--agentic", "--agentic-mode", "conditional" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeTrue();
    }

    // ---- #256 migration generate-alerts ---------------------------------------------------------

    [Fact]
    public void Parse_GenerateAlerts_SetsFlag()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "generate-alerts", "--output", "C:\\Reports" }, output);

        options.Should().NotBeNull();
        options!.GenerateAlerts.Should().BeTrue();
        options.OutputDirectory.Should().Be("C:\\Reports");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Validate_GenerateAlertsWithoutOutput_Fails()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(new[] { "migration", "generate-alerts" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeFalse();
        output.ToString().Should().Contain("'migration generate-alerts' requires --output");
    }

    // ---- #257 migration publish-metrics ---------------------------------------------------------

    [Fact]
    public void Parse_PublishMetrics_SetsFlag()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "migration", "publish-metrics" }, output);

        options.Should().NotBeNull();
        options!.PublishMetrics.Should().BeTrue();
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Parse_PublishMetricsWithWatchAndPollInterval_SetsBoth()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "publish-metrics", "--watch", "--poll-interval", "7" }, output);

        options.Should().NotBeNull();
        options!.PublishMetrics.Should().BeTrue();
        options.Watch.Should().BeTrue();
        options.PollIntervalSeconds.Should().Be(7);
    }

    // ---- #259 feedback record -------------------------------------------------------------------

    [Fact]
    public void Parse_FeedbackRecord_SetsFlagAndFile()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "feedback", "record", "--import-outcome", "outcome.json" }, output);

        options.Should().NotBeNull();
        options!.FeedbackRecord.Should().BeTrue();
        options.ImportOutcomeFile.Should().Be("outcome.json");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Parse_FeedbackWithoutRecord_ReturnsNullAndWritesHelp()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "feedback" }, output);

        options.Should().BeNull();
        output.ToString().Should().Contain("Did you mean 'feedback record'");
    }

    [Fact]
    public void Validate_FeedbackRecordWithoutFile_Fails()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(new[] { "feedback", "record" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeFalse();
        output.ToString().Should().Contain("'feedback record' requires --import-outcome");
    }

    [Fact]
    public void Validate_ImportOutcomeWithoutFeedbackRecord_Fails()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(new[] { "--import-outcome", "outcome.json" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeFalse();
        output.ToString().Should().Contain("--import-outcome is only valid with the 'feedback record' command");
    }

    [Fact]
    public void Validate_FeedbackRecordWithFile_Succeeds()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(
            new[] { "feedback", "record", "--import-outcome", "outcome.json" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeTrue();
    }

    // ---- subcommand exclusivity -----------------------------------------------------------------

    [Fact]
    public void Validate_CombiningTwoSubcommands_Fails()
    {
        var output = new StringWriter();
        var options = CliArgumentParser.Parse(
            new[] { "migration", "status", "--test-connection" }, new StringWriter());
        options.Should().NotBeNull();

        var ok = CliArgumentParser.Validate(options!, output);

        ok.Should().BeFalse();
        output.ToString().Should().Contain("Cannot combine the commands");
    }

    // ---- help text ------------------------------------------------------------------------------

    [Fact]
    public void DisplayHelp_IncludesNewCommandsAndFlags()
    {
        var output = new StringWriter();

        CliArgumentParser.DisplayHelp(output);

        var text = output.ToString();
        text.Should().Contain("--agentic-mode");
        text.Should().Contain("migration generate-alerts");
        text.Should().Contain("migration publish-metrics");
        text.Should().Contain("feedback record");
        text.Should().Contain("--import-outcome");
    }
}
