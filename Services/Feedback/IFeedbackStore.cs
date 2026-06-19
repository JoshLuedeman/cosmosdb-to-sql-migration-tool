using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services.Feedback;

/// <summary>
/// Abstraction over the durable store for anonymized <see cref="MigrationOutcome"/> records
/// that power the continuous-learning feedback loop. Implementations must never persist any
/// data beyond what is present on the supplied (already anonymized) outcome.
/// </summary>
public interface IFeedbackStore
{
    /// <summary>
    /// A human-readable description of where outcomes are stored (e.g., a file path), suitable
    /// for display in consent notices.
    /// </summary>
    string Location { get; }

    /// <summary>
    /// Appends a single anonymized outcome to the store.
    /// </summary>
    /// <param name="outcome">The anonymized outcome to persist.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the record has been written.</returns>
    Task AppendAsync(MigrationOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams all previously recorded outcomes. A missing/empty store yields an empty sequence;
    /// individual malformed records are skipped rather than aborting the stream.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>An asynchronous stream of stored outcomes.</returns>
    IAsyncEnumerable<MigrationOutcome> ReadAllAsync(CancellationToken cancellationToken = default);
}
