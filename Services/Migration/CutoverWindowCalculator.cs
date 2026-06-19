using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Calculates the estimated cutover downtime window for an online (change-feed-based) migration
    /// (#136 of parent #69). Pure analysis over the #135 <see cref="IncrementalSyncEstimate"/>; performs
    /// no live Cosmos DB calls.
    /// </summary>
    /// <remarks>
    /// Models the final maintenance window during which the source is quiesced (read-only): a fixed
    /// quiesce overhead, the final delta-sync drain of the residual change-feed backlog, fixed data
    /// validation and connection-switch overheads, and a safety buffer. Because the source is quiesced,
    /// no new changes arrive during cutover, so the residual backlog drains at the estimated incremental
    /// capacity. All figures are heuristic planning estimates, not guaranteed SLAs.
    /// </remarks>
    public sealed class CutoverWindowCalculator
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CutoverWindowCalculator> _logger;

        /// <summary>Creates a new <see cref="CutoverWindowCalculator"/>.</summary>
        /// <param name="configuration">Configuration supplying the <c>IncrementalMigration:Cutover*</c> assumptions.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public CutoverWindowCalculator(IConfiguration configuration, ILogger<CutoverWindowCalculator> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Calculates the estimated cutover downtime window from the incremental sync estimate.
        /// </summary>
        /// <param name="syncEstimate">The #135 incremental sync estimate. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="CutoverWindowEstimate"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="syncEstimate"/> is <c>null</c>.</exception>
        public CutoverWindowEstimate Calculate(IncrementalSyncEstimate syncEstimate)
        {
            ArgumentNullException.ThrowIfNull(syncEstimate);

            var appStopMinutes = Math.Max(0, _configuration.GetValue("IncrementalMigration:CutoverAppStopMinutes", 5.0));
            var validationMinutes = Math.Max(0, _configuration.GetValue("IncrementalMigration:CutoverValidationMinutes", 15.0));
            var switchMinutes = Math.Max(0, _configuration.GetValue("IncrementalMigration:CutoverConnectionSwitchMinutes", 5.0));
            var bufferPercent = Math.Max(0, _configuration.GetValue("IncrementalMigration:CutoverSafetyBufferPercent", 20.0));
            var targetMinutes = Math.Max(1, _configuration.GetValue("IncrementalMigration:CutoverTargetDowntimeMinutes", 30.0));
            var parallelismPercent = Math.Clamp(_configuration.GetValue("IncrementalMigration:CutoverDrainParallelismPercent", 100.0), 0, 100);

            var quiesce = TimeSpan.FromMinutes(appStopMinutes);
            var validation = TimeSpan.FromMinutes(validationMinutes);
            var connectionSwitch = TimeSpan.FromMinutes(switchMinutes);
            var fixedOverhead = quiesce + validation + connectionSwitch;
            var bufferMultiplier = 1.0 + (bufferPercent / 100.0);

            var estimate = new CutoverWindowEstimate
            {
                AppQuiesceDuration = quiesce,
                DataValidationDuration = validation,
                ConnectionSwitchDuration = connectionSwitch,
                SafetyBufferPercent = bufferPercent,
                FixedOverheadDuration = fixedOverhead,
                MinimumKnownDowntime = Scale(fixedOverhead, bufferMultiplier),
                TargetDowntime = TimeSpan.FromMinutes(targetMinutes),
                DrainParallelismPercent = parallelismPercent,
            };

            AddAssumptions(estimate, targetMinutes, parallelismPercent);

            foreach (var container in syncEstimate.Containers)
            {
                estimate.Containers.Add(EstimateContainer(container, syncEstimate.SyncInterval));
            }

            if (syncEstimate.Containers.Count == 0)
            {
                // Nothing to drain: the window is just the fixed overhead.
                estimate.FinalSyncDrainDuration = TimeSpan.Zero;
                estimate.ParallelDrainDuration = TimeSpan.Zero;
                estimate.FullyContendedDrainDuration = TimeSpan.Zero;
                estimate.DrainBounded = true;
                estimate.Feasibility = CutoverFeasibility.Feasible;
                estimate.TotalDowntime = estimate.MinimumKnownDowntime;
                estimate.Risk = ClassifyRisk(estimate.TotalDowntime.Value, estimate.TargetDowntime);
                estimate.Notes.Add("No containers to synchronize; the cutover window is limited to fixed overhead.");
                return estimate;
            }

            estimate.ContainersRequiringPreCutoverCatchUp = estimate.Containers
                .Where(c => !c.DrainBounded)
                .Select(c => c.ContainerName)
                .ToList();

            if (estimate.ContainersRequiringPreCutoverCatchUp.Count > 0)
            {
                estimate.FinalSyncDrainDuration = null;
                estimate.TotalDowntime = null;
                estimate.DrainBounded = false;
                estimate.Feasibility = CutoverFeasibility.RequiresPreCutoverCatchUp;
                estimate.Risk = CutoverDowntimeRisk.Unknown;
                estimate.Notes.Add(
                    $"{estimate.ContainersRequiringPreCutoverCatchUp.Count} container(s) have unbounded pre-cutover " +
                    "lag or unknown incremental capacity. Achieve zero change-feed lag (catch-up) on these before " +
                    "cutover, then re-estimate; until then only the fixed-overhead floor is bounded.");
                _logger.LogInformation(
                    "Cutover window requires pre-cutover catch-up for {Count} container(s); only fixed overhead is bounded",
                    estimate.ContainersRequiringPreCutoverCatchUp.Count);
                return estimate;
            }

            // Drain bounds: fully-parallel (slowest container) is the best case; fully-contended (serial
            // sum) is the worst case. The reported drain blends between them by the configured parallelism.
            var perContainerDrainSeconds = estimate.Containers
                .Select(c => c.ResidualDrainDuration!.Value.TotalSeconds)
                .ToList();
            var parallelSeconds = perContainerDrainSeconds.DefaultIfEmpty(0).Max();
            var serialSeconds = perContainerDrainSeconds.Sum();
            var finalDrainSeconds = parallelSeconds + ((serialSeconds - parallelSeconds) * (1.0 - (parallelismPercent / 100.0)));

            var finalDrain = SafeFromSeconds(finalDrainSeconds);

            estimate.ParallelDrainDuration = SafeFromSeconds(parallelSeconds);
            estimate.FullyContendedDrainDuration = SafeFromSeconds(serialSeconds);
            estimate.FinalSyncDrainDuration = finalDrain;
            estimate.DrainBounded = true;
            estimate.Feasibility = CutoverFeasibility.Feasible;
            estimate.TotalDowntime = Scale(fixedOverhead + finalDrain, bufferMultiplier);
            estimate.Risk = ClassifyRisk(estimate.TotalDowntime.Value, estimate.TargetDowntime);

            if (parallelismPercent < 100 && serialSeconds > parallelSeconds)
            {
                estimate.Notes.Add(
                    $"Drain parallelism assumed at {parallelismPercent:0.##}%: target contention pushes the final-sync " +
                    $"drain toward the fully-contended bound (~{estimate.FullyContendedDrainDuration:hh\\:mm\\:ss}). " +
                    "Provision additional target throughput to approach the fully-parallel bound.");
            }

            _logger.LogInformation(
                "Cutover window estimated: total {Total}, risk {Risk}, feasibility {Feasibility}",
                estimate.TotalDowntime, estimate.Risk, estimate.Feasibility);

            return estimate;
        }

        private static ContainerCutoverEstimate EstimateContainer(
            ContainerIncrementalSyncEstimate container,
            TimeSpan syncInterval)
        {
            var result = new ContainerCutoverEstimate
            {
                ContainerName = container.ContainerName,
                DrainCapacityDocsPerSecond = container.EstimatedIncrementalCapacityDocsPerSecond,
            };

            // Worst-case residual since the last completed checkpoint: churn over one steady-state sync
            // lag (interval plus one processing cycle). Falls back to the sync interval when lag is unset.
            var lag = container.EstimatedSteadyStateSyncLag > TimeSpan.Zero
                ? container.EstimatedSteadyStateSyncLag
                : syncInterval;
            result.ResidualBacklogDocuments =
                (long)Math.Round(Math.Max(0, container.EstimatedChangedDocumentsPerSecond) * lag.TotalSeconds);

            // Unbounded pre-cutover lag (unsustainable) or unknown capacity ⇒ drain cannot be bounded,
            // unless there is genuinely nothing to drain.
            var capacity = container.EstimatedIncrementalCapacityDocsPerSecond;
            var capacityKnown = container.InitialLoadThroughputKnown && capacity > 0 &&
                                !double.IsNaN(capacity) && !double.IsInfinity(capacity);

            if (result.ResidualBacklogDocuments <= 0)
            {
                result.ResidualDrainDuration = TimeSpan.Zero;
                result.DrainBounded = true;
                if (container.DocumentCount == 0)
                {
                    result.Notes.Add("Container has no documents; nothing to drain at cutover.");
                }
                else
                {
                    result.Notes.Add("No estimated residual backlog at cutover.");
                }
                return result;
            }

            if (!container.SteadyStateSustainable)
            {
                result.ResidualDrainDuration = null;
                result.DrainBounded = false;
                result.Notes.Add(
                    "Steady-state sync is unsustainable, so change-feed lag grows without bound before cutover; " +
                    "the residual at quiesce is unbounded. Increase capacity/parallelism to reach zero lag first.");
                return result;
            }

            if (!capacityKnown)
            {
                result.ResidualDrainDuration = null;
                result.DrainBounded = false;
                result.Notes.Add("Incremental capacity is unknown; the residual drain cannot be bounded.");
                return result;
            }

            var drainSeconds = result.ResidualBacklogDocuments / capacity;
            result.ResidualDrainDuration = SafeFromSeconds(drainSeconds);
            result.DrainBounded = true;
            return result;
        }

        private static void AddAssumptions(CutoverWindowEstimate estimate, double targetMinutes, double parallelismPercent)
        {
            estimate.Assumptions.Add(
                "The source is quiesced (read-only / maintenance mode) for the duration of the window, so no new " +
                "changes arrive and the residual change-feed backlog drains at the estimated incremental capacity.");
            estimate.Assumptions.Add(
                "Residual backlog is the worst-case churn accumulated since the last completed sync checkpoint " +
                "(one steady-state sync lag); it excludes any pre-existing unbounded lag.");
            estimate.Assumptions.Add(
                $"Drain parallelism is assumed at {parallelismPercent:0.##}%: the reported final-sync drain blends the " +
                "fully-parallel bound (slowest container) and the fully-contended bound (serial sum across containers).");
            estimate.Assumptions.Add(
                "Target SQL schema and indexes are assumed already provisioned and write-ready before cutover.");
            estimate.Assumptions.Add(
                "Excludes application restart/warmup, DNS TTL / connection-pool recycle, validation-failure retries, " +
                "and the rollback decision point — budget additional operational time for these.");
            estimate.Assumptions.Add(
                $"Risk is assessed against a target downtime of {targetMinutes:0.##} minute(s) " +
                "(Low ≤ target, Moderate ≤ 2× target, High > 2× target).");
        }

        private static CutoverDowntimeRisk ClassifyRisk(TimeSpan total, TimeSpan target)
        {
            if (total <= target)
            {
                return CutoverDowntimeRisk.Low;
            }

            return total <= target + target ? CutoverDowntimeRisk.Moderate : CutoverDowntimeRisk.High;
        }

        private static TimeSpan Scale(TimeSpan value, double multiplier)
            => SafeFromSeconds(value.TotalSeconds * multiplier);

        private static TimeSpan SafeFromSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0)
            {
                return TimeSpan.Zero;
            }

            // Guard against TimeSpan overflow for pathological inputs.
            const double maxSeconds = 365.0 * 24 * 60 * 60 * 1000; // ~1000 years
            if (double.IsInfinity(seconds) || seconds > maxSeconds)
            {
                return TimeSpan.FromSeconds(maxSeconds);
            }

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
