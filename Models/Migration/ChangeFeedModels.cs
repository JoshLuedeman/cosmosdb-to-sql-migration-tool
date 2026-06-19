namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// The Azure Cosmos DB change feed read mode used to drive incremental synchronization.
    /// </summary>
    public enum ChangeFeedMode
    {
        /// <summary>
        /// Latest-version (formerly "incremental") mode. Emits the most recent version of each
        /// created or updated item. Always available on the SQL (Core) API. Does <b>not</b> emit
        /// delete or intermediate-version events.
        /// </summary>
        LatestVersion,

        /// <summary>
        /// All-versions-and-deletes (formerly "full fidelity") mode. Emits every version of an item
        /// plus delete tombstones, but only within the container's configured retention window.
        /// Requires an explicit container <c>ChangeFeedPolicy</c> retention configuration that is not
        /// readable through the public .NET SDK metadata.
        /// </summary>
        AllVersionsAndDeletes
    }

    /// <summary>
    /// Describes how confidently a given change feed mode's availability can be determined from the
    /// metadata the assessment tool can read.
    /// </summary>
    public enum ChangeFeedModeAvailability
    {
        /// <summary>The mode is guaranteed available for the container (e.g. latest-version on the SQL API).</summary>
        Available,

        /// <summary>
        /// Availability cannot be determined from the public SDK metadata and must be verified manually
        /// against the account/container configuration (e.g. all-versions-and-deletes retention).
        /// </summary>
        RequiresManualVerification
    }

    /// <summary>
    /// Per-container assessment of change feed readiness for incremental migration. Derived purely from
    /// the already-collected <see cref="ContainerAnalysis"/>; it performs no live Cosmos DB calls.
    /// </summary>
    public sealed class ContainerChangeFeedReadiness
    {
        /// <summary>Name of the Cosmos DB container this readiness record describes.</summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>
        /// Whether the latest-version change feed is available. Always <c>true</c> for SQL (Core) API
        /// containers; included explicitly so reports can state it unambiguously.
        /// </summary>
        public bool LatestVersionChangeFeedAvailable { get; set; } = true;

        /// <summary>
        /// Detectability of all-versions-and-deletes mode. The public SDK does not expose the
        /// container <c>ChangeFeedPolicy</c>/retention, so this is
        /// <see cref="ChangeFeedModeAvailability.RequiresManualVerification"/>.
        /// </summary>
        public ChangeFeedModeAvailability AllVersionsAndDeletesAvailability { get; set; }
            = ChangeFeedModeAvailability.RequiresManualVerification;

        /// <summary>
        /// Whether time-to-live (TTL) is enabled on the container at all
        /// (<c>DefaultTimeToLive</c> has a value, whether <c>-1</c> or a positive default).
        /// </summary>
        public bool TimeToLiveEnabled { get; set; }

        /// <summary>
        /// Whether a positive default TTL expiration is active (<c>DefaultTimeToLive &gt; 0</c>), meaning
        /// items expire automatically after the default number of seconds unless overridden per item.
        /// </summary>
        public bool DefaultTtlExpirationEnabled { get; set; }

        /// <summary>
        /// Whether only item-level TTL is possible (<c>DefaultTimeToLive == -1</c>): TTL is enabled but
        /// no container default applies, so individual items may still set their own expiration.
        /// </summary>
        public bool ItemLevelTtlPossible { get; set; }

        /// <summary>
        /// Raw <c>DefaultTimeToLive</c> value preserved for downstream reasoning:
        /// <c>null</c> = TTL disabled, <c>-1</c> = enabled with no default, <c>&gt;0</c> = default seconds.
        /// </summary>
        public int? DefaultTimeToLiveSeconds { get; set; }

        /// <summary>The custom TTL property path, when the container expires items by a property other than <c>_ts</c>.</summary>
        public string? TimeToLivePropertyPath { get; set; }

        /// <summary>The container's partition-key path(s). Contains more than one entry for hierarchical (sub-)partition keys.</summary>
        public IReadOnlyList<string> PartitionKeyPaths { get; set; } = new List<string>();

        /// <summary>Number of partition-key paths (1 for a classic single-level key, &gt;1 for hierarchical keys).</summary>
        public int PartitionKeyPathCount { get; set; }

        /// <summary>Whether the container uses a hierarchical (multi-level) partition key.</summary>
        public bool IsHierarchicalPartitionKey { get; set; }

        /// <summary>
        /// Approximate number of feed ranges (physical partitions) for the container, captured during
        /// live analysis via <c>GetFeedRangesAsync</c>. Feeds change feed processor lease sizing and the
        /// incremental-sync parallelism estimate. <c>0</c> when the value could not be read.
        /// </summary>
        public int FeedRangeCount { get; set; }

        /// <summary>
        /// Whether deletes propagate to the SQL target through the recommended latest-version feed.
        /// Always <c>false</c>: the latest-version change feed never emits delete events.
        /// </summary>
        public bool DeletePropagationSupportedByLatestVersion { get; set; }

        /// <summary>
        /// Whether the container has a known source of server-side deletes (TTL expiration) that the
        /// latest-version change feed will not surface.
        /// </summary>
        public bool KnownServerSideTtlDeletes { get; set; }

        /// <summary>
        /// Whether delete handling must be validated/designed externally before cutover (always <c>true</c>:
        /// even without TTL, application hard deletes are invisible to the latest-version change feed).
        /// </summary>
        public bool RequiresDeleteHandlingValidation { get; set; } = true;

        /// <summary>The change feed mode recommended for this container's incremental sync.</summary>
        public ChangeFeedMode RecommendedMode { get; set; } = ChangeFeedMode.LatestVersion;

        /// <summary>Actionable warnings about change feed limitations for this container (e.g. TTL delete gaps).</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Informational notes about change feed behavior for this container.</summary>
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Database-level aggregation of per-container change feed readiness used to ground the incremental
    /// migration plan, sync estimates, and cutover/runbook documentation.
    /// </summary>
    public sealed class ChangeFeedAvailabilityAnalysis
    {
        /// <summary>Per-container change feed readiness records.</summary>
        public List<ContainerChangeFeedReadiness> Containers { get; set; } = new();

        /// <summary>
        /// Whether every container supports latest-version change feed incremental sync of creates/updates.
        /// True for SQL (Core) API databases; surfaced explicitly for report clarity.
        /// </summary>
        public bool AllContainersSupportLatestVersionIncrementalSync { get; set; } = true;

        /// <summary>Whether any container has a known server-side delete source (TTL expiration).</summary>
        public bool AnyContainerHasKnownServerSideDeletes { get; set; }

        /// <summary>
        /// Whether delete propagation requires an external validation/design step. Always <c>true</c>
        /// because the latest-version change feed never emits hard deletes.
        /// </summary>
        public bool DeletePropagationRequiresExternalValidation { get; set; } = true;

        /// <summary>Database-wide warnings about change feed limitations and verification requirements.</summary>
        public List<string> GlobalWarnings { get; set; } = new();
    }
}
