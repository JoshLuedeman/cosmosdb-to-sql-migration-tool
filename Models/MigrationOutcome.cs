using System.Text.Json.Serialization;

namespace CosmosToSqlAssessment.Models;

/// <summary>
/// The terminal status of a completed (or attempted) migration, used as a coarse
/// success metric in the continuous-learning feedback loop.
/// </summary>
public enum MigrationOutcomeStatus
{
    /// <summary>Status was not recorded.</summary>
    Unknown = 0,

    /// <summary>The migration completed and all data/workload moved successfully.</summary>
    Succeeded = 1,

    /// <summary>The migration completed but with caveats (e.g., partial data, manual fix-ups required).</summary>
    PartiallySucceeded = 2,

    /// <summary>The migration attempt failed before completion.</summary>
    Failed = 3,

    /// <summary>The migration was completed but later rolled back.</summary>
    RolledBack = 4
}

/// <summary>
/// Normalized, tool-owned assessment of overall migration complexity. Mirrors the
/// free-text <see cref="MigrationComplexity.OverallComplexity"/> value but as a closed
/// enum so it cannot smuggle arbitrary (potentially identifying) text into a persisted
/// feedback record.
/// </summary>
public enum MigrationComplexityRating
{
    /// <summary>Complexity could not be determined from the assessment.</summary>
    Unknown = 0,

    /// <summary>Low-complexity migration.</summary>
    Low = 1,

    /// <summary>Medium-complexity migration.</summary>
    Medium = 2,

    /// <summary>High-complexity migration.</summary>
    High = 3
}

/// <summary>
/// Coarse size band for a workload, derived from its total data size. Used for grouping
/// and similarity matching so that exact sizes need not be compared directly.
/// </summary>
public enum WorkloadSizeBucket
{
    /// <summary>Under 10 GB.</summary>
    Small = 0,

    /// <summary>10 GB up to (but not including) 100 GB.</summary>
    Medium = 1,

    /// <summary>100 GB up to (but not including) 1 TB.</summary>
    Large = 2,

    /// <summary>1 TB and above.</summary>
    VeryLarge = 3
}

/// <summary>
/// An anonymized, aggregate "fingerprint" of an assessed workload. It captures only the
/// non-identifying, tool-derived characteristics needed to find <em>similar</em> prior
/// migrations (so refined recommendations can be attributed to "N prior similar
/// migrations"). It deliberately excludes any names, endpoints, identifiers, or other
/// customer data.
/// </summary>
/// <remarks>
/// Privacy-by-design: every property here is either a closed enum, an aggregate count, or
/// an aggregate size — none of which carry per-document or per-customer detail. Cosmos
/// account/database/container names are intentionally never included.
/// </remarks>
public sealed class WorkloadProfile
{
    /// <summary>Normalized overall migration complexity rating.</summary>
    public MigrationComplexityRating ComplexityRating { get; set; } = MigrationComplexityRating.Unknown;

    /// <summary>Number of source containers in the assessed workload.</summary>
    public int ContainerCount { get; set; }

    /// <summary>Coarse size band derived from <see cref="TotalDataSizeGb"/>.</summary>
    public WorkloadSizeBucket SizeBucket { get; set; } = WorkloadSizeBucket.Small;

    /// <summary>Aggregate count of documents across all containers (not per-document data).</summary>
    public long TotalDocumentCount { get; set; }

    /// <summary>Aggregate data size across all containers, in gigabytes.</summary>
    public double TotalDataSizeGb { get; set; }

    /// <summary>The highest provisioned request units (RU/s) observed across containers.</summary>
    public int MaxProvisionedRUs { get; set; }

    /// <summary>Count of index recommendations produced by the assessment (a complexity signal).</summary>
    public int IndexRecommendationCount { get; set; }

    /// <summary>The Azure SQL platform recommended by the assessment (tool-generated label).</summary>
    public string RecommendedPlatform { get; set; } = string.Empty;

