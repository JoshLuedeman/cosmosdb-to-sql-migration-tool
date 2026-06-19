namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Whether the estimated cutover downtime window can be bounded with the available estimates (#136).
    /// </summary>
    public enum CutoverFeasibility
    {
        /// <summary>Feasibility could not be determined (e.g. no containers analyzed with indeterminate scope).</summary>
        Unknown,

        /// <summary>Every container's residual backlog can be drained at the estimated capacity ⇒ a bounded downtime window exists.</summary>
        Feasible,

        /// <summary>
        /// At least one container has unbounded pre-cutover lag (steady-state unsustainable) or unknown
        /// incremental capacity, so the final-sync drain — and therefore the full window — cannot be bounded
        /// until zero lag is achieved before cutover. The fixed-overhead floor is still known.
        /// </summary>
        RequiresPreCutoverCatchUp
    }

    /// <summary>
    /// Risk band for the estimated cutover downtime window, assessed relative to a configurable target
    /// downtime (RTO). Derived from the estimated total downtime versus <c>CutoverTargetDowntimeMinutes</c>.
    /// </summary>
    public enum CutoverDowntimeRisk
    {
        /// <summary>The downtime window could not be bounded (pre-cutover catch-up required).</summary>
        Unknown,

        /// <summary>Estimated downtime is within the target downtime.</summary>
        Low,

        /// <summary>Estimated downtime is above target but within twice the target.</summary>
        Moderate,

        /// <summary>Estimated downtime exceeds twice the target downtime.</summary>
        High
    }

    /// <summary>
    /// Per-container contribution to the cutover downtime window (#136): the residual change-feed backlog
    /// expected at the moment the source is quiesced and the time to drain it at the estimated capacity.
    /// All figures are heuristic planning estimates, not guaranteed SLAs.
    /// </summary>
    public sealed class ContainerCutoverEstimate
    {
        /// <summary>Source container name.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>
        /// Estimated residual backlog (documents) accumulated since the last completed sync checkpoint —
        /// the worst-case churn over one steady-state sync lag. Excludes any pre-existing unbounded lag.
        /// </summary>
        public long ResidualBacklogDocuments { get; set; }

        /// <summary>Estimated incremental drain capacity in documents per second (from the #135 sync estimate).</summary>
        public double DrainCapacityDocsPerSecond { get; set; }

        /// <summary>
        /// Estimated time to drain <see cref="ResidualBacklogDocuments"/> at <see cref="DrainCapacityDocsPerSecond"/>
        /// once the source is quiesced. <c>null</c> when the drain cannot be bounded (see <see cref="DrainBounded"/>).
        /// </summary>
        public TimeSpan? ResidualDrainDuration { get; set; }

        /// <summary>
        /// Whether the residual drain is bounded. <c>false</c> when the container's steady-state sync is
        /// unsustainable (pre-cutover lag grows without bound) or its incremental capacity is unknown.
        /// </summary>
        public bool DrainBounded { get; set; }

        /// <summary>Informational notes / remediation callouts for this container's cutover contribution.</summary>
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Estimated cutover downtime window for an online (change-feed-based) migration (#136). Models the
    /// final maintenance window during which the source is quiesced (read-only): quiesce overhead, final
    /// delta-sync drain of the residual change-feed backlog, data validation, connection switchover, plus
    /// a safety buffer. Consumes the #135 <see cref="IncrementalSyncEstimate"/>. Grounds the phased plan
    /// (#137) and report runbook (#139).
    /// </summary>
    public sealed class CutoverWindowEstimate
    {
        /// <summary>Fixed time to quiesce the application / place the source in read-only mode.</summary>
        public TimeSpan AppQuiesceDuration { get; set; }

        /// <summary>
        /// Estimated time to drain the residual change-feed backlog to zero after quiesce, blended between
        /// the fully-parallel and fully-contended bounds by the configured drain parallelism. <c>null</c>
        /// when any container's drain is unbounded (pre-cutover catch-up required).
        /// </summary>
        public TimeSpan? FinalSyncDrainDuration { get; set; }

        /// <summary>
        /// Best-case drain assuming every container drains independently in parallel (the slowest container,
        /// <c>max(residual ÷ capacity)</c>). <c>null</c> when the drain is unbounded.
        /// </summary>
        public TimeSpan? ParallelDrainDuration { get; set; }

        /// <summary>
        /// Worst-case drain assuming the containers fully contend for the target and drain serially
        /// (<c>Σ(residual ÷ capacity)</c>). <c>null</c> when the drain is unbounded.
        /// </summary>
        public TimeSpan? FullyContendedDrainDuration { get; set; }

        /// <summary>The assumed drain parallelism percentage (100 = fully parallel/independent, 0 = fully serial).</summary>
        public double DrainParallelismPercent { get; set; }

        /// <summary>Fixed time for post-cutover data validation / smoke tests.</summary>
        public TimeSpan DataValidationDuration { get; set; }

        /// <summary>Fixed time to switch the application connection string / DNS to the target.</summary>
        public TimeSpan ConnectionSwitchDuration { get; set; }

        /// <summary>Safety buffer percentage applied to the summed window.</summary>
        public double SafetyBufferPercent { get; set; }

        /// <summary>
        /// Sum of the fixed overheads (quiesce + validation + connection switch), excluding drain and buffer.
        /// Always known, even when the full window cannot be bounded.
        /// </summary>
        public TimeSpan FixedOverheadDuration { get; set; }

        /// <summary>
        /// The known lower bound on downtime: the fixed overhead with the safety buffer applied. Useful to
        /// downstream consumers even when <see cref="TotalDowntime"/> is <c>null</c>.
        /// </summary>
        public TimeSpan MinimumKnownDowntime { get; set; }

        /// <summary>
        /// Estimated total cutover downtime (fixed overhead + final-sync drain, with the safety buffer
        /// applied). <c>null</c> when the final-sync drain cannot be bounded (pre-cutover catch-up required).
        /// </summary>
        public TimeSpan? TotalDowntime { get; set; }

        /// <summary>Whether the final-sync drain — and therefore the full window — is bounded.</summary>
        public bool DrainBounded { get; set; }

        /// <summary>Overall feasibility of bounding the cutover window with the available estimates.</summary>
        public CutoverFeasibility Feasibility { get; set; } = CutoverFeasibility.Unknown;

        /// <summary>Risk band relative to the configured target downtime (RTO).</summary>
        public CutoverDowntimeRisk Risk { get; set; } = CutoverDowntimeRisk.Unknown;

        /// <summary>The target downtime (RTO) the window was assessed against.</summary>
        public TimeSpan TargetDowntime { get; set; }

        /// <summary>Per-container cutover drain estimates.</summary>
        public List<ContainerCutoverEstimate> Containers { get; set; } = new();

        /// <summary>Names of containers that require pre-cutover catch-up before a bounded window is possible.</summary>
        public List<string> ContainersRequiringPreCutoverCatchUp { get; set; } = new();

        /// <summary>The explicit assumptions behind the cutover estimate.</summary>
        public List<string> Assumptions { get; set; } = new();

        /// <summary>Additional informational notes.</summary>
        public List<string> Notes { get; set; } = new();
    }
}
