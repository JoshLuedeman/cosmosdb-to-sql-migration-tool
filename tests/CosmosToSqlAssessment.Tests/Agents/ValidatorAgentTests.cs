using CosmosToSqlAssessment.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="ValidatorAgent"/> (#214): completeness, consistency cross-checks,
/// diagnostic-only handling of optional/failed outputs, hardening, and the write-once verdict.
/// </summary>
public class ValidatorAgentTests
{
    private static ValidatorAgent NewAgent() => new(NullLogger<ValidatorAgent>.Instance);

    private static CosmosDbAnalysis Cosmos(params string[] containerNames) => new()
    {
        DatabaseMetrics = new DatabaseMetrics { ContainerCount = containerNames.Length },
        Containers = containerNames.Select(n => new ContainerAnalysis { ContainerName = n }).ToList()
    };

    private static SqlMigrationAssessment Sql(params string[] sourceContainers) => new()
    {
        DatabaseMappings = new List<DatabaseMapping>
        {
            new()
            {
                ContainerMappings = sourceContainers
                    .Select(c => new ContainerMapping { SourceContainer = c, TargetTable = c })
                    .ToList()
            }
        }
    };

    private static SharedAssessmentContext FullyPopulated()
    {
        var context = new SharedAssessmentContext("AppDb", "test-account");
        context.SetCosmosAnalysis("CosmosAnalyzer", Cosmos("users"));
        context.SetSqlAssessment("SqlPlanner", Sql("users"));
        context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate());
        return context;
    }

    [Fact]
    public void Metadata_is_stable_and_does_not_depend_on_optional_data_quality()
    {
        var agent = NewAgent();

        agent.Name.Should().Be("Validator");
        agent.Role.Should().Be(AgentRole.Validation);
        agent.Dependencies.Should().BeEquivalentTo(new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning });
        agent.Dependencies.Should().NotContain(AgentRole.DataQuality);
    }

    [Fact]
    public async Task Acceptable_when_all_required_outputs_present_and_consistent()
    {
        var context = FullyPopulated();

        var result = await NewAgent().RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded);
        context.HasValidationReport.Should().BeTrue();
        var report = context.ValidationReport!;
        report.IsComplete.Should().BeTrue();
        report.IsConsistent.Should().BeTrue();
        report.IsAcceptable.Should().BeTrue();
        report.MissingRequiredOutputs.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_required_output_flags_incomplete_but_agent_still_succeeds()
    {
        var context = new SharedAssessmentContext("AppDb", "test-account");
        context.SetCosmosAnalysis("CosmosAnalyzer", Cosmos("users"));
        // No SqlAssessment, no DataFactoryEstimate.

        var result = await NewAgent().RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded); // finding problems is a successful validation
        var report = context.ValidationReport!;
        report.IsComplete.Should().BeFalse();
        report.IsAcceptable.Should().BeFalse();
        report.MissingRequiredOutputs.Should().Contain("SqlAssessment").And.Contain("DataFactoryEstimate");
        report.Findings.Should().Contain(f =>
            f.Check == ValidatorAgent.CheckMissingRequiredOutput && f.Level == AgentMessageLevel.Error);
    }

    [Fact]
    public async Task Unmapped_cosmos_container_makes_run_inconsistent()
    {
        var context = new SharedAssessmentContext("AppDb", "test-account");
        context.SetCosmosAnalysis("CosmosAnalyzer", Cosmos("users", "orders"));
        context.SetSqlAssessment("SqlPlanner", Sql("users")); // 'orders' has no mapping
        context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate());

        await NewAgent().RunAsync(context);

        var report = context.ValidationReport!;
        report.IsComplete.Should().BeTrue();
        report.IsConsistent.Should().BeFalse();
        report.IsAcceptable.Should().BeFalse();
        report.Findings.Should().Contain(f =>
            f.Check == ValidatorAgent.CheckUnmappedCosmosContainer
            && f.Category == ValidationFindingCategory.Consistency
            && f.Level == AgentMessageLevel.Error);
    }

    [Fact]
    public async Task Failed_agent_and_missing_optional_data_quality_are_diagnostic_only()
    {
        var context = FullyPopulated();
        context.RecordResult(AgentResult.Failed("SomeOptionalAgent", AgentRole.DataQuality, "boom", TimeSpan.Zero));

        await NewAgent().RunAsync(context);

        var report = context.ValidationReport!;
        report.FailedAgents.Should().Contain("SomeOptionalAgent");
        report.DataQualityAvailable.Should().BeFalse();
        // Diagnostic findings must not flip the verdict.
        report.IsAcceptable.Should().BeTrue();
        report.Findings.Should().Contain(f => f.Check == ValidatorAgent.CheckDataQualityUnavailable);
        report.Findings.Should().Contain(f => f.Check == ValidatorAgent.CheckAgentFailed);
    }

    [Fact]
    public async Task Reports_data_quality_available_when_present()
    {
        var context = FullyPopulated();
        context.SetDataQualityAnalysis("DataQuality", new DataQualityAnalysis());

        await NewAgent().RunAsync(context);

        var report = context.ValidationReport!;
        report.DataQualityAvailable.Should().BeTrue();
        report.Findings.Should().Contain(f => f.Check == ValidatorAgent.CheckDataQualityAvailable);
    }

    [Fact]
    public async Task Hardened_against_null_collections_and_still_emits_report()
    {
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var cosmos = new CosmosDbAnalysis
        {
            DatabaseMetrics = new DatabaseMetrics { ContainerCount = 1 },
            Containers = new List<ContainerAnalysis> { new() { ContainerName = "users" } }
        };
        var sql = new SqlMigrationAssessment { DatabaseMappings = null! }; // malformed: null mappings
        context.SetCosmosAnalysis("CosmosAnalyzer", cosmos);
        context.SetSqlAssessment("SqlPlanner", sql);
        context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate());

        var result = await NewAgent().RunAsync(context);

        result.Status.Should().Be(AgentRunStatus.Succeeded); // no crash
        context.HasValidationReport.Should().BeTrue();
        var report = context.ValidationReport!;
        // 'users' is unmapped because there were no mappings at all -> consistency error, not a crash.
        report.IsConsistent.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Check == ValidatorAgent.CheckUnmappedCosmosContainer);
    }

    [Fact]
    public async Task Container_count_mismatch_is_a_soft_warning_only()
    {
        var context = new SharedAssessmentContext("AppDb", "test-account");
        var cosmos = new CosmosDbAnalysis
        {
            DatabaseMetrics = new DatabaseMetrics { ContainerCount = 5 }, // disagrees with the 1 analyzed
            Containers = new List<ContainerAnalysis> { new() { ContainerName = "users" } }
        };
        context.SetCosmosAnalysis("CosmosAnalyzer", cosmos);
        context.SetSqlAssessment("SqlPlanner", Sql("users"));
        context.SetDataFactoryEstimate("Orchestrator", new DataFactoryEstimate());

        await NewAgent().RunAsync(context);

        var report = context.ValidationReport!;
        report.Findings.Should().Contain(f =>
            f.Check == ValidatorAgent.CheckContainerCountMismatch && f.Level == AgentMessageLevel.Warning);
        // Warning-level consistency findings must not make the run inconsistent on their own.
        report.IsConsistent.Should().BeTrue();
        report.IsAcceptable.Should().BeTrue();
    }

    [Fact]
    public async Task Validation_is_single_run_per_context()
    {
        var context = FullyPopulated();
        var agent = NewAgent();

        var first = await agent.RunAsync(context);
        var second = await agent.RunAsync(context);

        first.Status.Should().Be(AgentRunStatus.Succeeded);
        // Second run hits the write-once guard on SetValidationReport -> isolated as a failed result.
        second.Status.Should().Be(AgentRunStatus.Failed);
        second.Error.Should().Contain("InvalidOperationException");
    }
}