    /// <summary>The Azure SQL service tier recommended by the assessment (tool-generated label).</summary>
    public string RecommendedTier { get; set; } = string.Empty;

    /// <summary>
    /// Maps a total data size in gigabytes to a coarse <see cref="WorkloadSizeBucket"/>.
    /// </summary>
    /// <param name="totalDataSizeGb">Aggregate workload size in gigabytes.</param>
    /// <returns>The matching size band.</returns>
    public static WorkloadSizeBucket BucketFor(double totalDataSizeGb)
    {
        if (totalDataSizeGb < 10) return WorkloadSizeBucket.Small;
        if (totalDataSizeGb < 100) return WorkloadSizeBucket.Medium;
        if (totalDataSizeGb < 1024) return WorkloadSizeBucket.Large;
        return WorkloadSizeBucket.VeryLarge;
    }

    /// <summary>
    /// Parses a free-text complexity label (e.g., "Low"/"Medium"/"High") into the closed
    /// <see cref="MigrationComplexityRating"/> enum, returning
    /// <see cref="MigrationComplexityRating.Unknown"/> for unrecognized values.
    /// </summary>
    /// <param name="complexity">The free-text complexity label to normalize.</param>
    /// <returns>The normalized complexity rating.</returns>
    public static MigrationComplexityRating ParseComplexity(string? complexity) =>
        complexity?.Trim().ToLowerInvariant() switch
        {
            "low" => MigrationComplexityRating.Low,
            "medium" => MigrationComplexityRating.Medium,
            "high" => MigrationComplexityRating.High,
            _ => MigrationComplexityRating.Unknown
        };

    /// <summary>
    /// Derives an anonymized <see cref="WorkloadProfile"/> from a completed assessment.
    /// Only aggregate, non-identifying characteristics are extracted.
    /// </summary>
    /// <param name="assessment">The assessment result to fingerprint.</param>
    /// <returns>A workload profile suitable for similarity matching.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assessment"/> is null.</exception>
    public static WorkloadProfile FromAssessment(AssessmentResult assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var containers = assessment.CosmosAnalysis?.Containers ?? new List<ContainerAnalysis>();
        var totalDocs = containers.Sum(c => c.DocumentCount);
        var totalSizeGb = containers.Sum(c => c.SizeBytes) / (1024.0 * 1024.0 * 1024.0);
        var maxRUs = containers.Count > 0 ? containers.Max(c => c.ProvisionedRUs) : 0;

        var sql = assessment.SqlAssessment ?? new SqlMigrationAssessment();

        return new WorkloadProfile
        {
            ComplexityRating = ParseComplexity(sql.Complexity?.OverallComplexity),
            ContainerCount = containers.Count,
            TotalDocumentCount = totalDocs,
            TotalDataSizeGb = totalSizeGb,
            SizeBucket = BucketFor(totalSizeGb),
            MaxProvisionedRUs = maxRUs,
            IndexRecommendationCount = sql.IndexRecommendations?.Count ?? 0,
            RecommendedPlatform = sql.RecommendedPlatform ?? string.Empty,
            RecommendedTier = sql.RecommendedTier ?? string.Empty
        };
    }
}

/// <summary>
/// An anonymized, aggregate record of how a migration actually turned out, captured
/// <em>only with explicit opt-in</em> and used to refine future recommendations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privacy contract.</b> This schema is anonymized and aggregate <em>by construction</em>:
/// it contains no names, endpoints, identifiers (other than a random, non-correlatable
/// <see cref="OutcomeId"/>), IP addresses, credentials, or free-text notes. Every field is
/// either a closed enum, an aggregate count/size, a tool-generated categorical label, or a
/// numeric metric. There is intentionally <b>no</b> property in which a caller could place
/// customer data such as a database name or a sample document.
/// </para>
/// <para>
/// The three metric families required by the feedback loop are represented explicitly:
/// <b>success</b> (<see cref="Status"/>, <see cref="ActualMigrationDays"/>,
/// <see cref="DataCompletenessPercent"/>), <b>performance</b> (<see cref="DeployedPlatform"/>,
/// <see cref="DeployedTier"/>, utilization/latency, <see cref="PerformanceSatisfactory"/>),
/// and <b>cost actual vs estimate</b> (the monthly and one-time cost pairs).
/// </para>
/// </remarks>
public sealed class MigrationOutcome
{
    /// <summary>The schema version this record was written with.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The schema version of this record, enabling forward-compatible evolution of the
    /// persisted feedback format.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// A random, non-correlatable identifier for this record (no link to any user, account,
    /// or assessment). Used only to de-duplicate records within a store.
    /// </summary>
    public string OutcomeId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The UTC instant at which this outcome was recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The anonymized workload fingerprint used for similarity matching.</summary>
    public WorkloadProfile Profile { get; set; } = new();

