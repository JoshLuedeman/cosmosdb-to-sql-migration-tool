using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Thread-safe shared blackboard through which assessment agents communicate. Agents read the
/// outputs produced by upstream agents, contribute their own domain output exactly once, append
/// log messages, and record their run results. Implemented by <see cref="SharedAssessmentContext"/>.
/// </summary>
/// <remarks>
/// The contract is the stable surface later agents and orchestration layers depend on. All members
/// are safe to call concurrently. Collection-returning members return point-in-time snapshots, never
/// live views.
/// </remarks>
public interface ISharedAssessmentContext
{
    /// <summary>Name of the Cosmos DB database under assessment.</summary>
    string DatabaseName { get; }

    /// <summary>Friendly name of the Cosmos DB account under assessment.</summary>
    string CosmosAccountName { get; }

    /// <summary>The Cosmos DB analysis output, or <see langword="null"/> if not yet produced.</summary>
    CosmosDbAnalysis? CosmosAnalysis { get; }

    /// <summary>The SQL migration assessment output, or <see langword="null"/> if not yet produced.</summary>
    SqlMigrationAssessment? SqlAssessment { get; }

    /// <summary>The data-quality analysis output, or <see langword="null"/> if not produced (it is optional).</summary>
    DataQualityAnalysis? DataQualityAnalysis { get; }

    /// <summary>The Data Factory migration estimate, or <see langword="null"/> if not yet produced.</summary>
    DataFactoryEstimate? DataFactoryEstimate { get; }

    /// <summary>
    /// The validator's verdict, or <see langword="null"/> if validation has not run. This is run metadata,
    /// not a domain output: it is not part of <see cref="GetMissingRequiredOutputs"/> and does not feed
    /// <see cref="ToAssessmentResult"/>.
    /// </summary>
    ValidationReport? ValidationReport { get; }

    /// <summary>Whether <see cref="CosmosAnalysis"/> has been set.</summary>
    bool HasCosmosAnalysis { get; }

    /// <summary>Whether <see cref="SqlAssessment"/> has been set.</summary>
    bool HasSqlAssessment { get; }

    /// <summary>Whether <see cref="DataQualityAnalysis"/> has been set.</summary>
    bool HasDataQualityAnalysis { get; }

    /// <summary>Whether <see cref="DataFactoryEstimate"/> has been set.</summary>
    bool HasDataFactoryEstimate { get; }

    /// <summary>
    /// Whether <see cref="ValidationReport"/> has been set. <see langword="false"/> means validation did not
    /// run or crashed before producing a report (treat as an infrastructure failure, distinct from an
    /// unacceptable-but-reported run).
    /// </summary>
    bool HasValidationReport { get; }

    /// <summary>A snapshot of all messages appended so far, in insertion order.</summary>
    IReadOnlyList<AgentMessage> Messages { get; }

    /// <summary>A snapshot of all agent results recorded so far, in insertion order.</summary>
    IReadOnlyList<AgentResult> Results { get; }

    /// <summary>
    /// Commits the Cosmos DB analysis output. Throws <see cref="InvalidOperationException"/> if it has
    /// already been set (single-assignment guard against concurrent double-writes).
    /// </summary>
    /// <param name="producerName">Name of the agent producing the output.</param>
    /// <param name="analysis">The analysis to commit.</param>
    void SetCosmosAnalysis(string producerName, CosmosDbAnalysis analysis);

    /// <summary>
    /// Commits the SQL migration assessment output. Throws <see cref="InvalidOperationException"/> if it
    /// has already been set.
    /// </summary>
    /// <param name="producerName">Name of the agent producing the output.</param>
    /// <param name="assessment">The assessment to commit.</param>
    void SetSqlAssessment(string producerName, SqlMigrationAssessment assessment);

    /// <summary>
    /// Commits the data-quality analysis output. Throws <see cref="InvalidOperationException"/> if it has
    /// already been set.
    /// </summary>
    /// <param name="producerName">Name of the agent producing the output.</param>
    /// <param name="analysis">The analysis to commit.</param>
    void SetDataQualityAnalysis(string producerName, DataQualityAnalysis analysis);

    /// <summary>
    /// Commits the Data Factory migration estimate. Throws <see cref="InvalidOperationException"/> if it
    /// has already been set.
    /// </summary>
    /// <param name="producerName">Name of the producer (an agent or the orchestrator's derived step).</param>
    /// <param name="estimate">The estimate to commit.</param>
    void SetDataFactoryEstimate(string producerName, DataFactoryEstimate estimate);

    /// <summary>
    /// Commits the validator's verdict. Throws <see cref="InvalidOperationException"/> if it has already been
    /// set (validation is single-run per context).
    /// </summary>
    /// <param name="producerName">Name of the validating agent.</param>
    /// <param name="report">The validation report to commit.</param>
    void SetValidationReport(string producerName, ValidationReport report);

    /// <summary>Appends a pre-built message to the blackboard.</summary>
    /// <param name="message">The message to append.</param>
    void AddMessage(AgentMessage message);

    /// <summary>Appends an <see cref="AgentMessageLevel.Info"/> message.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    void LogInfo(string agentName, string text);

    /// <summary>Appends an <see cref="AgentMessageLevel.Warning"/> message.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    void LogWarning(string agentName, string text);

    /// <summary>Appends an <see cref="AgentMessageLevel.Error"/> message.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    void LogError(string agentName, string text);

    /// <summary>Records the terminal result of an agent run.</summary>
    /// <param name="result">The result to record.</param>
    void RecordResult(AgentResult result);

    /// <summary>Returns the most recently recorded result for the named agent, or <see langword="null"/>.</summary>
    /// <param name="agentName">The agent name to look up.</param>
    /// <returns>The matching <see cref="AgentResult"/> or <see langword="null"/>.</returns>
    AgentResult? GetResult(string agentName);

    /// <summary>Whether any recorded result for the given role succeeded.</summary>
    /// <param name="role">The role to test.</param>
    /// <returns><see langword="true"/> if at least one agent with that role succeeded.</returns>
    bool HasSucceeded(AgentRole role);

    /// <summary>
    /// Lists the required outputs (<see cref="CosmosAnalysis"/>, <see cref="SqlAssessment"/>,
    /// <see cref="DataFactoryEstimate"/>) that are still missing. Data-quality analysis is optional and
    /// never reported here. Used by the validator to flag incomplete runs.
    /// </summary>
    /// <returns>Human-readable names of missing required outputs; empty when complete.</returns>
    IReadOnlyList<string> GetMissingRequiredOutputs();

    /// <summary>Whether all required outputs are present (i.e. <see cref="GetMissingRequiredOutputs"/> is empty).</summary>
    /// <returns><see langword="true"/> if the context can produce a complete assessment result.</returns>
    bool IsCompleteForAssessmentResult();

    /// <summary>
    /// Projects the accumulated outputs into the existing <see cref="AssessmentResult"/> model so the
    /// unchanged downstream report / SQL-project / Data Factory generation can consume them. Best-effort:
    /// missing required outputs are filled with empty defaults rather than throwing, mirroring the
    /// single-pass pipeline's tolerance of a skipped data-quality phase.
    /// </summary>
    /// <returns>A populated <see cref="AssessmentResult"/>.</returns>
    AssessmentResult ToAssessmentResult();
}
