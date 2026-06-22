using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Orchestration;

/// <summary>
/// Seam test for #248 proving that <see cref="UserInputs.AgenticMode"/> is actually threaded through
/// <see cref="AssessmentOrchestrator"/> into the <see cref="AgentOrchestrator"/>'s
/// <see cref="AgentOrchestrationOptions.Mode"/> — something the parser tests and the end-to-end
/// equivalence tests (which call <see cref="AgentOrchestrator.RunAsync"/> directly) cannot demonstrate.
///
/// <para>
/// The seam is observed via a behavioural difference: the Cosmos producer fails, so in
/// <see cref="AgentExecutionMode.Conditional"/> the downstream SQL producer is skipped (never invoked),
/// whereas in <see cref="AgentExecutionMode.Sequential"/> it is invoked regardless. Both runs end up
/// incomplete (the failed Cosmos analysis means a required output is missing), so the agentic path throws
/// <see cref="InvalidOperationException"/> in both cases — we catch that and assert on the recorded
/// invocation flag.
/// </para>
/// </summary>
public class AgenticModeWiringTests
{
    private sealed class RecordingAgent : AssessmentAgentBase
    {
        private readonly Action? _body;

        public RecordingAgent(
            string name,
            AgentRole role,
            IReadOnlyCollection<AgentRole> dependencies,
            Action? body = null)
        {
            Name = name;
            Role = role;
            Dependencies = dependencies;
            _body = body;
        }

        public override string Name { get; }
        public override AgentRole Role { get; }
        public override IReadOnlyCollection<AgentRole> Dependencies { get; }

        public bool WasInvoked { get; private set; }

        protected override Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            _body?.Invoke();
            return Task.CompletedTask;
        }
    }

    private static (AssessmentOrchestrator Orchestrator, IServiceProvider Provider, RecordingAgent Sql) BuildHarness()
    {
        var sql = new RecordingAgent("Sql", AgentRole.SqlPlanning, new[] { AgentRole.CosmosAnalysis });
        var agents = new List<IAssessmentAgent>
        {
            // Cosmos fails: its downstream dependents become skip candidates in Conditional mode.
            new RecordingAgent("Cosmos", AgentRole.CosmosAnalysis, Array.Empty<AgentRole>(),
                () => throw new InvalidOperationException("cosmos boom")),
            sql,
            new RecordingAgent("DataFactory", AgentRole.DataFactoryEstimation,
                new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning }),
            new RecordingAgent("Validator", AgentRole.Validation,
                new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning })
        };

        var agentOrchestrator = new AgentOrchestrator(agents, NullLogger<AgentOrchestrator>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var provider = new ServiceCollection()
            .AddSingleton(agentOrchestrator)
            .BuildServiceProvider();

        var orchestrator = new AssessmentOrchestrator(
            provider, configuration, NullLogger<AssessmentOrchestrator>.Instance);

        return (orchestrator, provider, sql);
    }

    [Fact]
    public async Task Conditional_mode_skips_the_sql_agent_when_cosmos_fails()
    {
        var (orchestrator, provider, sql) = BuildHarness();
        var userInputs = new UserInputs
        {
            AccountEndpoint = "https://fake.documents.azure.com:443/",
            UseAgentic = true,
            AgenticMode = AgentExecutionMode.Conditional
        };

        Func<Task> act = () => orchestrator.RunDatabaseAssessmentAsync(
            provider, userInputs, "db", CancellationToken.None);

        // Incomplete run (Cosmos failed) -> agentic path throws; the mode still reached the engine.
        await act.Should().ThrowAsync<InvalidOperationException>();
        sql.WasInvoked.Should().BeFalse("Conditional mode must skip a dependent whose dependency failed");
    }

    [Fact]
    public async Task Sequential_mode_still_invokes_the_sql_agent_when_cosmos_fails()
    {
        var (orchestrator, provider, sql) = BuildHarness();
        var userInputs = new UserInputs
        {
            AccountEndpoint = "https://fake.documents.azure.com:443/",
            UseAgentic = true,
            AgenticMode = AgentExecutionMode.Sequential
        };

        Func<Task> act = () => orchestrator.RunDatabaseAssessmentAsync(
            provider, userInputs, "db", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        sql.WasInvoked.Should().BeTrue("Sequential mode runs every agent regardless of upstream failure");
    }
}
