using CosmosToSqlAssessment.Models;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Agent that cross-checks the outputs produced by the other agents and emits a typed
/// <see cref="Agents.ValidationReport"/> describing whether the run is complete and internally consistent.
/// </summary>
/// <remarks>
/// <para>
/// The validator <strong>never skips</strong>: it always runs so it can flag whatever is missing, and is
/// scheduled <em>last</em> by the orchestrator (after the optional data-quality agent and the derived Data
/// Factory step). Its <see cref="Dependencies"/> list only the agent-backed required producers so a
/// dependency-ordering scheduler places it after them; it does not depend on the optional data-quality
/// output (which may legitimately never be produced).
/// </para>
/// <para>
/// Finding a problem is a successful validation, not a failure: the agent records
/// <see cref="AgentRunStatus.Succeeded"/> as long as it produced a report, even when that report is not
/// acceptable. Only a genuine execution fault makes the agent fail — and even then the validator hardens
/// its cross-checks so a malformed upstream output becomes a finding rather than a crash, ensuring a report
/// is (almost) always emitted. The orchestrator/CLI derive the process exit code from
/// <see cref="Agents.ValidationReport.IsAcceptable"/>.
/// </para>
/// </remarks>
public sealed class ValidatorAgent : AssessmentAgentBase
{
    /// <summary>The stable name of this agent.</summary>
    public const string AgentName = "Validator";

    /// <summary>Check code: a required domain output is missing.</summary>
    public const string CheckMissingRequiredOutput = "MissingRequiredOutput";

    /// <summary>Check code: an agent recorded a failed result.</summary>
    public const string CheckAgentFailed = "AgentFailed";

    /// <summary>Check code: optional data-quality analysis was not available.</summary>
    public const string CheckDataQualityUnavailable = "DataQualityUnavailable";

    /// <summary>Check code: optional data-quality analysis was available.</summary>
    public const string CheckDataQualityAvailable = "DataQualityAvailable";

    /// <summary>Check code: a Cosmos container has no corresponding SQL mapping.</summary>
    public const string CheckUnmappedCosmosContainer = "UnmappedCosmosContainer";

    /// <summary>Check code: a SQL container mapping has a null/empty source container name.</summary>
    public const string CheckMalformedSqlMapping = "MalformedSqlMapping";

    /// <summary>Check code: the database metrics container count disagrees with the analyzed container list.</summary>
    public const string CheckContainerCountMismatch = "ContainerCountMismatch";

    /// <summary>Check code: an unexpected error occurred inside the validator's cross-checks.</summary>
    public const string CheckValidatorInternalError = "ValidatorInternalError";

    private static readonly IReadOnlyCollection<AgentRole> _dependencies =
        new[] { AgentRole.CosmosAnalysis, AgentRole.SqlPlanning };

    private readonly ILogger<ValidatorAgent> _logger;

    /// <summary>
    /// Creates a new <see cref="ValidatorAgent"/>.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public ValidatorAgent(ILogger<ValidatorAgent> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Name => AgentName;

    /// <inheritdoc />
    public override AgentRole Role => AgentRole.Validation;

    /// <inheritdoc />
    public override IReadOnlyCollection<AgentRole> Dependencies => _dependencies;

