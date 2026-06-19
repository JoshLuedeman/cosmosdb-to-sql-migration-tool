using CosmosToSqlAssessment.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Scheduling/coordination tests for <see cref="AgentOrchestrator"/> (#215) using lightweight fake agents,
/// so the engine (ordering, waves, failure isolation, conditional skip, timeout, graph validation) is tested
/// independently of the real services. Equivalence and real-service behaviour live in the end-to-end tests.
/// </summary>
public class AgentOrchestratorTests
{
    private sealed class FakeAgent : AssessmentAgentBase
    {
        private readonly List<string> _log;
        private readonly Func<CancellationToken, Task>? _body;
        private readonly string? _skipReason;

        public FakeAgent(
            string name,
            AgentRole role,
            IReadOnlyCollection<AgentRole> dependencies,
            List<string> log,
            Func<CancellationToken, Task>? body = null,
            string? skipReason = null)
        {
            Name = name;
            Role = role;
            Dependencies = dependencies;
            _log = log;
            _body = body;
            _skipReason = skipReason;
        }

        public override string Name { get; }
        public override AgentRole Role { get; }
        public override IReadOnlyCollection<AgentRole> Dependencies { get; }

        protected override string? GetSkipReason(ISharedAssessmentContext context) => _skipReason;

        protected override async Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
        {
            lock (_log)
            {
                _log.Add(Name);
            }

            if (_body is not null)
            {
                await _body(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static AgentOrchestrator NewOrchestrator(IEnumerable<IAssessmentAgent> agents) =>
        new(agents, NullLogger<AgentOrchestrator>.Instance);

    private static List<IAssessmentAgent> StandardAgents(
        List<string> log,
        Func<CancellationToken, Task>? cosmosBody = null)
    {
        return new List<IAssessmentAgent>
        {
            new FakeAgent("Cosmos", AgentRole.CosmosAnalysis, Array.Empty<AgentRole>(), log, cosmosBody),
            new FakeAgent("Sql", AgentRole.SqlPlanning, new[] { AgentRole.CosmosAnalysis }, log),
            new FakeAgent("DataQuality", AgentRole.DataQuality, new[] { AgentRole.CosmosAnalysis }, log),
            new FakeAgent("DataFactory", AgentRole.DataFactoryEstimation, new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning }, log),
            new FakeAgent("Validator", AgentRole.Validation, new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning }, log)
        };
    }

    [Fact]
    public async Task Sequential_runs_in_dependency_priority_order()
    {
        var log = new List<string>();
        var orchestrator = NewOrchestrator(StandardAgents(log));

        await orchestrator.RunAsync("AppDb", "acct",
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Sequential });

        log.Should().Equal("Cosmos", "Sql", "DataQuality", "DataFactory", "Validator");
    }

    [Fact]
    public async Task Parallel_runs_waves_with_cosmos_first_and_validator_last()
    {
        var log = new List<string>();
        var orchestrator = NewOrchestrator(StandardAgents(log));

        await orchestrator.RunAsync("AppDb", "acct",
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Parallel });

        log.Should().HaveCount(5);
        log[0].Should().Be("Cosmos");                       // wave 0
        log.IndexOf("Sql").Should().BeLessThan(log.IndexOf("DataFactory")); // DataFactory waits for Sql
        log.IndexOf("Cosmos").Should().BeLessThan(log.IndexOf("Sql"));
        log[^1].Should().Be("Validator");                   // validator strictly last
    }

    [Fact]
    public async Task Failure_isolation_lets_other_agents_continue()
    {
        var log = new List<string>();
        var agents = StandardAgents(log);
        agents[1] = new FakeAgent("Sql", AgentRole.SqlPlanning, new[] { AgentRole.CosmosAnalysis }, log,
            _ => throw new InvalidOperationException("sql boom"));
        var orchestrator = NewOrchestrator(agents);

        var result = await orchestrator.RunAsync("AppDb", "acct");

        result.AgentResults.Single(r => r.AgentName == "Sql").Status.Should().Be(AgentRunStatus.Failed);
        result.AgentResults.Single(r => r.AgentName == "Cosmos").Status.Should().Be(AgentRunStatus.Succeeded);
        // Other agents still ran despite the SQL failure.
        log.Should().Contain("DataFactory").And.Contain("DataQuality").And.Contain("Validator");
    }

    [Fact]
    public async Task Conditional_skips_dependents_when_a_dependency_fails()
    {
        var log = new List<string>();
        var agents = StandardAgents(log, cosmosBody: _ => throw new InvalidOperationException("cosmos boom"));
        var orchestrator = NewOrchestrator(agents);

        var result = await orchestrator.RunAsync("AppDb", "acct",
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Conditional });

        result.AgentResults.Single(r => r.AgentName == "Cosmos").Status.Should().Be(AgentRunStatus.Failed);
        foreach (var dependent in new[] { "Sql", "DataQuality", "DataFactory" })
        {
            var r = result.AgentResults.Single(x => x.AgentName == dependent);
            r.Status.Should().Be(AgentRunStatus.Skipped);
            r.Error.Should().Contain("Conditional skip");
        }
        // Conditionally-skipped producers never executed; the Validator never skips so it still runs last.
        log.Should().Equal("Cosmos", "Validator");
    }

    [Fact]
    public async Task Per_agent_timeout_records_a_failed_result_and_continues()
    {
        var log = new List<string>();
        var agents = StandardAgents(log,
            cosmosBody: ct => Task.Delay(TimeSpan.FromSeconds(30), ct));
        var orchestrator = NewOrchestrator(agents);

        var result = await orchestrator.RunAsync("AppDb", "acct",
            new AgentOrchestrationOptions
            {
                Mode = AgentExecutionMode.Sequential,
                PerAgentTimeout = TimeSpan.FromMilliseconds(150)
            });

        var cosmos = result.AgentResults.Single(r => r.AgentName == "Cosmos");
        cosmos.Status.Should().Be(AgentRunStatus.Failed);
        cosmos.Error.Should().Contain("Timed out");
        // Run continued past the timed-out agent.
        result.AgentResults.Should().Contain(r => r.AgentName == "Validator");
    }

    [Fact]
    public async Task Global_cancellation_propagates()
    {
        var log = new List<string>();
        var agents = StandardAgents(log,
            cosmosBody: ct => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var orchestrator = NewOrchestrator(agents);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await orchestrator.RunAsync("AppDb", "acct", null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_rejects_duplicate_role()
    {
        var log = new List<string>();
        var agents = StandardAgents(log);
        agents.Add(new FakeAgent("Cosmos2", AgentRole.CosmosAnalysis, Array.Empty<AgentRole>(), log));

        var act = () => NewOrchestrator(agents);

        act.Should().Throw<InvalidOperationException>().WithMessage("*share role*");
    }

    [Fact]
    public void Constructor_rejects_duplicate_name()
    {
        var log = new List<string>();
        var agents = StandardAgents(log);
        // Same name, different (also duplicate-free? no) — use a fresh role to isolate the name check.
        agents[2] = new FakeAgent("Cosmos", AgentRole.DataQuality, new[] { AgentRole.CosmosAnalysis }, log);

        var act = () => NewOrchestrator(agents);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public void Constructor_rejects_missing_required_role()
    {
        var log = new List<string>();
        var agents = StandardAgents(log);
        agents.RemoveAll(a => a.Role == AgentRole.DataFactoryEstimation);

        var act = () => NewOrchestrator(agents);

        act.Should().Throw<InvalidOperationException>().WithMessage("*required agent role*");
    }

    [Fact]
    public void Constructor_rejects_dangling_dependency()
    {
        var log = new List<string>();
        var agents = StandardAgents(log);
        // Validator depends on a role nobody produces.
        agents[4] = new FakeAgent("Validator", AgentRole.Validation, new[] { AgentRole.Orchestration }, log);

        var act = () => NewOrchestrator(agents);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no registered agent produces*");
    }
}
