namespace CosmosToSqlAssessment.Agents;

/// <summary>
/// Severity of an <see cref="AgentMessage"/> recorded on a <see cref="SharedAssessmentContext"/>.
/// </summary>
public enum AgentMessageLevel
{
    /// <summary>Informational progress or diagnostic message.</summary>
    Info,

    /// <summary>A non-fatal concern that did not stop the agent.</summary>
    Warning,

    /// <summary>An error condition, typically paired with a failed <see cref="AgentResult"/>.</summary>
    Error
}

/// <summary>
/// An immutable, timestamped log entry produced by an agent and appended to the shared
/// <see cref="SharedAssessmentContext.Messages"/> blackboard.
/// </summary>
/// <param name="AgentName">The <see cref="IAssessmentAgent.Name"/> of the producing agent.</param>
/// <param name="Level">The severity of the message.</param>
/// <param name="Text">The human-readable message text.</param>
/// <param name="TimestampUtc">The UTC time the message was created.</param>
public sealed record AgentMessage(
    string AgentName,
    AgentMessageLevel Level,
    string Text,
    DateTimeOffset TimestampUtc)
{
    /// <summary>Creates an <see cref="AgentMessageLevel.Info"/> message stamped with the current UTC time.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    /// <returns>A new <see cref="AgentMessage"/>.</returns>
    public static AgentMessage Info(string agentName, string text) =>
        new(agentName, AgentMessageLevel.Info, text, DateTimeOffset.UtcNow);

    /// <summary>Creates an <see cref="AgentMessageLevel.Warning"/> message stamped with the current UTC time.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    /// <returns>A new <see cref="AgentMessage"/>.</returns>
    public static AgentMessage Warning(string agentName, string text) =>
        new(agentName, AgentMessageLevel.Warning, text, DateTimeOffset.UtcNow);

    /// <summary>Creates an <see cref="AgentMessageLevel.Error"/> message stamped with the current UTC time.</summary>
    /// <param name="agentName">The producing agent's name.</param>
    /// <param name="text">The message text.</param>
    /// <returns>A new <see cref="AgentMessage"/>.</returns>
    public static AgentMessage Error(string agentName, string text) =>
        new(agentName, AgentMessageLevel.Error, text, DateTimeOffset.UtcNow);
}
