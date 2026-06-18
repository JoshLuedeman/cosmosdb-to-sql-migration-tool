namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Identifies the assessment domain an <see cref="IAssessmentAgent"/> owns and the
/// kind of output it contributes to a <see cref="SharedAssessmentContext"/>.
/// </summary>
/// <remarks>
/// Roles let the orchestrator and the validator reason about which domain produced a
/// given result without depending on concrete agent types. Adding new values is a
/// source-compatible change, so the enum can grow as later epics introduce more agents.
/// </remarks>
public enum AgentRole
{
    /// <summary>Analysis of the source Cosmos DB account, databases, and containers.</summary>
    CosmosAnalysis,

    /// <summary>Azure SQL platform recommendation and migration planning.</summary>
    SqlPlanning,

    /// <summary>Pre-migration data-quality analysis of the source documents.</summary>
    DataQuality,

    /// <summary>
    /// Azure Data Factory migration estimate derived from the Cosmos analysis and SQL plan.
    /// Produced by the orchestrator as a derived step rather than by a domain agent.
    /// </summary>
    DataFactoryEstimation,

    /// <summary>Cross-checking and completeness validation of other agents' outputs.</summary>
    Validation,

    /// <summary>Coordination of the agent run itself (used by the orchestrator).</summary>
    Orchestration
}
