using System.Collections.Generic;

namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Recommended Azure SQL time-based partition granularity for a migrated container's target table
    /// (#138 of parent #69). The granularity reflects a manageability suggestion derived from data volume;
    /// it is never a guarantee that partitioning improves query performance.
    /// </summary>
    public enum PartitionGranularity
    {
        /// <summary>No time-based partitioning recommended (the overhead is not justified by the data volume).</summary>
        None = 0,

        /// <summary>One partition per calendar year.</summary>
        Year = 1,

        /// <summary>One partition per calendar month.</summary>
        Month = 2,

        /// <summary>One partition per calendar day.</summary>
        Day = 3,
    }

    /// <summary>
    /// Overall strength of the time-based partitioning recommendation for a container's target SQL table.
    /// Partitioning in Azure SQL is primarily a manageability feature (sliding-window aging, partition-level
    /// maintenance) rather than a general query-performance feature, so the default leans conservative.
    /// </summary>
    public enum PartitioningStrength
    {
        /// <summary>
        /// Partitioning is not recommended. Applies to small containers, containers with no usable schema
        /// evidence, or empty containers where a single non-partitioned table is simpler and sufficient.
        /// </summary>
        NotRecommended = 0,

        /// <summary>
        /// Partitioning may help with manageability at this data volume, but the decision must be validated
        /// against the expected query predicates and the retention/archival policy before adopting it.
        /// </summary>
        ConditionalManageability = 1,

        /// <summary>
        /// Partitioning is recommended: the data volume is large and a high-confidence immutable temporal
        /// column is available to serve as the partitioning key.
        /// </summary>
        Recommended = 2,
    }

    /// <summary>
    /// Confidence that a detected field is a suitable, stable temporal partitioning key candidate.
    /// </summary>
    public enum TemporalColumnConfidence
    {
        /// <summary>Low confidence: ambiguous, business-specific, or likely-mutable temporal field.</summary>
        Low = 0,

        /// <summary>Medium confidence: an event/occurrence-style timestamp that is usually but not always immutable.</summary>
        Medium = 1,

        /// <summary>High confidence: a creation/insertion timestamp that is almost certainly immutable.</summary>
        High = 2,
    }

    /// <summary>
    /// Heuristic assessment of whether a temporal field's value is stable (immutable) over a document's
    /// lifetime. Mutable partitioning columns force expensive cross-partition row movement on update and
    /// must be avoided as Azure SQL partitioning keys.
    /// </summary>
    public enum TemporalColumnMutabilityRisk
    {
        /// <summary>Mutability cannot be inferred from the field name; user validation required.</summary>
        Unknown = 0,

        /// <summary>The field name suggests an immutable creation/event timestamp.</summary>
        ImmutableLikely = 1,

        /// <summary>The field name suggests a mutable last-modified/business timestamp; avoid as a partition key.</summary>
        MutableLikely = 2,
    }

    /// <summary>
    /// Whether sliding-window partition maintenance (aging out the oldest partition via SWITCH/DROP rather
    /// than row-by-row deletes) is applicable for the container's target table.
    /// </summary>
    public enum SlidingWindowConsideration
    {
        /// <summary>Not applicable: no time-to-live signal or no partitioning recommended.</summary>
        NotApplicable = 0,

        /// <summary>
        /// May apply, but only after validating that the SQL partition column's age basis matches the
        /// Cosmos TTL age basis (TTL ages by <c>_ts</c> last-modified, which differs from a creation column).
        /// </summary>
        ConsiderWithValidation = 1,
    }

    /// <summary>
    /// A detected document field evaluated as a potential Azure SQL time-based partitioning key (#138).
    /// </summary>
    public sealed class TemporalColumnCandidate
    {
        /// <summary>The source field name as detected in the Cosmos documents.</summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>The recommended SQL data type for the field (for example <c>datetime2</c>).</summary>
        public string RecommendedSqlType { get; set; } = string.Empty;

        /// <summary>The raw JSON/CLR types detected for the field across sampled documents.</summary>
        public List<string> DetectedTypes { get; set; } = new();

        /// <summary>Confidence that this field is a suitable, stable partitioning key.</summary>
        public TemporalColumnConfidence Confidence { get; set; }

        /// <summary>Heuristic mutability risk of the field's value over a document's lifetime.</summary>
        public TemporalColumnMutabilityRisk MutabilityRisk { get; set; }

        /// <summary>
        /// Fraction (0–1) of the container's detected schema variants (weighted by prevalence) in which the
        /// field appears. Low prevalence indicates a sparse field that may be unsuitable as a partition key.
        /// </summary>
        public double SchemaPrevalence { get; set; }

        /// <summary>Human-readable rationale for the confidence and mutability classification.</summary>
        public string Rationale { get; set; } = string.Empty;

        /// <summary>
        /// <c>true</c> when this candidate is a synthetic <c>InitialLoadTimestamp</c> column captured from the
        /// Cosmos <c>_ts</c> value at load time rather than an existing document field. Such a column is a
        /// stable partitioning value but is NOT the document's true creation time.
        /// </summary>
        public bool IsSyntheticFromInitialLoad { get; set; }
    }

    /// <summary>
    /// Time-based partitioning recommendation for a single container's target Azure SQL table (#138).
    /// </summary>
    public sealed class ContainerPartitioningRecommendation
    {
        /// <summary>The Cosmos container name.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>The container's document count (echoed for reporting context).</summary>
        public long DocumentCount { get; set; }

        /// <summary>The container's data size in bytes (echoed for reporting context).</summary>
        public long SizeBytes { get; set; }

        /// <summary>Overall strength of the partitioning recommendation.</summary>
        public PartitioningStrength Strength { get; set; }

        /// <summary>Recommended time-based partition granularity (or <see cref="PartitionGranularity.None"/>).</summary>
        public PartitionGranularity RecommendedGranularity { get; set; }

        /// <summary>
        /// The Azure SQL partition function range direction recommended for a temporal key. Always
        /// <c>RANGE RIGHT</c>, which is conventional for ascending date boundaries.
        /// </summary>
        public string PartitionFunctionType { get; set; } = "RANGE RIGHT";

        /// <summary>Ranked shortlist of candidate temporal partitioning columns (highest confidence first).</summary>
        public List<TemporalColumnCandidate> TemporalColumnCandidates { get; set; } = new();

        /// <summary>
        /// The single recommended partition column name, set only when exactly one high-confidence immutable
        /// candidate exists; otherwise <c>null</c>, meaning the column choice requires user validation.
        /// </summary>
        public string? RecommendedPartitionColumn { get; set; }

        /// <summary>
        /// <c>true</c> when no suitable immutable temporal column was detected and the recommendation is to
        /// capture the initial Cosmos <c>_ts</c> as a stable <c>InitialLoadTimestamp</c> column at load time.
        /// </summary>
        public bool RequiresSyntheticCreationColumn { get; set; }

        /// <summary>Whether sliding-window partition maintenance should be considered (TTL-driven).</summary>
        public SlidingWindowConsideration SlidingWindow { get; set; }

        /// <summary>
        /// Upper bound on parallel initial-load workers, derived from the container's feed-range count. This
        /// is a ceiling, not a guaranteed degree of parallelism (RU limits, throttling, hot ranges, and
        /// feed-range split/merge apply).
        /// </summary>
        public int InitialLoadParallelismUpperBound { get; set; }

        /// <summary>Recommended approach for slicing the initial bulk load by Cosmos <c>_ts</c> windows.</summary>
        public string InitialLoadSlicingApproach { get; set; } = string.Empty;

        /// <summary>Azure SQL partition/index alignment caveats the user must honor when implementing.</summary>
        public List<string> IndexAlignmentCaveats { get; set; } = new();

        /// <summary>Rationale lines explaining the recommendation.</summary>
        public List<string> Rationale { get; set; } = new();

        /// <summary>Caveats and warnings that qualify the recommendation.</summary>
        public List<string> Caveats { get; set; } = new();
    }

    /// <summary>
    /// Database-level time-based partitioning analysis aggregating per-container recommendations (#138).
    /// </summary>
    public sealed class TimeBasedPartitioningAnalysis
    {
        /// <summary>Per-container time-based partitioning recommendations.</summary>
        public List<ContainerPartitioningRecommendation> Containers { get; set; } = new();

        /// <summary>Count of containers for which partitioning is recommended or conditionally suggested.</summary>
        public int ContainersRecommendedForPartitioning { get; set; }

        /// <summary>Assumptions underpinning the analysis (for example detection heuristics and thresholds).</summary>
        public List<string> Assumptions { get; set; } = new();

        /// <summary>Database-wide notes applicable across all containers.</summary>
        public List<string> Notes { get; set; } = new();
    }
}
