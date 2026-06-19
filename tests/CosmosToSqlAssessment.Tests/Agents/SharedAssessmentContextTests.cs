using CosmosToSqlAssessment.Agents;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for the foundational agent communication contract introduced in #210:
/// <see cref="SharedAssessmentContext"/>, <see cref="AgentMessage"/>, and <see cref="AgentResult"/>.
/// </summary>
public class SharedAssessmentContextTests
{
    private static SharedAssessmentContext NewContext() => new("AppDb", "test-account");

    [Fact]
    public void Constructor_rejects_empty_database_name()
    {
        var act = () => new SharedAssessmentContext("", "acct");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_tolerates_null_account_name()
    {
        var context = new SharedAssessmentContext("AppDb", null!);
        context.CosmosAccountName.Should().BeEmpty();
        context.DatabaseName.Should().Be("AppDb");
    }

    [Fact]
    public void Domain_outputs_round_trip_and_flip_has_flags()
    {
        var context = NewContext();

        context.HasCosmosAnalysis.Should().BeFalse();
        context.HasSqlAssessment.Should().BeFalse();
        context.HasDataQualityAnalysis.Should().BeFalse();
        context.HasDataFactoryEstimate.Should().BeFalse();

        var cosmos = new CosmosDbAnalysis();
        var sql = new SqlMigrationAssessment();
        var quality = new DataQualityAnalysis();
        var estimate = new DataFactoryEstimate();

        context.SetCosmosAnalysis("CosmosAnalyzer", cosmos);
        context.SetSqlAssessment("SqlPlanner", sql);
        context.SetDataQualityAnalysis("DataQuality", quality);
        context.SetDataFactoryEstimate("Orchestrator", estimate);

        context.CosmosAnalysis.Should().BeSameAs(cosmos);
        context.SqlAssessment.Should().BeSameAs(sql);
        context.DataQualityAnalysis.Should().BeSameAs(quality);
        context.DataFactoryEstimate.Should().BeSameAs(estimate);

        context.HasCosmosAnalysis.Should().BeTrue();
        context.HasSqlAssessment.Should().BeTrue();
        context.HasDataQualityAnalysis.Should().BeTrue();
        context.HasDataFactoryEstimate.Should().BeTrue();
    }

    [Fact]
    public void Setting_a_domain_output_twice_throws()
    {
        var context = NewContext();
        context.SetCosmosAnalysis("CosmosAnalyzer", new CosmosDbAnalysis());

        var act = () => context.SetCosmosAnalysis("Rogue", new CosmosDbAnalysis());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already been set*Rogue*");
    }

    [Fact]
    public void Set_methods_reject_null_output()
    {
        var context = NewContext();
        var act = () => context.SetSqlAssessment("SqlPlanner", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Missing_required_outputs_excludes_optional_data_quality()
    {
        var context = NewContext();

        context.GetMissingRequiredOutputs()
            .Should().BeEquivalentTo(new[] { "CosmosAnalysis", "SqlAssessment", "DataFactoryEstimate" });
        context.IsCompleteForAssessmentResult().Should().BeFalse();

        context.SetCosmosAnalysis("CosmosAnalyzer", new CosmosDbAnalysis());
        context.SetSqlAssessment("SqlPlanner", new SqlMigrationAssessment());
        context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate());

        // Data quality intentionally not set — it is optional.
        context.GetMissingRequiredOutputs().Should().BeEmpty();
        context.IsCompleteForAssessmentResult().Should().BeTrue();
    }

    [Fact]
    public void Messages_returns_ordered_immutable_snapshot()
    {
        var context = NewContext();

        context.LogInfo("CosmosAnalyzer", "starting");
        context.LogWarning("DataQuality", "skipped");
        context.LogError("Validator", "incomplete");

        var snapshot = context.Messages;
        snapshot.Should().HaveCount(3);
        snapshot[0].Level.Should().Be(AgentMessageLevel.Info);
        snapshot[1].Level.Should().Be(AgentMessageLevel.Warning);
        snapshot[2].Level.Should().Be(AgentMessageLevel.Error);

        // Snapshot is detached: further writes do not mutate an already-returned list.
        context.LogInfo("CosmosAnalyzer", "more");
        snapshot.Should().HaveCount(3);
        context.Messages.Should().HaveCount(4);
    }

    [Fact]
    public void Results_record_and_lookup_by_name_and_role()
    {
        var context = NewContext();

        context.RecordResult(AgentResult.Succeeded("CosmosAnalyzer", AgentRole.CosmosAnalysis, TimeSpan.FromSeconds(1)));
        context.RecordResult(AgentResult.Failed("DataQuality", AgentRole.DataQuality, "boom", TimeSpan.FromSeconds(2)));

        context.GetResult("CosmosAnalyzer")!.Status.Should().Be(AgentRunStatus.Succeeded);
        context.GetResult("DataQuality")!.Status.Should().Be(AgentRunStatus.Failed);
        context.GetResult("Nope").Should().BeNull();

        context.HasSucceeded(AgentRole.CosmosAnalysis).Should().BeTrue();
        context.HasSucceeded(AgentRole.DataQuality).Should().BeFalse();
        context.HasSucceeded(AgentRole.SqlPlanning).Should().BeFalse();
    }

    [Fact]
    public void GetResult_returns_latest_recorded_for_name()
    {
        var context = NewContext();
        context.RecordResult(AgentResult.Skipped("Validator", AgentRole.Validation, "deps missing"));
        context.RecordResult(AgentResult.Succeeded("Validator", AgentRole.Validation, TimeSpan.Zero));

        context.GetResult("Validator")!.Status.Should().Be(AgentRunStatus.Succeeded);
    }

    [Fact]
    public void ToAssessmentResult_is_best_effort_with_defaults()
    {
        var context = NewContext();
        var result = context.ToAssessmentResult();

        result.DatabaseName.Should().Be("AppDb");
        result.CosmosAccountName.Should().Be("test-account");
        result.CosmosAnalysis.Should().NotBeNull();
        result.SqlAssessment.Should().NotBeNull();
        result.DataFactoryEstimate.Should().NotBeNull();
        result.DataQualityAnalysis.Should().BeNull();
    }

    [Fact]
    public void ToAssessmentResult_carries_committed_outputs()
    {
        var context = NewContext();
        var cosmos = new CosmosDbAnalysis();
        var quality = new DataQualityAnalysis();
        context.SetCosmosAnalysis("CosmosAnalyzer", cosmos);
        context.SetDataQualityAnalysis("DataQuality", quality);

        var result = context.ToAssessmentResult();

        result.CosmosAnalysis.Should().BeSameAs(cosmos);
        result.DataQualityAnalysis.Should().BeSameAs(quality);
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_messages_or_results()
    {
        var context = NewContext();
        const int writers = 16;
        const int perWriter = 250;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                context.LogInfo($"agent-{w}", $"msg-{i}");
                context.RecordResult(AgentResult.Succeeded($"agent-{w}-{i}", AgentRole.CosmosAnalysis, TimeSpan.Zero));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        context.Messages.Should().HaveCount(writers * perWriter);
        context.Results.Should().HaveCount(writers * perWriter);
    }

    [Fact]
    public async Task Concurrent_distinct_output_writes_all_succeed()
    {
        var context = NewContext();

        await Task.WhenAll(
            Task.Run(() => context.SetCosmosAnalysis("CosmosAnalyzer", new CosmosDbAnalysis())),
            Task.Run(() => context.SetSqlAssessment("SqlPlanner", new SqlMigrationAssessment())),
            Task.Run(() => context.SetDataQualityAnalysis("DataQuality", new DataQualityAnalysis())),
            Task.Run(() => context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate())));

        context.IsCompleteForAssessmentResult().Should().BeTrue();
        context.HasDataQualityAnalysis.Should().BeTrue();
    }
}
