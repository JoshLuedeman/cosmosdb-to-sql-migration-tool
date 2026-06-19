namespace CosmosToSqlAssessment.Models.Monitoring;

/// <summary>
/// Options controlling the <c>migration status</c> CLI command (#225): whether to keep
/// watching and how frequently to poll.
/// </summary>
public sealed record MigrationStatusReportOptions
{
    /// <summary>
    /// When <c>true</c>, the command continuously polls and re-renders progress until
    /// cancelled. When <c>false</c> (the default) it renders a single snapshot and exits.
    /// </summary>
    public bool Watch { get; init; }

    /// <summary>Polling interval, in seconds, used by the watch loop. Defaults to 10.</summary>
    public int PollIntervalSeconds { get; init; } = 10;
}
