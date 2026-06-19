using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Tests.EndToEnd;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="DataQualityAgent"/> (#213), exercised in isolation against the mock harness.
/// </summary>
public class DataQualityAgentTests
{
    private static DataQualityAgent NewAgent(DataQualityAnalysisService service) =>
        new(service, NullLogger<DataQualityAgent>.Instance);

    private static async Task<(E2EFixture Fixture, SharedAssessmentContext Context)> SeededAsync()
    {
        var fixture = new E2EFixture()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId").WithThroughput(400)
                    .WithDocuments(E2ESampleData.TwoUsers.ToArray())))
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
        var agent = NewAgent(fixture.DataQualityService);

        agent.Name.Should().Be("DataQuality");
        agent.Role.Should().Be(AgentRole.DataQuality);
        agent.Dependencies.Should().BeEquivalentTo(new[] { AgentRole.CosmosAnalysis });
    }

    [Fact]
    public async Task Run_analyzes_quality_when_cosmos_analysis_present()
    {
        var (fixture, context) = await SeededAsync();
        using var _ = fixture;
        var agent = NewAgent(fixture.DataQualityService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded);
        context.HasDataQualityAnalysis.Should().BeTrue();
        context.DataQualityAnalysis.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_skips_when_cosmos_analysis_missing_and_stays_optional()
    {
        using var fixture = new E2EFixture().WithDatabase("AppDb").Build();
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var agent = NewAgent(fixture.DataQualityService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Skipped);
        result.Error.Should().Contain("Cosmos analysis is not available");
        context.HasDataQualityAnalysis.Should().BeFalse();
        // Optional output: a skipped data-quality run must NOT make the run incomplete.
        context.GetMissingRequiredOutputs().Should().NotContain("DataQualityAnalysis");
    }

    [Fact]
    public async Task Run_isolates_failure_without_throwing()
    {
        var (fixture, context) = await SeededAsync();
        using var _ = fixture;
        // Pre-set the data-quality output so the agent's final write-once commit throws,
        // deterministically exercising the base failure-isolation path through the real agent.
        context.SetDataQualityAnalysis("Intruder", new DataQualityAnalysis());
        var agent = NewAgent(fixture.DataQualityService);

        var result = await agent.RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Failed);
        result.Error.Should().Contain("InvalidOperationException");
    }
}
