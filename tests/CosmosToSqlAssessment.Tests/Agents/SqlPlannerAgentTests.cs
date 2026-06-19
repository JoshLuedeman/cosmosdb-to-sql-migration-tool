using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Tests.EndToEnd;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="SqlPlannerAgent"/> (#212), exercised in isolation against the mock harness.
/// </summary>
public class SqlPlannerAgentTests
{
    private static SqlPlannerAgent NewAgent(SqlMigrationAssessmentService service) =>
        new(service, NullLogger<SqlPlannerAgent>.Instance);

    private static async Task<(E2EFixture Fixture, SharedAssessmentContext Context)> SeededAsync()
    {
        var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(800)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .Build();

        var context = new SharedAssessmentContext("AppDb", "test-account");
        var cosmos = await fixture.CosmosService.AnalyzeDatabaseAsync("AppDb");
        context.SetCosmosAnalysis("CosmosAnalyzer", cosmos);
        return (fixture, context);
    }

    [Fact]
    public void Metadata_is_stable()
    {
        using var fixture = new E2EFixture().WithDatabase("AppDb").Build();
        var agent = NewAgent(fixture.SqlAssessmentService);

        agent.Name.Should().Be("SqlPlanner");
        agent.Role.Should().Be(AgentRole.SqlPlanning);
        agent.Dependencies.Should().BeEquivalentTo(new[] { AgentRole.CosmosAnalysis });
    }

    [Fact]
    public async Task Run_plans_migration_when_cosmos_analysis_present()
    {
        var (fixture, context) = await SeededAsync();
        using var _ = fixture;
        var agent = NewAgent(fixture.SqlAssessmentService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded);
        context.HasSqlAssessment.Should().BeTrue();
        context.SqlAssessment!.DatabaseMappings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Run_skips_when_cosmos_analysis_missing()
    {
        using var fixture = new E2EFixture().WithDatabase("AppDb").Build();
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var agent = NewAgent(fixture.SqlAssessmentService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Skipped);
        result.Error.Should().Contain("Cosmos analysis is not available");
        context.HasSqlAssessment.Should().BeFalse();
        context.GetResult("SqlPlanner")!.Status.Should().Be(AgentRunStatus.Skipped);
    }

    [Fact]
    public async Task Run_isolates_failure_without_throwing()
    {
        var (fixture, context) = await SeededAsync();
        using var _ = fixture;
        // Pre-set the SQL assessment so the agent's final write-once commit throws,
        // deterministically exercising the base failure-isolation path through the real agent.
        context.SetSqlAssessment("Intruder", new SqlMigrationAssessment());
        var agent = NewAgent(fixture.SqlAssessmentService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.Error.Should().Contain("InvalidOperationException");
        context.Messages.Should().Contain(m => m.Level == AgentMessageLevel.Error);
    }
}
