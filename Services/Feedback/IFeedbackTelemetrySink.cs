using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services.Feedback;

/// <summary>
/// A telemetry-safe, <em>coarsened</em> projection of a <see cref="MigrationOutcome"/> intended for
/// the optional remote telemetry endpoint. Exact aggregate counts and costs are replaced with
/// bucketed labels to further reduce any re-identification risk when data leaves the local machine.
/// </summary>
/// <remarks>
/// This type intentionally carries only enums and bucketed string labels — never raw counts, sizes,
/// costs, names, or identifiers.
/// </remarks>
public sealed class CoarsenedOutcome
{
    /// <summary>The schema version of the originating outcome.</summary>
    public int SchemaVersion { get; set; } = MigrationOutcome.CurrentSchemaVersion;

    /// <summary>Normalized workload complexity.</summary>
    public MigrationComplexityRating ComplexityRating { get; set; }

    /// <summary>Coarse workload size band.</summary>
    public WorkloadSizeBucket SizeBucket { get; set; }

    /// <summary>Bucketed number of source containers.</summary>
    public string ContainerCountBucket { get; set; } = string.Empty;

    /// <summary>The recommended Azure SQL platform (tool-generated label).</summary>
    public string RecommendedPlatform { get; set; } = string.Empty;

    /// <summary>The recommended Azure SQL tier (tool-generated label).</summary>
    public string RecommendedTier { get; set; } = string.Empty;

    /// <summary>The deployed Azure SQL platform (tool-generated label).</summary>
    public string DeployedPlatform { get; set; } = string.Empty;

    /// <summary>The deployed Azure SQL tier (tool-generated label).</summary>
    public string DeployedTier { get; set; } = string.Empty;

    /// <summary>The terminal migration status.</summary>
    public MigrationOutcomeStatus Status { get; set; }

    /// <summary>Whether the deployed sizing performed satisfactorily, if assessed.</summary>
    public bool? PerformanceSatisfactory { get; set; }

    /// <summary>Bucketed monthly cost variance (estimate accuracy band).</summary>
    public string MonthlyCostVarianceBucket { get; set; } = string.Empty;

    /// <summary>Bucketed actual migration duration.</summary>
    public string MigrationDaysBucket { get; set; } = string.Empty;

    /// <summary>
    /// Projects a full <see cref="MigrationOutcome"/> into its telemetry-safe, coarsened form.
    /// </summary>
    /// <param name="outcome">The outcome to coarsen.</param>
    /// <returns>A bucketed, telemetry-safe projection.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outcome"/> is null.</exception>
    public static CoarsenedOutcome From(MigrationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var profile = outcome.Profile ?? new WorkloadProfile();

        return new CoarsenedOutcome
        {
            SchemaVersion = outcome.SchemaVersion,
            ComplexityRating = profile.ComplexityRating,
            SizeBucket = profile.SizeBucket,
            ContainerCountBucket = BucketContainerCount(profile.ContainerCount),
            RecommendedPlatform = profile.RecommendedPlatform,
            RecommendedTier = profile.RecommendedTier,
            DeployedPlatform = outcome.DeployedPlatform,
            DeployedTier = outcome.DeployedTier,
            Status = outcome.Status,
            PerformanceSatisfactory = outcome.PerformanceSatisfactory,
            MonthlyCostVarianceBucket = BucketCostVariance(outcome.MonthlyCostVariancePercent),
            MigrationDaysBucket = BucketDays(outcome.ActualMigrationDays)
        };
    }

    /// <summary>Maps a container count to a coarse band.</summary>
    /// <param name="count">The exact container count.</param>
    /// <returns>A bucket label.</returns>
    public static string BucketContainerCount(int count) => count switch
    {
        <= 1 => "1",
        <= 5 => "2-5",
        <= 20 => "6-20",
        <= 100 => "21-100",
        _ => "100+"
    };

    /// <summary>Maps a monthly cost variance percentage to an estimate-accuracy band.</summary>
    /// <param name="variancePercent">The monthly cost variance percent, or null if undefined.</param>
    /// <returns>A bucket label.</returns>
    public static string BucketCostVariance(double? variancePercent) => variancePercent switch
    {
        null => "Unknown",
        < -10 => "UnderEstimate",
        <= 10 => "OnTarget",
        <= 50 => "OverEstimate",
        _ => "WellOverEstimate"
    };

    /// <summary>Maps an actual migration duration in days to a coarse band.</summary>
    /// <param name="days">The actual migration duration in days.</param>
    /// <returns>A bucket label.</returns>
    public static string BucketDays(int days) => days switch
    {
        <= 5 => "0-5",
        <= 15 => "6-15",
        <= 30 => "16-30",
        _ => "30+"
    };
}

/// <summary>
/// Optional transport that forwards a <see cref="CoarsenedOutcome"/> to a configured remote
/// telemetry endpoint. Only invoked when feedback consent is granted <em>and</em> a telemetry
/// endpoint is configured.
/// </summary>
public interface IFeedbackTelemetrySink
{
    /// <summary>
    /// Sends a coarsened outcome to the remote endpoint. Implementations must not throw on
    /// transport errors (local collection must always succeed regardless).
    /// </summary>
    /// <param name="payload">The coarsened, telemetry-safe payload.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the send attempt finishes.</returns>
    Task SendAsync(CoarsenedOutcome payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// A no-op <see cref="IFeedbackTelemetrySink"/> used when no remote telemetry endpoint is
/// configured. Nothing leaves the local machine.
/// </summary>
public sealed class NullFeedbackTelemetrySink : IFeedbackTelemetrySink
{
    /// <inheritdoc />
    public Task SendAsync(CoarsenedOutcome payload, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
