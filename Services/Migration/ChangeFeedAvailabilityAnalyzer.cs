using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Determines per-container change feed readiness for incremental (change-feed-based) migration.
    /// Pure analysis over an already-collected <see cref="CosmosDbAnalysis"/>; performs no live Cosmos
    /// DB calls so it is deterministic, fast, and unit-testable. Foundational sub-issue #134 of parent
    /// #69 (incremental migration support with change feed).
    /// </summary>
    /// <remarks>
    /// Key domain facts encoded here:
    /// <list type="bullet">
    ///   <item>The latest-version change feed is always available on the SQL (Core) API.</item>
    ///   <item>The latest-version change feed never emits delete events, so hard deletes (application
    ///   <c>DeleteItem</c> calls or TTL expiration) are invisible to it and must be handled separately.</item>
    ///   <item>All-versions-and-deletes mode emits deletes within a retention window but requires a
    ///   container <c>ChangeFeedPolicy</c> retention that the public .NET SDK does not expose — so its
    ///   availability is reported as requiring manual verification.</item>
    /// </list>
    /// </remarks>
    public sealed class ChangeFeedAvailabilityAnalyzer
    {
        private readonly ILogger<ChangeFeedAvailabilityAnalyzer> _logger;

        /// <summary>Creates a new <see cref="ChangeFeedAvailabilityAnalyzer"/>.</summary>
        /// <param name="logger">Logger for diagnostic output.</param>
        public ChangeFeedAvailabilityAnalyzer(ILogger<ChangeFeedAvailabilityAnalyzer> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Produces a database-level change feed availability analysis from the supplied Cosmos analysis.
        /// </summary>
        /// <param name="cosmosAnalysis">The collected Cosmos DB analysis. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="ChangeFeedAvailabilityAnalysis"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="cosmosAnalysis"/> is <c>null</c>.</exception>
        public ChangeFeedAvailabilityAnalysis Analyze(CosmosDbAnalysis cosmosAnalysis)
        {
            ArgumentNullException.ThrowIfNull(cosmosAnalysis);

            var analysis = new ChangeFeedAvailabilityAnalysis();

            foreach (var container in cosmosAnalysis.Containers)
            {
                analysis.Containers.Add(AnalyzeContainer(container));
            }

            analysis.AllContainersSupportLatestVersionIncrementalSync =
                analysis.Containers.All(c => c.LatestVersionChangeFeedAvailable);
            analysis.AnyContainerHasKnownServerSideDeletes =
                analysis.Containers.Any(c => c.KnownServerSideTtlDeletes);

            analysis.GlobalWarnings.Add(
                "The latest-version change feed never emits delete events. Hard deletes (application " +
                "DeleteItem calls or TTL expiration) will not propagate to the SQL target through " +
                "latest-version incremental sync and must be reconciled with a soft-delete pattern, a " +
                "periodic full reconciliation, or all-versions-and-deletes mode.");

            analysis.GlobalWarnings.Add(
                "All-versions-and-deletes (full-fidelity) availability cannot be verified from public SDK " +
                "metadata. If delete capture is required, confirm the container ChangeFeedPolicy retention " +
                "is configured in the Azure portal / ARM before relying on it.");

            if (analysis.AnyContainerHasKnownServerSideDeletes)
            {
                analysis.GlobalWarnings.Add(
                    "One or more containers have TTL enabled. TTL-expired documents are deleted server-side " +
                    "and are invisible to the latest-version change feed; plan an explicit strategy to age " +
                    "out the corresponding SQL rows.");
            }

            _logger.LogInformation(
                "Change feed availability analyzed for {ContainerCount} container(s); {TtlContainers} with TTL deletes",
                analysis.Containers.Count,
                analysis.Containers.Count(c => c.KnownServerSideTtlDeletes));

            return analysis;
        }

        private static ContainerChangeFeedReadiness AnalyzeContainer(ContainerAnalysis container)
        {
            var ttl = container.DefaultTimeToLiveSeconds;
            var ttlEnabled = ttl.HasValue;
            var defaultExpiration = ttl is > 0;
            var itemLevelTtlOnly = ttl == -1;

            var partitionKeyPaths = (container.PartitionKeyPaths is { Count: > 0 })
                ? container.PartitionKeyPaths.ToList()
                : (!string.IsNullOrEmpty(container.PartitionKey)
                    ? new List<string> { container.PartitionKey }
                    : new List<string>());

            var readiness = new ContainerChangeFeedReadiness
            {
                ContainerName = container.ContainerName,
                LatestVersionChangeFeedAvailable = true,
                AllVersionsAndDeletesAvailability = ChangeFeedModeAvailability.RequiresManualVerification,
                TimeToLiveEnabled = ttlEnabled,
                DefaultTtlExpirationEnabled = defaultExpiration,
                ItemLevelTtlPossible = itemLevelTtlOnly,
                DefaultTimeToLiveSeconds = ttl,
                TimeToLivePropertyPath = container.TimeToLivePropertyPath,
                PartitionKeyPaths = partitionKeyPaths,
                PartitionKeyPathCount = partitionKeyPaths.Count,
                IsHierarchicalPartitionKey = partitionKeyPaths.Count > 1,
                FeedRangeCount = container.FeedRangeCount,
                DeletePropagationSupportedByLatestVersion = false,
                KnownServerSideTtlDeletes = ttlEnabled,
                RequiresDeleteHandlingValidation = true,
            };

            readiness.Notes.Add("Latest-version change feed is available for incremental sync of creates and updates.");
            readiness.Notes.Add(
                "Latest-version change feed does not emit delete events; application hard deletes will not " +
                "propagate to the SQL target via incremental sync.");

            if (ttlEnabled)
            {
                readiness.RecommendedMode = ChangeFeedMode.AllVersionsAndDeletes;
                var ttlDescription = defaultExpiration
                    ? $"a default expiration of {ttl} second(s)"
                    : "item-level TTL only (DefaultTimeToLive = -1)";
                readiness.Warnings.Add(
                    $"TTL is enabled ({ttlDescription}). TTL-expired documents are deleted server-side and are " +
                    "not surfaced by the latest-version change feed. Use all-versions-and-deletes mode (verify " +
                    "retention) or design an out-of-band aging/reconciliation strategy for the SQL target.");

                if (!string.IsNullOrEmpty(container.TimeToLivePropertyPath))
                {
                    readiness.Notes.Add(
                        $"Container uses a custom TTL property path '{container.TimeToLivePropertyPath}'.");
                }
            }
            else
            {
                readiness.RecommendedMode = ChangeFeedMode.LatestVersion;
                readiness.Notes.Add(
                    "No TTL configured; recommend latest-version change feed for incremental sync, with a " +
                    "delete-handling validation step before cutover.");
            }

            if (readiness.IsHierarchicalPartitionKey)
            {
                readiness.Notes.Add(
                    $"Hierarchical partition key with {readiness.PartitionKeyPathCount} levels: " +
                    string.Join(", ", partitionKeyPaths) + ".");
            }

            return readiness;
        }
    }
}