    /// <inheritdoc />
    protected override Task ExecuteAsync(ISharedAssessmentContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ValidatorAgent cross-checking outputs for database {DatabaseName}", context.DatabaseName);

        var findings = new List<ValidationFinding>();

        // --- Completeness: required outputs that were never produced. ---
        var missing = context.GetMissingRequiredOutputs();
        foreach (var output in missing)
        {
            var detail = $"Required output '{output}' was not produced.";
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Completeness, AgentMessageLevel.Error, CheckMissingRequiredOutput, detail));
            context.LogError(Name, detail);
        }

        // --- Diagnostic: agents that failed (informational; completeness stays output-based). ---
        var failedAgents = context.Results
            .Where(r => r.Status == AgentRunStatus.Failed)
            .Select(r => r.AgentName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var agentName in failedAgents)
        {
            var detail = $"Agent '{agentName}' reported a failure; downstream outputs it owns may be absent.";
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Diagnostic, AgentMessageLevel.Warning, CheckAgentFailed, detail));
            context.LogWarning(Name, detail);
        }

        // --- Diagnostic: optional data quality. Never affects the verdict. ---
        var dataQualityAvailable = context.HasDataQualityAnalysis;
        if (dataQualityAvailable)
        {
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Diagnostic, AgentMessageLevel.Info, CheckDataQualityAvailable,
                "Optional data-quality analysis is available."));
        }
        else
        {
            var detail = "Optional data-quality analysis was not produced; this does not affect run acceptability.";
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Diagnostic, AgentMessageLevel.Info, CheckDataQualityUnavailable, detail));
            context.LogInfo(Name, detail);
        }

        // --- Consistency: cross-check Cosmos <-> SQL coverage. Hardened against malformed shapes. ---
        try
        {
            CrossCheckCosmosToSql(context, findings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A malformed upstream output must not turn the validator into a crashed agent: record it as a
            // finding and still emit a report so the incompleteness/inconsistency is surfaced cleanly.
            var detail = $"Validator cross-check raised {ex.GetType().Name}: {ex.Message}";
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Consistency, AgentMessageLevel.Error, CheckValidatorInternalError, detail));
            context.LogError(Name, detail);
        }

        var isComplete = missing.Count == 0;
        var isConsistent = !findings.Any(f =>
            f.Category == ValidationFindingCategory.Consistency && f.Level == AgentMessageLevel.Error);

        var report = new ValidationReport
        {
            IsComplete = isComplete,
            IsConsistent = isConsistent,
            MissingRequiredOutputs = missing,
            FailedAgents = failedAgents,
            Findings = findings,
            DataQualityAvailable = dataQualityAvailable
        };

        context.LogInfo(Name,
            $"Validation complete: acceptable={report.IsAcceptable} (complete={isComplete}, consistent={isConsistent}); " +
            $"{findings.Count} finding(s), {missing.Count} missing required output(s), {failedAgents.Length} failed agent(s).");

        // Commit is the final action so a later failure never leaves the context with a partial report.
        context.SetValidationReport(Name, report);
        return Task.CompletedTask;
    }

    private void CrossCheckCosmosToSql(ISharedAssessmentContext context, List<ValidationFinding> findings)
    {
        var cosmos = context.CosmosAnalysis;
        var sql = context.SqlAssessment;
        if (cosmos is null || sql is null)
        {
            // Absence is already captured as a completeness finding; there is nothing to cross-check.
            return;
        }

        var containers = cosmos.Containers ?? new List<ContainerAnalysis>();

        // Build the set of source containers covered by the SQL plan (null-safe at every level).
        var mappedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var databaseMapping in sql.DatabaseMappings ?? new List<DatabaseMapping>())
        {
            foreach (var containerMapping in databaseMapping.ContainerMappings ?? new List<ContainerMapping>())
            {
                var source = containerMapping.SourceContainer;
                if (string.IsNullOrWhiteSpace(source))
                {
                    var detail = "A SQL container mapping has a null or empty source container name.";
                    findings.Add(ValidationFinding.Create(
                        ValidationFindingCategory.Consistency, AgentMessageLevel.Error, CheckMalformedSqlMapping, detail));
                    context.LogError(Name, detail);
                    continue;
                }

                mappedSources.Add(source);
            }
        }

        foreach (var container in containers)
        {
            var name = container?.ContainerName;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!mappedSources.Contains(name))
            {
                var detail = $"Cosmos container '{name}' has no corresponding SQL table mapping.";
                findings.Add(ValidationFinding.Create(
                    ValidationFindingCategory.Consistency, AgentMessageLevel.Error, CheckUnmappedCosmosContainer, detail));
                context.LogError(Name, detail);
            }
        }

        // Soft consistency signal: the reported container count vs. the analyzed list. Warning-only because
        // the metric may be an approximation in some sources, so it must not flip IsConsistent on its own.
        var reportedCount = cosmos.DatabaseMetrics?.ContainerCount ?? containers.Count;
        if (reportedCount != containers.Count)
        {
            var detail = $"DatabaseMetrics.ContainerCount ({reportedCount}) differs from the number of analyzed containers ({containers.Count}).";
            findings.Add(ValidationFinding.Create(
                ValidationFindingCategory.Consistency, AgentMessageLevel.Warning, CheckContainerCountMismatch, detail));
            context.LogWarning(Name, detail);
        }
    }
}
