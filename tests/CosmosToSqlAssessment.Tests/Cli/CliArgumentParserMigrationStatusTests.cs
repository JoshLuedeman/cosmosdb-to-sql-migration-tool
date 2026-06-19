using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Tests.Cli;

public class CliArgumentParserMigrationStatusTests
{
    [Fact]
    public void Parse_MigrationStatus_SetsFlag()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "migration", "status" }, output);

        options.Should().NotBeNull();
        options!.MigrationStatus.Should().BeTrue();
        options.Watch.Should().BeFalse();
        options.PollIntervalSeconds.Should().Be(10);
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Parse_MigrationStatus_IsCaseInsensitive()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "MIGRATION", "STATUS" }, output);

        options.Should().NotBeNull();
        options!.MigrationStatus.Should().BeTrue();
    }

    [Fact]
    public void Parse_MigrationStatusWithWatchAndPollInterval_SetsBoth()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "status", "--watch", "--poll-interval", "5" },
            output);

        options.Should().NotBeNull();
        options!.MigrationStatus.Should().BeTrue();
        options.Watch.Should().BeTrue();
        options.PollIntervalSeconds.Should().Be(5);
    }

    [Fact]
    public void Parse_MigrationStatus_IsAdditiveWithSiblingFlags()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "status", "--skip-auto-discovery", "--interactive", "--agentic" },
            output);

        options.Should().NotBeNull();
        options!.MigrationStatus.Should().BeTrue();
        options.SkipAutoDiscovery.Should().BeTrue();
        options.Interactive.Should().BeTrue();
        options.Agentic.Should().BeTrue();
    }

    [Fact]
    public void Parse_MigrationWithoutStatus_ReturnsNullAndWritesHelp()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(new[] { "migration" }, output);

        options.Should().BeNull();
        output.ToString().Should().Contain("Did you mean 'migration status'");
    }

    [Fact]
    public void Parse_InvalidPollInterval_ReturnsNull()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "status", "--poll-interval", "abc" },
            output);

        options.Should().BeNull();
        output.ToString().Should().Contain("Invalid value for --poll-interval");
    }

    [Fact]
    public void Parse_NonPositivePollInterval_ReturnsNull()
    {
        var output = new StringWriter();

        var options = CliArgumentParser.Parse(
            new[] { "migration", "status", "--poll-interval", "0" },
            output);

        options.Should().BeNull();
    }

    [Fact]
    public void DisplayHelp_IncludesMigrationStatusSubcommand()
    {
        var output = new StringWriter();

        CliArgumentParser.DisplayHelp(output);

        var text = output.ToString();
        text.Should().Contain("migration status");
        text.Should().Contain("--watch");
        text.Should().Contain("--poll-interval");
    }
}
