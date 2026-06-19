using CosmosToSqlAssessment.Models.Monitoring;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Supplies the raw migration progress samples that the <c>migration status</c> command
/// (#225) renders. Implementations typically read from the Azure Data Factory pipeline run
/// telemetry (parent #70) surfaced through Azure Monitor (parent #76).
/// </summary>
/// <remarks>
/// In one-shot mode the returned stream yields the currently-known samples and completes.
/// In watch mode (<see cref="MigrationStatusReportOptions.Watch"/>) the stream keeps yielding
/// newly observed samples — polling at <see cref="MigrationStatusReportOptions.PollIntervalSeconds"/>
/// — until the supplied <see cref="CancellationToken"/> is cancelled.
/// </remarks>
public interface IMigrationStatusSource
{
    /// <summary>
    /// Reads migration progress samples for the requested reporting mode.
    /// </summary>
    /// <param name="options">Watch/poll settings for the read.</param>
    /// <param name="cancellationToken">Token that stops enumeration (and any polling loop).</param>
    /// <returns>A stream of progress samples, ordered oldest-to-newest.</returns>
    IAsyncEnumerable<MigrationProgressSample> ReadSamplesAsync(
        MigrationStatusReportOptions options,
        CancellationToken cancellationToken = default);
}
