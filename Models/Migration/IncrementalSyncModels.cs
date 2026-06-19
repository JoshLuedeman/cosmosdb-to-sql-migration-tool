namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Risk band describing whether the change-feed incremental sync can keep pace with the source
    /// change rate, derived from estimated utilization (change rate ÷ estimated incremental capacity).
    /// </summary>
    public enum SyncSustainabilityRisk
    {
        /// <summary>Utilization could not be determined (e.g. unknown initial-load throughput).</summary>
        Unknown,

        /// <summary>Utilization below 50%: comfortable headroom.</summary>
        Healthy,

        /// <summary>Utilization 50–80%: sustainable but with limited headroom.</summary>
        Moderate,

        /// <summary>Utilization 80–100%: sustainable only under stable conditions; high lag risk under bursts.</summary>
        High,

        /// <summary>Utilization ≥ 100%: the change rate exceeds estimated capacity; backlog cannot drain.</summary>
        Unsustainable
    }

    /// <summary>
    /// Per-container comparison of estimated initial bulk-load time versus change-feed incremental sync
    /// behavior (#135). All throughput figures are heuristic planning estimates, not guaranteed SLAs.
    /// </summary>
    public sealed class ContainerIncrementalSyncEstimate
    {
        /// <summary>Source container name.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>Approximate document count for the container.</summary>
        public long DocumentCount { get; set; }

        /// <summary>Approximate stored size of the container in bytes.</summary>
        public long SizeBytes { get; set; }

        /// <summary>Average document size in bytes (<c>SizeBytes / DocumentCount</c>; 0 when no documents).</summary>
        public double AverageDocumentSizeBytes { get; set; }

        /// <summary>Approximate feed-range (physical-partition) count; a parallelism signal for change-feed processing.</summary>
        public int FeedRangeCount { get; set; }

        /// <summary>Estimated initial bulk-load duration for this container (reused from the Data Factory estimate).</summary>
        public TimeSpan InitialLoadDuration { get; set; }

        /// <summary>
        /// Whether the initial-load throughput could be derived (false when the estimated initial-load
        /// duration was non-positive while documents exist, making capacity indeterminate).
        /// </summary>
        public bool InitialLoadThroughputKnown { get; set; }

        /// <summary>Estimated documents-per-second of the initial bulk load (<c>DocumentCount / initialLoadSeconds</c>).</summary>
        public double InitialLoadDocsPerSecond { get; set; }

        /// <summary>Assumed daily document churn rate (percentage of documents changed per day).</summary>
        public double DailyDocumentChangeRatePercent { get; set; }

        /// <summary>Estimated number of documents changed per day at the assumed churn rate.</summary>
        public long EstimatedChangedDocumentsPerDay { get; set; }

        /// <summary>Estimated steady-state changed documents per second.</summary>
        public double EstimatedChangedDocumentsPerSecond { get; set; }

        /// <summary>Estimated changed data volume per day in megabytes (churn rate applied to stored size).</summary>
        public double EstimatedChangedDataPerDayMB { get; set; }

        /// <summary>
        /// Estimated incremental sync capacity in documents per second
        /// (<c>InitialLoadDocsPerSecond × IncrementalThroughputFactor</c>). 0 when capacity is unknown.
        /// </summary>
        public double EstimatedIncrementalCapacityDocsPerSecond { get; set; }

        /// <summary>Estimated utilization percentage (change rate ÷ incremental capacity × 100). 0 when unknown.</summary>
        public double UtilizationPercent { get; set; }

        /// <summary>Risk band derived from <see cref="UtilizationPercent"/>.</summary>
        public SyncSustainabilityRisk Risk { get; set; } = SyncSustainabilityRisk.Unknown;

        /// <summary>Whether the steady-state change rate is below the estimated incremental capacity.</summary>
        public bool SteadyStateSustainable { get; set; }

        /// <summary>Estimated number of changed documents accumulated during the initial load (the post-load backlog).</summary>
        public long EstimatedBacklogDocumentsAfterInitialLoad { get; set; }

        /// <summary>
        /// Estimated time to drain the post-initial-load backlog while still ingesting new changes.
        /// <c>null</c> when the stream is unsustainable or capacity is unknown (the backlog cannot be drained).
        /// </summary>
        public TimeSpan? EstimatedBacklogCatchUp { get; set; }

        /// <summary>Estimated steady-state sync lag (sync interval plus one interval's processing time).</summary>
        public TimeSpan EstimatedSteadyStateSyncLag { get; set; }

        /// <summary>Informational notes / risk callouts for this container.</summary>
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Database-level comparison of initial bulk-load time versus change-feed incremental sync time and
    /// steady-state behavior (#135). Grounds the cutover-window (#136) and phased-plan (#137) sub-issues.
    /// </summary>
    public sealed class IncrementalSyncEstimate
    {
        /// <summary>Assumed daily document churn rate used for all containers.</summary>
        public double DailyDocumentChangeRatePercent { get; set; }

        /// <summary>Assumed incremental sync interval (trigger cadence) used for steady-state lag.</summary>
        public TimeSpan SyncInterval { get; set; }

        /// <summary>
        /// Multiplier applied to estimated initial-load throughput to approximate incremental sync capacity.
        /// Relative to end-to-end initial-load throughput, not raw change-feed read throughput.
        /// </summary>
        public double IncrementalThroughputFactorRelativeToInitialLoad { get; set; }

        /// <summary>Overall estimated initial bulk-load duration (reused from the Data Factory estimate).</summary>
        public TimeSpan InitialLoadDuration { get; set; }

        /// <summary>Total document count across all containers.</summary>
        public long TotalDocuments { get; set; }

        /// <summary>Estimated total changed documents per day across all containers.</summary>
        public long EstimatedTotalChangedDocumentsPerDay { get; set; }

        /// <summary>Estimated total changed documents per second across all containers.</summary>
        public double EstimatedTotalChangedDocumentsPerSecond { get; set; }

        /// <summary>Estimated total backlog documents accumulated during the initial load.</summary>
        public long EstimatedBacklogDocumentsAfterInitialLoad { get; set; }

        /// <summary>
        /// Estimated time for the slowest container to drain its post-initial-load backlog (containers
        /// sync in parallel, so the overall catch-up is the maximum across containers). <c>null</c> when
        /// any container is unsustainable or its capacity is unknown.
        /// </summary>
        public TimeSpan? EstimatedBacklogCatchUpAfterInitialLoad { get; set; }

        /// <summary>Estimated steady-state sync lag (maximum across containers).</summary>
        public TimeSpan EstimatedSteadyStateSyncLag { get; set; }

        /// <summary>Whether every container's steady-state change rate is below its estimated capacity.</summary>
        public bool SteadyStateSustainable { get; set; }

        /// <summary>Worst (highest) risk band across all containers.</summary>
        public SyncSustainabilityRisk OverallRisk { get; set; } = SyncSustainabilityRisk.Unknown;

        /// <summary>Per-container incremental sync estimates.</summary>
        public List<ContainerIncrementalSyncEstimate> Containers { get; set; } = new();

        /// <summary>Names of the highest-risk containers (by utilization / catch-up), most critical first.</summary>
        public List<string> HighestRiskContainers { get; set; } = new();

        /// <summary>The explicit assumptions behind these estimates.</summary>
        public List<string> Assumptions { get; set; } = new();

        /// <summary>Additional informational notes.</summary>
        public List<string> Notes { get; set; } = new();
    }
}
