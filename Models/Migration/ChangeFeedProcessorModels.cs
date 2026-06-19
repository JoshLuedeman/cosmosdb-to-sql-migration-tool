namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Checkpointing strategy recommended for a Change Feed Processor (CFP) worker (#140 of parent #69).
    /// The change feed processor delivers changes <b>at least once</b>, so downstream processing must be
    /// idempotent regardless of the strategy chosen.
    /// </summary>
    public enum CheckpointStrategy
    {
        /// <summary>
        /// The CFP automatically checkpoints after each successfully processed batch (the default). Simple
        /// and resilient; on a host restart/rebalance the last uncheckpointed batch is redelivered.
        /// </summary>
        AutomaticPerBatch,

        /// <summary>
        /// The delegate checkpoints manually to gain finer control over the at-least-once boundary (for
        /// example checkpointing only after the SQL upsert/MERGE commits). Reduces duplicate reprocessing
        /// at the cost of additional implementation complexity.
        /// </summary>
        ManualForAtLeastOnceControl
    }

    /// <summary>
    /// Per-container Change Feed Processor implementation guidance (#140). Derived purely from the #134
    /// <see cref="ContainerChangeFeedReadiness"/>; performs no live Cosmos DB calls.
    /// </summary>
    public sealed class ContainerChangeFeedProcessorGuidance
    {
        /// <summary>Name of the Cosmos DB container the guidance applies to.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>The change feed mode recommended for this container (from the #134 readiness analysis).</summary>
        public ChangeFeedMode RecommendedMode { get; set; } = ChangeFeedMode.LatestVersion;

        /// <summary>
        /// Approximate feed-range (physical-partition) count for the container, captured during analysis.
        /// <c>0</c> when the value could not be read; see <see cref="FeedRangeCountKnown"/>.
        /// </summary>
        public int FeedRangeCount { get; set; }

        /// <summary>Whether <see cref="FeedRangeCount"/> is a known value (greater than zero) rather than unreadable.</summary>
        public bool FeedRangeCountKnown { get; set; }

        /// <summary>
        /// Upper bound on the number of compute instances that can usefully process this container's change
        /// feed in parallel (one lease per feed range). <c>null</c> when the feed-range count is unknown.
        /// This is a runtime-varying ceiling (partition split/merge), not a guaranteed degree of parallelism.
        /// </summary>
        public int? MaxUsefulComputeInstances { get; set; }

        /// <summary>
        /// Whether the recommended mode requires the account to have continuous backup enabled. Always
        /// <c>true</c> for <see cref="ChangeFeedMode.AllVersionsAndDeletes"/>; otherwise <c>false</c>.
        /// </summary>
        public bool RequiresContinuousBackup { get; set; }

        /// <summary>
        /// Whether this container's processor needs isolated lease state — a dedicated lease container or, at
        /// minimum, a distinct processor name / lease prefix. Always <c>true</c> for all-versions-and-deletes
        /// (its checkpoint state must never be shared with a latest-version processor).
        /// </summary>
        public bool RequiresIsolatedLeaseState { get; set; }

        /// <summary>Note describing how deletes must be handled for this container under the recommended mode.</summary>
        public string DeleteHandlingNote { get; set; } = string.Empty;

        /// <summary>Additional informational notes for this container's processor guidance.</summary>
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Database-level Change Feed Processor (CFP) implementation guidance (#140 of parent #69). Produced from
    /// the #134 change-feed availability analysis, it advises on building a CFP worker as an alternative (or
    /// complement) to the Azure Data Factory <c>_ts</c>-watermark incremental copy pipeline this tool also
    /// generates. All figures are conservative planning guidance, not validated sizing — monitor and adjust.
    /// </summary>
    public sealed class ChangeFeedProcessorGuidance
    {
        /// <summary>Recommended name for the dedicated lease container.</summary>
        public string RecommendedLeaseContainerName { get; set; } = "leases";

        /// <summary>Recommended partition-key path for the lease container.</summary>
        public string LeaseContainerPartitionKeyPath { get; set; } = "/id";

        /// <summary>
        /// A conservative <b>starting</b> throughput (RU/s, expressed as an autoscale maximum) for the lease
        /// container — not validated sizing. Lease workload is driven mostly by checkpoint frequency, lease
        /// renew/acquire cadence, failovers, and lease count; monitor for throttling (429s) and adjust.
        /// </summary>
        public int SuggestedLeaseContainerStartingRUs { get; set; }

        /// <summary>Whether the lease-container throughput is recommended to use autoscale (its RU value is the autoscale maximum).</summary>
        public bool LeaseContainerUsesAutoscale { get; set; } = true;

        /// <summary>The checkpointing strategy recommended for the worker.</summary>
        public CheckpointStrategy CheckpointStrategy { get; set; } = CheckpointStrategy.AutomaticPerBatch;

        /// <summary>Note describing the at-least-once delivery / idempotency implication of the checkpoint strategy.</summary>
        public string CheckpointingNote { get; set; } = string.Empty;

        /// <summary>Recommended number of compute instances to start with (kept deliberately small; scale out on evidence).</summary>
        public int RecommendedInitialComputeInstances { get; set; } = 1;

        /// <summary>
        /// Useful scale-out ceiling when a single worker fleet runs the processors for all containers: the
        /// largest single-container feed-range count (a host can run multiple containers' processors).
        /// <c>null</c> when no container has a known feed-range count.
        /// </summary>
        public int? ComputeScaleOutCeilingSharedFleet { get; set; }

        /// <summary>
        /// Useful scale-out ceiling when each container is processed by an independent worker pool: the sum of
        /// the per-container feed-range counts. <c>null</c> when no container has a known feed-range count.
        /// </summary>
        public int? ComputeScaleOutCeilingIndependentPools { get; set; }

        /// <summary>Guidance on what to scale on (and up to which ceiling).</summary>
        public string ScaleOutTrigger { get; set; } = string.Empty;

        /// <summary>Whether any container is recommended to use all-versions-and-deletes mode.</summary>
        public bool AnyContainerRequiresAllVersionsAndDeletes { get; set; }

        /// <summary>Whether the account must have continuous backup enabled (true when any container needs all-versions-and-deletes).</summary>
        public bool RequiresContinuousBackupForDeletes { get; set; }

        /// <summary>Per-container processor guidance.</summary>
        public List<ContainerChangeFeedProcessorGuidance> Containers { get; set; } = new();

        /// <summary>Ordered, actionable implementation steps for standing up the CFP worker.</summary>
        public List<string> ImplementationSteps { get; set; } = new();

        /// <summary>The explicit assumptions behind this guidance.</summary>
        public List<string> Assumptions { get; set; } = new();

        /// <summary>Warnings and caveats that qualify the guidance.</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// How a Change Feed Processor worker relates to the Azure Data Factory <c>_ts</c>-watermark
        /// incremental copy pipeline this tool also generates: the trade-offs and the rule that both must
        /// not write the same SQL target without idempotency and duplicate-boundary handling.
        /// </summary>
        public List<string> RelationshipToDataFactoryWatermarkPipeline { get; set; } = new();
    }
}
