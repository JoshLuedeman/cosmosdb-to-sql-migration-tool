using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Estimates and compares the initial bulk-load duration against change-feed incremental sync time
    /// and steady-state behavior (#135 of parent #69). Pure analysis over the already-collected
    /// <see cref="CosmosDbAnalysis"/> and <see cref="DataFactoryEstimate"/>; performs no live Cosmos DB
    /// calls and reuses the existing Data Factory initial-load estimate as its baseline.
    /// </summary>
    /// <remarks>
    /// All figures are heuristic planning estimates, not guaranteed throughput. Incremental capacity is
    /// approximated as a configurable multiple of the estimated end-to-end initial-load throughput
    /// (default ×1.0), deliberately conservative because incremental sync typically performs more
    /// expensive target-side upserts/merges than the initial insert-only load.
    /// </remarks>
    public sealed class IncrementalSyncEstimator
    {
        private const int SecondsPerDay = 86_400;

        private readonly IConfiguration _configuration;
        private readonly ILogger<IncrementalSyncEstimator> _logger;

        /// <summary>Creates a new <see cref="IncrementalSyncEstimator"/>.</summary>
        /// <param name="configuration">Configuration supplying the <c>IncrementalMigration:*</c> assumptions.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public IncrementalSyncEstimator(IConfiguration configuration, ILogger<IncrementalSyncEstimator> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Produces a database-level initial-load-versus-incremental-sync estimate.
        /// </summary>
        /// <param name="cosmosAnalysis">The collected Cosmos DB analysis. Must not be <c>null</c>.</param>
        /// <param name="dataFactoryEstimate">The Data Factory estimate providing initial-load durations. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="IncrementalSyncEstimate"/>.</returns>
        /// <exception cref="ArgumentNullException">If either argument is <c>null</c>.</exception>
        public IncrementalSyncEstimate Estimate(CosmosDbAnalysis cosmosAnalysis, DataFactoryEstimate dataFactoryEstimate)
        {
            ArgumentNullException.ThrowIfNull(cosmosAnalysis);
            ArgumentNullException.ThrowIfNull(dataFactoryEstimate);

            var changeRatePercent = Math.Max(0, _configuration.GetValue("IncrementalMigration:DailyChangeRatePercent", 5.0));
            var syncIntervalMinutes = Math.Max(1, _configuration.GetValue("IncrementalMigration:SyncIntervalMinutes", 15));
            var throughputFactor = Math.Max(0.01, _configuration.GetValue("IncrementalMigration:IncrementalThroughputFactor", 1.0));
            var syncInterval = TimeSpan.FromMinutes(syncIntervalMinutes);

            var estimate = new IncrementalSyncEstimate
            {
                DailyDocumentChangeRatePercent = changeRatePercent,
                SyncInterval = syncInterval,
                IncrementalThroughputFactorRelativeToInitialLoad = throughputFactor,
                InitialLoadDuration = dataFactoryEstimate.EstimatedDuration,
            };

            estimate.Assumptions.Add(
                $"Daily change rate is interpreted as document churn: {changeRatePercent:0.##}% of documents changed per day.");
            estimate.Assumptions.Add(
                $"Incremental sync capacity is estimated as {throughputFactor:0.##}× the estimated end-to-end " +
                "initial-load throughput (not raw change-feed read throughput); incremental upserts/merges may be slower.");
            estimate.Assumptions.Add(
                $"Steady-state sync lag assumes an incremental trigger cadence of {syncIntervalMinutes} minute(s).");
            estimate.Assumptions.Add(
                "Estimates are heuristic planning figures, not guaranteed SLAs, and do not model queueing variance.");

            if (cosmosAnalysis.Containers.Count == 0)
            {
                estimate.SteadyStateSustainable = true;
                estimate.OverallRisk = SyncSustainabilityRisk.Healthy;
                estimate.EstimatedBacklogCatchUpAfterInitialLoad = TimeSpan.Zero;
                estimate.EstimatedSteadyStateSyncLag = syncInterval;
                estimate.Notes.Add("No containers available; nothing to synchronize.");
                return estimate;
            }

            var totalInitialSeconds = Math.Max(0, dataFactoryEstimate.EstimatedDuration.TotalSeconds);
            long totalDocs = cosmosAnalysis.Containers.Sum(c => Math.Max(0, c.DocumentCount));

            foreach (var container in cosmosAnalysis.Containers)
            {
                estimate.Containers.Add(EstimateContainer(
                    container,
                    dataFactoryEstimate,
                    totalInitialSeconds,
                    totalDocs,
                    changeRatePercent,
                    throughputFactor,
                    syncInterval));
            }

            estimate.TotalDocuments = totalDocs;
            estimate.EstimatedTotalChangedDocumentsPerDay = estimate.Containers.Sum(c => c.EstimatedChangedDocumentsPerDay);
            estimate.EstimatedTotalChangedDocumentsPerSecond = estimate.Containers.Sum(c => c.EstimatedChangedDocumentsPerSecond);
            estimate.EstimatedBacklogDocumentsAfterInitialLoad = estimate.Containers.Sum(c => c.EstimatedBacklogDocumentsAfterInitialLoad);
            estimate.SteadyStateSustainable = estimate.Containers.All(c => c.SteadyStateSustainable);
            estimate.OverallRisk = estimate.Containers.Select(c => c.Risk).DefaultIfEmpty(SyncSustainabilityRisk.Unknown).Max();
            estimate.EstimatedSteadyStateSyncLag = estimate.Containers
                .Select(c => c.EstimatedSteadyStateSyncLag)
                .DefaultIfEmpty(syncInterval)
                .Max();

            // Containers sync in parallel, so the overall catch-up is the slowest container. If any
            // container cannot drain its backlog (null catch-up), the overall catch-up is undefined.
            if (estimate.Containers.Any(c => c.EstimatedBacklogCatchUp is null))
            {
                estimate.EstimatedBacklogCatchUpAfterInitialLoad = null;
            }
            else
            {
                estimate.EstimatedBacklogCatchUpAfterInitialLoad = estimate.Containers
                    .Select(c => c.EstimatedBacklogCatchUp!.Value)
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Max();
            }

            estimate.HighestRiskContainers = estimate.Containers
                .Where(c => c.Risk is SyncSustainabilityRisk.High or SyncSustainabilityRisk.Unsustainable)
                .OrderByDescending(c => c.Risk)
                .ThenByDescending(c => c.UtilizationPercent)
                .Select(c => c.ContainerName)
                .ToList();

            if (!estimate.SteadyStateSustainable)
            {
                estimate.Notes.Add(
                    "One or more containers have an estimated change rate at or above incremental capacity. " +
                    "Increase provisioned throughput / parallelism, raise the change-feed processing capacity, " +
                    "or shorten the sync interval before relying on incremental sync for cutover.");
            }

            _logger.LogInformation(
                "Incremental sync estimated for {ContainerCount} container(s): overall risk {Risk}, sustainable={Sustainable}",
                estimate.Containers.Count, estimate.OverallRisk, estimate.SteadyStateSustainable);

            return estimate;
        }

        private static ContainerIncrementalSyncEstimate EstimateContainer(
            ContainerAnalysis container,
            DataFactoryEstimate dataFactoryEstimate,
            double totalInitialSeconds,
            long totalDocs,
            double changeRatePercent,
            double throughputFactor,
            TimeSpan syncInterval)
        {
            var docs = Math.Max(0, container.DocumentCount);
            var sizeBytes = Math.Max(0, container.SizeBytes);

            var result = new ContainerIncrementalSyncEstimate
            {
                ContainerName = container.ContainerName,
                DocumentCount = docs,
                SizeBytes = sizeBytes,
                AverageDocumentSizeBytes = docs > 0 ? (double)sizeBytes / docs : 0,
                FeedRangeCount = container.FeedRangeCount,
                DailyDocumentChangeRatePercent = changeRatePercent,
            };

            // Initial-load duration: prefer the matching pipeline estimate; otherwise distribute the
            // overall duration by document-count share.
            var pipeline = dataFactoryEstimate.PipelineEstimates
                .FirstOrDefault(p => string.Equals(p.SourceContainer, container.ContainerName, StringComparison.OrdinalIgnoreCase));
            double initialSeconds;
            if (pipeline is not null)
            {
                initialSeconds = Math.Max(0, pipeline.EstimatedDuration.TotalSeconds);
                result.InitialLoadDuration = pipeline.EstimatedDuration;
            }
            else if (totalDocs > 0)
            {
                initialSeconds = totalInitialSeconds * ((double)docs / totalDocs);
                result.InitialLoadDuration = TimeSpan.FromSeconds(initialSeconds);
                result.Notes.Add("No matching pipeline estimate; initial-load duration distributed by document share.");
            }
            else
            {
                initialSeconds = 0;
                result.InitialLoadDuration = TimeSpan.Zero;
            }

            // Change-rate figures.
            var changedPerDay = (long)Math.Round(docs * changeRatePercent / 100.0);
            result.EstimatedChangedDocumentsPerDay = changedPerDay;
            result.EstimatedChangedDocumentsPerSecond = changedPerDay / (double)SecondsPerDay;
            result.EstimatedChangedDataPerDayMB = sizeBytes * (changeRatePercent / 100.0) / (1024.0 * 1024.0);

            if (docs == 0)
            {
                // Nothing to load or sync.
                result.InitialLoadThroughputKnown = true;
                result.SteadyStateSustainable = true;
                result.Risk = SyncSustainabilityRisk.Healthy;
                result.EstimatedBacklogCatchUp = TimeSpan.Zero;
                result.EstimatedSteadyStateSyncLag = syncInterval;
                result.Notes.Add("Container has no documents; no incremental sync workload.");
                return result;
            }

            if (initialSeconds <= 0)
            {
                // Documents exist but no measurable initial-load time → capacity is indeterminate.
                result.InitialLoadThroughputKnown = false;
                result.Risk = SyncSustainabilityRisk.Unknown;
                result.SteadyStateSustainable = false;
                result.EstimatedBacklogCatchUp = null;
                result.EstimatedSteadyStateSyncLag = syncInterval;
                result.Notes.Add("Initial-load duration is unknown (non-positive); incremental capacity cannot be estimated.");
                AddFeedRangeNote(result);
                return result;
            }

            result.InitialLoadThroughputKnown = true;
            result.InitialLoadDocsPerSecond = docs / initialSeconds;
            var capacity = result.InitialLoadDocsPerSecond * throughputFactor;
            result.EstimatedIncrementalCapacityDocsPerSecond = capacity;

            var changedPerSecond = result.EstimatedChangedDocumentsPerSecond;
            result.UtilizationPercent = capacity > 0 ? changedPerSecond / capacity * 100.0 : 0;
            result.Risk = ClassifyRisk(result.UtilizationPercent);
            result.SteadyStateSustainable = changedPerSecond < capacity;

            // Backlog accrued during the initial load.
            result.EstimatedBacklogDocumentsAfterInitialLoad = (long)Math.Round(changedPerSecond * initialSeconds);

            if (result.SteadyStateSustainable)
            {
                var drainRate = capacity - changedPerSecond; // net documents drained per second
                var catchUpSeconds = drainRate > 0
                    ? result.EstimatedBacklogDocumentsAfterInitialLoad / drainRate
                    : 0;
                result.EstimatedBacklogCatchUp = TimeSpan.FromSeconds(catchUpSeconds);

                var processingPerInterval = capacity > 0
                    ? changedPerSecond * syncInterval.TotalSeconds / capacity
                    : 0;
                result.EstimatedSteadyStateSyncLag = syncInterval + TimeSpan.FromSeconds(processingPerInterval);
            }
            else
            {
                result.EstimatedBacklogCatchUp = null;
                result.EstimatedSteadyStateSyncLag = syncInterval;
                result.Notes.Add(
                    "Estimated change rate meets or exceeds incremental capacity; the backlog cannot drain at this capacity.");
            }

            AddFeedRangeNote(result);
            return result;
        }

        private static void AddFeedRangeNote(ContainerIncrementalSyncEstimate result)
        {
            if (result.FeedRangeCount <= 1 && result.EstimatedChangedDocumentsPerDay > 0)
            {
                result.Notes.Add(
                    "Container has a single feed range (physical partition); change-feed processing cannot be " +
                    "parallelized across partitions, which can limit incremental sync throughput for high change volumes.");
            }
        }

        private static SyncSustainabilityRisk ClassifyRisk(double utilizationPercent) => utilizationPercent switch
        {
            < 50.0 => SyncSustainabilityRisk.Healthy,
            < 80.0 => SyncSustainabilityRisk.Moderate,
            < 100.0 => SyncSustainabilityRisk.High,
            _ => SyncSustainabilityRisk.Unsustainable,
        };
    }
}
