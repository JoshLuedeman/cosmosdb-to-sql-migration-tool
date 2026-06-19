using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Tests.EndToEnd;
using CosmosToSqlAssessment.Tests.Mocks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="CosmosAnalyzerAgent"/> and the shared <see cref="AssessmentAgentBase"/>
/// failure-isolation behaviour (#211). Each agent is exercised in isolation.
/// </summary>
public class CosmosAnalyzerAgentTests
{
    private static CosmosAnalyzerAgent NewAgent(CosmosDbAnalysisService service) =>
        new(service, NullLogger<CosmosAnalyzerAgent>.Instance);

    [Fact]
    public void Metadata_is_stable()
    {
        using var fixture = new E2EFixture().WithDatabase("AppDb").Build();
        var agent = NewAgent(fixture.CosmosService);

        agent.Name.Should().Be("CosmosAnalyzer");
        agent.Role.Should().Be(AgentRole.CosmosAnalysis);
        agent.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_analyzes_database_and_commits_to_context()
    {
        using var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId").WithThroughput(800)
                    .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
            .Build();

        var context = new SharedAssessmentContext("AppDb", "test-account");
        var agent = NewAgent(fixture.CosmosService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded);
        result.Role.Should().Be(AgentRole.CosmosAnalysis);
        context.HasCosmosAnalysis.Should().BeTrue();
        context.CosmosAnalysis!.Containers.Should().HaveCount(2);
        context.GetResult("CosmosAnalyzer")!.Status.Should().Be(AgentRunStatus.Succeeded);
        context.Messages.Should().Contain(m => m.Text.Contains("Analyzed 2 container"));
    }

    private static CosmosDbAnalysisService ThrowingService(Exception toThrow)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var clientMock = new Mock<CosmosClient>();
        clientMock.Setup(c => c.GetDatabase(It.IsAny<string>())).Throws(toThrow);
        return new CosmosDbAnalysisService(config, NullLogger<CosmosDbAnalysisService>.Instance, clientMock.Object);
    }

    [Fact]
    public async Task Run_isolates_failure_without_throwing()
    {
        var service = ThrowingService(new InvalidOperationException("simulated cosmos outage"));
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var agent = NewAgent(service);

        // Must NOT throw — failure isolation.
        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.Error.Should().Contain("simulated cosmos outage");
        result.Error.Should().Contain("InvalidOperationException");
        context.HasCosmosAnalysis.Should().BeFalse();
        context.Messages.Should().Contain(m => m.Level == AgentMessageLevel.Error);
    }

    [Fact]
    public async Task Run_propagates_cancellation_and_records_no_failure()
    {
        var service = ThrowingService(new OperationCanceledException());
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var agent = NewAgent(service);

        var act = async () => await agent.RunAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        // Cancellation is not an agent failure: no Failed result recorded.
        context.GetResult("CosmosAnalyzer").Should().BeNull();
    }
}
