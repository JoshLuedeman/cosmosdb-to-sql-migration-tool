using CosmosToSqlAssessment.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.EndToEnd;

/// <summary>
/// End-to-end equivalence tests for the agentic pipeline (#215): the <see cref="AgentOrchestrator"/> driving
/// the real production services (via the mock harness) must produce output equivalent to the single-pass
/// <see cref="E2EFixture.RunAssessmentAsync"/> baseline. This is the acceptance regression for parent #131.
/// </summary>
public class AgentEquivalenceTests
{
    private static E2EFixture BuildFixture() => new E2EFixture()
        .WithDatabase("AppDb", db => db
            .WithContainer("users", c => c
                .WithPartitionKey("/userId").WithThroughput(400)
                .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
            .WithContainer("orders", c => c
                .WithPartitionKey("/orderId").WithThroughput(800)
                .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
        .Build();

    private static List<IAssessmentAgent> AgentsFor(E2EFixture fixture, bool includeDataQuality = true)
    {
        var agents = new List<IAssessmentAgent>
        {
            new CosmosAnalyzerAgent(fixture.CosmosService, NullLogger<CosmosAnalyzerAgent>.Instance),
            new SqlPlannerAgent(fixture.SqlAssessmentService, NullLogger<SqlPlannerAgent>.Instance),
            new DataFactoryEstimatorAgent(fixture.DataFactoryService, NullLogger<DataFactoryEstimatorAgent>.Instance),
            new ValidatorAgent(NullLogger<ValidatorAgent>.Instance)
        };

        if (includeDataQuality)
        {
            agents.Add(new DataQualityAgent(fixture.DataQualityService, NullLogger<DataQualityAgent>.Instance));
        }

        return agents;
    }

    private static AgentOrchestrator OrchestratorFor(E2EFixture fixture, bool includeDataQuality = true) =>
        new(AgentsFor(fixture, includeDataQuality), NullLogger<AgentOrchestrator>.Instance);

    [Fact]
    public async Task Agentic_sequential_mode_is_equivalent_to_single_pass()
    {
        // Two independent fixtures with identical sample data so each path reads fresh mocks.
        using var baselineFixture = BuildFixture();
        using var agenticFixture = BuildFixture();

        var baseline = await baselineFixture.RunAssessmentAsync("AppDb");

        var run = await OrchestratorFor(agenticFixture).RunAsync(
            "AppDb", "test-account",
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Sequential });

        run.AssessmentResult.Should().BeEquivalentTo(baseline, opts => opts.Excluding(m =>
            m.Name == "AssessmentId" ||
            m.Name == "AssessmentDate" ||
            m.Name == "AnalysisDate" ||
            m.Name == "IssueId"));
    }

    [Fact]
    public async Task Agentic_parallel_mode_is_equivalent_to_single_pass()
    {
        using var baselineFixture = BuildFixture();
        using var agenticFixture = BuildFixture();

        var baseline = await baselineFixture.RunAssessmentAsync("AppDb");

        var run = await OrchestratorFor(agenticFixture).RunAsync(
            "AppDb", "test-account",
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Parallel });

        run.AssessmentResult.Should().BeEquivalentTo(baseline, opts => opts.Excluding(m =>
            m.Name == "AssessmentId" ||
            m.Name == "AssessmentDate" ||
            m.Name == "AnalysisDate" ||
            m.Name == "IssueId"));
    }

    [Fact]
    public async Task Complete_run_is_acceptable_and_maps_every_container()
    {
        using var fixture = BuildFixture();

        var run = await OrchestratorFor(fixture).RunAsync("AppDb", "test-account");

        run.IsAcceptable.Should().BeTrue();
        run.Validation.Should().NotBeNull();
        run.Validation!.IsComplete.Should().BeTrue();
        run.Validation.IsConsistent.Should().BeTrue();
        run.Validation.DataQualityAvailable.Should().BeTrue();
        // Both containers were produced and mapped (no unmapped-container findings).
        run.Validation.Findings.Should().NotContain(f => f.Check == ValidatorAgent.CheckUnmappedCosmosContainer);
        run.AssessmentResult.CosmosAnalysis.Containers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_without_optional_data_quality_agent_is_still_acceptable()
    {
        using var fixture = BuildFixture();

        var run = await OrchestratorFor(fixture, includeDataQuality: false).RunAsync("AppDb", "test-account");

        run.IsAcceptable.Should().BeTrue();             // data quality is optional
        run.Validation!.DataQualityAvailable.Should().BeFalse();
        run.AssessmentResult.DataQualityAnalysis.Should().BeNull();
        // Required outputs are all present.
        run.Validation.MissingRequiredOutputs.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_agent_records_a_result_for_observability()
    {
        using var fixture = BuildFixture();

        var run = await OrchestratorFor(fixture).RunAsync("AppDb", "test-account");

        run.AgentResults.Select(r => r.AgentName).Should().Contain(new[]
        {
            CosmosAnalyzerAgent.AgentName,
            SqlPlannerAgent.AgentName,
            DataQualityAgent.AgentName,
            DataFactoryEstimatorAgent.AgentName,
            ValidatorAgent.AgentName
        });
        run.AgentResults.Should().OnlyContain(r => r.Status == AgentRunStatus.Succeeded);
    }
}
