using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Produces Change Feed Processor (CFP) implementation guidance (#140 of parent #69) from the already
    /// computed #134 change-feed availability analysis. Pure composition; performs no live Cosmos DB calls.
    /// </summary>
    /// <remarks>
    /// The guidance advises on building a CFP worker — lease container, parallelism ceilings, mode selection,
    /// checkpointing, and bulk-load coordination — as an alternative or complement to the Azure Data Factory
    /// <c>_ts</c>-watermark incremental copy pipeline this tool also generates. All sizing figures are
    /// conservative starting guidance to monitor and adjust, not validated capacity numbers.
    /// </remarks>
    public sealed class ChangeFeedProcessorGuidanceGenerator
    {
        private const int MinimumLeaseContainerRUs = 400;

        private readonly IConfiguration _configuration;
        private readonly ILogger<ChangeFeedProcessorGuidanceGenerator> _logger;

        /// <summary>Creates a new <see cref="ChangeFeedProcessorGuidanceGenerator"/>.</summary>
        /// <param name="configuration">Configuration supplying the <c>IncrementalMigration:*</c> lease-sizing inputs.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public ChangeFeedProcessorGuidanceGenerator(IConfiguration configuration, ILogger<ChangeFeedProcessorGuidanceGenerator> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Generates Change Feed Processor guidance from the incremental migration analysis.
        /// </summary>
        /// <param name="analysis">The aggregate holding the #134 change-feed analysis. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="ChangeFeedProcessorGuidance"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="analysis"/> is <c>null</c>.</exception>
        public ChangeFeedProcessorGuidance Generate(IncrementalMigrationAnalysis analysis)
        {
            ArgumentNullException.ThrowIfNull(analysis);

            var baseRUs = Math.Max(MinimumLeaseContainerRUs,
                _configuration.GetValue("IncrementalMigration:ChangeFeedProcessorLeaseBaseRUs", MinimumLeaseContainerRUs));
            var perLeaseRUs = Math.Max(0,
                _configuration.GetValue("IncrementalMigration:ChangeFeedProcessorLeaseRUsPerLease", 100));

            var readiness = analysis.ChangeFeed;
            var guidance = new ChangeFeedProcessorGuidance();

            var perContainerCeilings = new List<int>();
            var anyUnknownFeedRange = false;

            foreach (var container in readiness.Containers)
            {
                var known = container.FeedRangeCount > 0;
                var avad = container.RecommendedMode == ChangeFeedMode.AllVersionsAndDeletes;

                var item = new ContainerChangeFeedProcessorGuidance
                {
                    ContainerName = container.ContainerName,
                    RecommendedMode = container.RecommendedMode,
                    FeedRangeCount = container.FeedRangeCount,
                    FeedRangeCountKnown = known,
                    MaxUsefulComputeInstances = known ? Math.Max(1, container.FeedRangeCount) : null,
                    RequiresContinuousBackup = avad,
                    RequiresIsolatedLeaseState = avad,
                    DeleteHandlingNote = avad
                        ? "All-versions-and-deletes mode surfaces delete tombstones (within the continuous-backup retention window); apply them to the SQL target."
                        : "Latest-version mode never emits deletes; handle hard/TTL deletes with a soft-delete pattern or periodic reconciliation."
                };

                if (known)
                {
                    perContainerCeilings.Add(Math.Max(1, container.FeedRangeCount));
                }
                else
                {
                    anyUnknownFeedRange = true;
                    item.Notes.Add("Feed-range count was unreadable; determine it at runtime (GetFeedRangesAsync / CFP leases) before scaling out.");
                }

                guidance.Containers.Add(item);
            }

            var totalKnownRanges = perContainerCeilings.Sum();
            var anyAvad = guidance.Containers.Any(c => c.RequiresContinuousBackup);

            guidance.SuggestedLeaseContainerStartingRUs = Math.Max(MinimumLeaseContainerRUs, baseRUs + (perLeaseRUs * totalKnownRanges));
            guidance.LeaseContainerUsesAutoscale = true;
            guidance.CheckpointStrategy = CheckpointStrategy.AutomaticPerBatch;
            guidance.CheckpointingNote = "The change feed processor delivers changes at least once: a host restart or lease rebalance redelivers the last uncheckpointed batch, so the SQL write path must be idempotent (upsert/MERGE keyed on the document id).";

            guidance.RecommendedInitialComputeInstances = 1;
            guidance.ComputeScaleOutCeilingSharedFleet = perContainerCeilings.Count > 0 ? perContainerCeilings.Max() : null;
            guidance.ComputeScaleOutCeilingIndependentPools = perContainerCeilings.Count > 0 ? totalKnownRanges : null;
            guidance.ScaleOutTrigger = "Start with one instance and scale out based on change feed estimator lag, host CPU/memory, and SQL write throughput, up to the feed-range ceiling (a host can run the processors for multiple containers).";

            guidance.AnyContainerRequiresAllVersionsAndDeletes = anyAvad;
            guidance.RequiresContinuousBackupForDeletes = anyAvad;

            BuildImplementationSteps(guidance, anyAvad);
            BuildAssumptions(guidance);
            BuildWarnings(guidance, anyAvad, anyUnknownFeedRange, readiness.Containers.Count);
            BuildDataFactoryRelationship(guidance);

            _logger.LogInformation(
                "Generated change feed processor guidance for {ContainerCount} container(s); lease container ~{LeaseRus} RU/s autoscale, AVAD required: {Avad}",
                guidance.Containers.Count, guidance.SuggestedLeaseContainerStartingRUs, anyAvad);

            return guidance;
        }

        private static void BuildImplementationSteps(ChangeFeedProcessorGuidance guidance, bool anyAvad)
        {
            guidance.ImplementationSteps.Add($"Provision a dedicated lease container named '{guidance.RecommendedLeaseContainerName}' with partition key '{guidance.LeaseContainerPartitionKeyPath}' (autoscale, starting ~{guidance.SuggestedLeaseContainerStartingRUs} RU/s max; monitor for throttling).");
            guidance.ImplementationSteps.Add("Build a .NET worker with GetChangeFeedProcessorBuilder, setting a stable processor name and a unique instance name per host, pointing at the lease container.");
            guidance.ImplementationSteps.Add("For latest-version mode, start the processor at or before the initial bulk-load snapshot boundary so no change is missed; expect (and tolerate) duplicate deliveries via an idempotent SQL upsert/MERGE.");
            if (anyAvad)
            {
                guidance.ImplementationSteps.Add("For all-versions-and-deletes containers, enable continuous backup and the AVAD feature on the account, use isolated lease state (a dedicated lease container or a distinct processor name/lease prefix), and start within the retention window (historical/from-beginning starts are not available in AVAD).");
            }
            guidance.ImplementationSteps.Add("Make the change handler idempotent and checkpoint after the SQL write commits; the default is automatic per-batch checkpointing.");
            guidance.ImplementationSteps.Add("Deploy a change feed estimator alongside the processor to monitor lag, and scale out instances (up to the feed-range ceiling) when lag grows.");
        }

        private static void BuildAssumptions(ChangeFeedProcessorGuidance guidance)
        {
            guidance.Assumptions.Add("Feed-range count approximates the container's physical-partition count; one lease is taken per feed range.");
            guidance.Assumptions.Add("Lease-container throughput is a conservative starting estimate to monitor and adjust, not validated sizing — actual RU is driven by checkpoint frequency, lease renew/acquire cadence, and failovers.");
            guidance.Assumptions.Add("Compute scale-out ceilings assume one lease per feed range; useful parallelism never exceeds the number of leases.");
        }

        private static void BuildWarnings(ChangeFeedProcessorGuidance guidance, bool anyAvad, bool anyUnknownFeedRange, int containerCount)
        {
            guidance.Warnings.Add("Feed-range counts vary at runtime (partition split/merge), so the parallelism ceiling is approximate, not a guarantee.");
            if (anyUnknownFeedRange)
            {
                guidance.Warnings.Add("One or more containers had an unreadable feed-range count; their parallelism ceiling is unknown — determine it at runtime before scaling out.");
            }
            if (containerCount == 0)
            {
                guidance.Warnings.Add("No containers were analyzed, so the guidance reflects defaults only.");
            }
            if (anyAvad)
            {
                guidance.Warnings.Add("All-versions-and-deletes mode applies to Azure Cosmos DB for NoSQL only, requires continuous backup and the AVAD feature enabled on the account, emits changes only within the retention window, cannot start from the beginning or an arbitrary historical time, may be unsupported on accounts with partition-merge history, and needs a recent Microsoft.Azure.Cosmos SDK (verify current support — it has had preview constraints).");
            }
        }

        private static void BuildDataFactoryRelationship(ChangeFeedProcessorGuidance guidance)
        {
            guidance.RelationshipToDataFactoryWatermarkPipeline.Add("Azure Data Factory _ts-watermark copy (generated by this tool): a scheduled pull that captures visible creates/updates; deletes require a soft-delete pattern or external reconciliation.");
            guidance.RelationshipToDataFactoryWatermarkPipeline.Add("Latest-version Change Feed Processor: continuous push-style processing of creates/updates with at-least-once delivery; still does not surface deletes.");
            guidance.RelationshipToDataFactoryWatermarkPipeline.Add("All-versions-and-deletes Change Feed Processor: also captures deletes and intermediate versions within the retention window, subject to continuous-backup/feature/SDK constraints.");
            guidance.RelationshipToDataFactoryWatermarkPipeline.Add("Choose one incremental path per target table; do not run both the ADF pipeline and a CFP worker into the same SQL target without idempotent upserts and duplicate-boundary handling.");
        }
    }
}
