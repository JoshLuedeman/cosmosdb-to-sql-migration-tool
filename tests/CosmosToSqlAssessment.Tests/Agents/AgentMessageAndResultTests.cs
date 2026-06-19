using CosmosToSqlAssessment.Agents;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for the <see cref="AgentMessage"/> and <see cref="AgentResult"/> value records
/// and their static factory helpers (#210).
/// </summary>
public class AgentMessageAndResultTests
{
    [Fact]
    public void AgentMessage_factories_set_level_and_recent_timestamp()
    {
        var before = DateTimeOffset.UtcNow;

        var info = AgentMessage.Info("CosmosAnalyzer", "hello");
        var warn = AgentMessage.Warning("DataQuality", "careful");
        var error = AgentMessage.Error("Validator", "broken");

        var after = DateTimeOffset.UtcNow;

        info.Level.Should().Be(AgentMessageLevel.Info);
        info.AgentName.Should().Be("CosmosAnalyzer");
        info.Text.Should().Be("hello");
        info.TimestampUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

        warn.Level.Should().Be(AgentMessageLevel.Warning);
        error.Level.Should().Be(AgentMessageLevel.Error);
    }

    [Fact]
    public void AgentResult_succeeded_has_no_error()
    {
        var result = AgentResult.Succeeded("SqlPlanner", AgentRole.SqlPlanning, TimeSpan.FromSeconds(3));

        result.Status.Should().Be(AgentRunStatus.Succeeded);
        result.AgentName.Should().Be("SqlPlanner");
        result.Role.Should().Be(AgentRole.SqlPlanning);
        result.Error.Should().BeNull();
        result.Duration.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void AgentResult_failed_carries_error_and_duration()
    {
        var result = AgentResult.Failed("CosmosAnalyzer", AgentRole.CosmosAnalysis, "timeout", TimeSpan.FromSeconds(9));

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.Error.Should().Be("timeout");
        result.Duration.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void AgentResult_skipped_carries_reason_and_zero_duration()
    {
        var result = AgentResult.Skipped("DataQuality", AgentRole.DataQuality, "no cosmos analysis");

        result.Status.Should().Be(AgentRunStatus.Skipped);
        result.Error.Should().Be("no cosmos analysis");
        result.Duration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AgentResult_records_are_value_equal()
    {
        var a = AgentResult.Succeeded("A", AgentRole.Validation, TimeSpan.FromSeconds(1));
        var b = AgentResult.Succeeded("A", AgentRole.Validation, TimeSpan.FromSeconds(1));

        a.Should().Be(b);
    }
}