    // ---- Success metrics ----

    /// <summary>The terminal status of the migration.</summary>
    public MigrationOutcomeStatus Status { get; set; } = MigrationOutcomeStatus.Unknown;

    /// <summary>The actual number of days the migration took, end to end.</summary>
    public int ActualMigrationDays { get; set; }

    /// <summary>
    /// The percentage (0–100) of data that migrated successfully (an aggregate completeness
    /// measure, not per-row detail).
    /// </summary>
    public double DataCompletenessPercent { get; set; }

    // ---- Performance metrics ----

    /// <summary>The Azure SQL platform actually deployed (may differ from the recommendation).</summary>
    public string DeployedPlatform { get; set; } = string.Empty;

    /// <summary>The Azure SQL service tier actually deployed (may differ from the recommendation).</summary>
    public string DeployedTier { get; set; } = string.Empty;

    /// <summary>Average post-migration resource utilization (DTU/vCore) percentage, if measured.</summary>
    public double? AvgResourceUtilizationPercent { get; set; }

    /// <summary>Peak post-migration resource utilization (DTU/vCore) percentage, if measured.</summary>
    public double? PeakResourceUtilizationPercent { get; set; }

    /// <summary>Average post-migration query latency in milliseconds, if measured.</summary>
    public double? AvgQueryLatencyMs { get; set; }

    /// <summary>
    /// Whether the deployed sizing performed satisfactorily in production, if assessed.
    /// This is the primary signal the refinement logic learns from.
    /// </summary>
    public bool? PerformanceSatisfactory { get; set; }

    // ---- Cost actual vs estimate ----

    /// <summary>The estimated recurring monthly Azure SQL cost (USD).</summary>
    public decimal EstimatedMonthlyCostUsd { get; set; }

    /// <summary>The actual recurring monthly Azure SQL cost (USD).</summary>
    public decimal ActualMonthlyCostUsd { get; set; }

    /// <summary>The estimated one-time migration cost (USD), e.g., Data Factory.</summary>
    public decimal EstimatedMigrationCostUsd { get; set; }

    /// <summary>The actual one-time migration cost (USD).</summary>
    public decimal ActualMigrationCostUsd { get; set; }

    // ---- Derived (not persisted) ----

    /// <summary>The signed monthly cost variance (actual minus estimate), in USD.</summary>
    [JsonIgnore]
    public decimal MonthlyCostVarianceUsd => ActualMonthlyCostUsd - EstimatedMonthlyCostUsd;

    /// <summary>
    /// The monthly cost variance as a percentage of the estimate, or <see langword="null"/>
    /// when the estimate is zero (undefined).
    /// </summary>
    [JsonIgnore]
    public double? MonthlyCostVariancePercent =>
        EstimatedMonthlyCostUsd == 0
            ? null
            : (double)((ActualMonthlyCostUsd - EstimatedMonthlyCostUsd) / EstimatedMonthlyCostUsd) * 100.0;

    /// <summary>
    /// Whether this outcome counts as a success for learning purposes (fully or partially
    /// succeeded).
    /// </summary>
    [JsonIgnore]
    public bool Succeeded =>
        Status is MigrationOutcomeStatus.Succeeded or MigrationOutcomeStatus.PartiallySucceeded;
}
