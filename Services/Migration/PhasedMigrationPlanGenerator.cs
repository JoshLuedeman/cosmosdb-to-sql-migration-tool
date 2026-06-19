using CosmosToSqlAssessment.Models.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Migration
{
    /// <summary>
    /// Synthesizes a phased incremental (change-feed-based) Cosmos DB → SQL migration plan (#137 of parent
    /// #69) from the already-computed change-feed availability (#134), incremental sync estimate (#135), and
    /// cutover window (#136). Pure composition; performs no live Cosmos DB calls.
    /// </summary>
    /// <remarks>
    /// The plan follows the parent #69 phase structure (Initial Bulk Load → Incremental Sync &amp;
    /// Stabilization → Cutover Preparation → Cutover → Verification &amp; Decommission). Elapsed preparation
    /// time and business downtime are reported separately so they are never conflated. All durations are
    /// heuristic planning estimates, not guaranteed SLAs.
    /// </remarks>
    public sealed class PhasedMigrationPlanGenerator
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<PhasedMigrationPlanGenerator> _logger;

        /// <summary>Creates a new <see cref="PhasedMigrationPlanGenerator"/>.</summary>
        /// <param name="configuration">Configuration supplying the <c>IncrementalMigration:*</c> assumptions.</param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public PhasedMigrationPlanGenerator(IConfiguration configuration, ILogger<PhasedMigrationPlanGenerator> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Generates the phased migration plan from the incremental migration analysis.
        /// </summary>
        /// <param name="analysis">The aggregate holding the #134/#135/#136 analyses. Must not be <c>null</c>.</param>
        /// <returns>A populated <see cref="PhasedMigrationPlan"/>.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="analysis"/> is <c>null</c>.</exception>
        public PhasedMigrationPlan Generate(IncrementalMigrationAnalysis analysis)
        {
            ArgumentNullException.ThrowIfNull(analysis);

            var changeFeed = analysis.ChangeFeed;
            var sync = analysis.SyncEstimate;
            var cutover = analysis.CutoverWindow;

            var soakIntervals = Math.Max(1, _configuration.GetValue("IncrementalMigration:IncrementalSyncSoakIntervals", 4));
            var soak = TimeSpan.FromSeconds(sync.SyncInterval.TotalSeconds * soakIntervals);

            var plan = new PhasedMigrationPlan();

            var ttlDeleteContainers = changeFeed.Containers
                .Where(c => c.KnownServerSideTtlDeletes)
                .Select(c => c.ContainerName)
                .ToList();

            // Phase durations (separated: elapsed preparation vs business downtime).
            var initialLoad = sync.InitialLoadDuration;
            var catchUp = sync.EstimatedBacklogCatchUpAfterInitialLoad;
            TimeSpan? phase2 = catchUp.HasValue ? catchUp.Value + soak : null;

            plan.Phases.Add(BuildInitialLoadPhase(initialLoad));
            plan.Phases.Add(BuildIncrementalSyncPhase(phase2, catchUp, soak, soakIntervals, sync, ttlDeleteContainers));
            plan.Phases.Add(BuildCutoverPrepPhase(sync, cutover));
            plan.Phases.Add(BuildCutoverPhase(cutover));
            plan.Phases.Add(BuildVerificationPhase());

            // Elapsed preparation = initial load + incremental catch-up/stabilization (sequential).
            plan.EstimatedElapsedPreparationDuration = phase2.HasValue ? initialLoad + phase2.Value : null;
            plan.EstimatedBusinessDowntime = cutover.TotalDowntime;
            plan.MinimumBusinessDowntime = cutover.MinimumKnownDowntime;

            EvaluateReadiness(plan, changeFeed, sync, cutover, ttlDeleteContainers);
            AddWarningsAndAssumptions(plan, changeFeed, sync, cutover, ttlDeleteContainers, soakIntervals);

            _logger.LogInformation(
                "Phased migration plan generated: readiness {Readiness}, {PhaseCount} phases",
                plan.OverallReadiness, plan.Phases.Count);

            return plan;
        }

        private static MigrationPlanPhase BuildInitialLoadPhase(TimeSpan initialLoad) => new()
        {
            Order = 1,
            Name = "Initial Bulk Load",
            Objective = "Copy the full existing dataset from Cosmos DB to the Azure SQL target.",
            PrimaryTooling = "Azure Data Factory Copy activity / mapping data flow.",
            EstimatedDuration = initialLoad,
            DurationBasis = "Reuses the Data Factory initial-load estimate for the whole database.",
            Steps =
            {
                "Provision the Azure SQL target schema, keys, and indexes so it is write-ready before loading.",
                "Define a deterministic key mapping from Cosmos item ids / partition keys to SQL primary keys.",
                "Build ADF pipelines that flatten Cosmos JSON to the SQL schema (type conversions, null handling).",
                "Run a pilot load on a representative subset; validate mappings and measure throughput.",
                "Execute the full bulk load, monitoring for errors and throughput bottlenecks.",
            },
            EntryCriteria =
            {
                "Target Azure SQL database provisioned with approved schema and security.",
                "Source/target connectivity validated; ADF integration runtime available.",
                "Key mapping and type-conversion rules agreed.",
            },
            ExitCriteria =
            {
                "All existing documents loaded into the SQL target.",
                "Row/record counts reconciled between source and target.",
                "Spot-check data validation (profiles, sampled entities) passes.",
            },
            Risks =
            {
                "Schema/type mismatches (Cosmos schema-less → SQL constraints) can fail rows; validate in the pilot.",
                "Bulk load competes with the source workload for RU/s; schedule for low-traffic periods.",
            },
        };

        private static MigrationPlanPhase BuildIncrementalSyncPhase(
            TimeSpan? phase2,
            TimeSpan? catchUp,
            TimeSpan soak,
            int soakIntervals,
            IncrementalSyncEstimate sync,
            IReadOnlyCollection<string> ttlDeleteContainers)
        {
            var phase = new MigrationPlanPhase
            {
                Order = 2,
                Name = "Incremental Sync & Stabilization",
                Objective = "Continuously apply Cosmos DB change-feed changes to the SQL target and reach a stable, low-lag steady state.",
                PrimaryTooling = "Change Feed Processor (or ADF change-feed data flow) with idempotent SQL upserts.",
                EstimatedDuration = phase2,
                DurationBasis = catchUp.HasValue
                    ? $"Backlog catch-up ({catchUp.Value:hh\\:mm\\:ss}) accrued during the initial load, plus a minimum " +
                      $"stabilization soak of {soakIntervals} sync interval(s) (~{soak:hh\\:mm\\:ss}). Stabilization also " +
                      "requires meeting the exit criteria below, so the real duration may be longer."
                    : "Unavailable: at least one container's steady-state change rate is unsustainable at the estimated " +
                      "capacity, so the backlog cannot drain. Increase capacity/parallelism before proceeding.",
                Steps =
                {
                    "Start change-feed processing from the initial-load checkpoint (lease/continuation tracked durably).",
                    "Apply changes as idempotent upserts keyed by the deterministic SQL key (safe to replay duplicates).",
                    "Implement a delete-handling strategy: hard deletes and TTL expirations are NOT emitted by the latest-version feed.",
                    "Stand up lag monitoring and alerting (change-feed lag, failed cycles, change rate vs capacity).",
                    "Run periodic reconciliation (counts, checksums/hashes where feasible, sampled entity comparison).",
                },
                EntryCriteria =
                {
                    "Initial bulk load complete and reconciled.",
                    "Change-feed processing host / ADF trigger deployed with durable checkpoints.",
                    "Lag monitoring and alerting in place.",
                },
                ExitCriteria =
                {
                    $"Change-feed lag stays below the agreed threshold for at least {soakIntervals} consecutive sync interval(s).",
                    "No failed sync cycles during the observation window.",
                    "Steady-state change rate remains below the sustainable incremental capacity.",
                    "Delete/update handling validated; reconciliation sample passes.",
                },
                Risks =
                {
                    "Latest-version change feed does not emit deletes; a separate delete-reconciliation mechanism is required.",
                    "A fixed soak is a minimum only; production traffic patterns may require a longer observation window.",
                },
            };

            if (!sync.SteadyStateSustainable)
            {
                phase.Risks.Add(
                    "Steady-state sync is estimated as UNSUSTAINABLE — change rate meets/exceeds capacity. Increase " +
                    "provisioned throughput/parallelism or shorten the sync interval before relying on incremental sync.");
            }

            if (ttlDeleteContainers.Count > 0)
            {
                phase.Risks.Add(
                    $"{ttlDeleteContainers.Count} container(s) have TTL-based server-side deletes the latest-version feed " +
                    "won't surface (" + string.Join(", ", ttlDeleteContainers) + "). Verify all-versions-and-deletes mode " +
                    "or design an out-of-band delete-reconciliation process.");
            }

            return phase;
        }

        private static MigrationPlanPhase BuildCutoverPrepPhase(IncrementalSyncEstimate sync, CutoverWindowEstimate cutover)
        {
            var deltaWindow = sync.EstimatedSteadyStateSyncLag > TimeSpan.Zero
                ? $"~{sync.EstimatedSteadyStateSyncLag:hh\\:mm\\:ss}"
                : "one sync interval";

            return new MigrationPlanPhase
            {
                Order = 3,
                Name = "Cutover Preparation",
                Objective = "Rehearse the cutover, prove reconciliation, and stage the rollback plan so the go/no-go decision is data-driven.",
                PrimaryTooling = "ADF / change-feed pipeline, staging application environment, reconciliation tooling.",
                EstimatedDuration = null,
                DurationBasis = $"Operationally scheduled (not modeled). The expected residual delta at quiesce is {deltaWindow}.",
                Steps =
                {
                    "Point a staging/test instance of the application at the SQL target and validate operational semantics.",
                    "Rehearse the write-freeze and final-sync drill end-to-end; time it.",
                    "Author and test the rollback runbook (see rollback guidance) including a maximum rollback window.",
                    "Agree go/no-go criteria and the cutover decision gate with stakeholders.",
                    "Schedule and communicate the maintenance window.",
                },
                EntryCriteria =
                {
                    "Incremental sync stable at low lag (Phase 2 exit criteria met).",
                    "Reconciliation method automated and passing.",
                },
                ExitCriteria =
                {
                    "Staging application validated against the SQL target.",
                    "Rollback runbook tested; go/no-go criteria agreed.",
                    "Maintenance window scheduled and communicated.",
                },
                Risks =
                {
                    "Skipping a timed dress rehearsal makes the cutover-window estimate unreliable.",
                    cutover.Feasibility == CutoverFeasibility.RequiresPreCutoverCatchUp
                        ? "Cutover currently requires pre-cutover catch-up: do not schedule the window until lag is drained to zero."
                        : "Confirm the estimated cutover window fits the agreed downtime budget before committing.",
                },
                RollbackGuidance =
                    "Prepare rollback BEFORE the cutover. A simple 'repoint to Cosmos' is only safe while writes are still " +
                    "routed exclusively to Cosmos (i.e. before SQL is made writable). Once the SQL target accepts writes, " +
                    "rollback requires a documented reverse-sync/compensation plan or must be accepted as 'restore service " +
                    "to Cosmos with possible data loss and manual reconciliation'. Define a maximum rollback window.",
            };
        }

        private static MigrationPlanPhase BuildCutoverPhase(CutoverWindowEstimate cutover)
        {
            var bounded = cutover.TotalDowntime.HasValue;
            return new MigrationPlanPhase
            {
                Order = 4,
                Name = "Cutover",
                Objective = "Quiesce the source, drain the final residual changes, switch the application to SQL, and validate before resuming writes.",
                PrimaryTooling = "Application connection/DNS switch, change-feed final drain, validation scripts.",
                EstimatedDuration = cutover.TotalDowntime,
                DurationBasis = bounded
                    ? $"Estimated cutover downtime window {cutover.TotalDowntime:hh\\:mm\\:ss} (risk {cutover.Risk}): quiesce + " +
                      "final-sync drain + validation + connection switch + safety buffer."
                    : $"Full window unavailable until pre-cutover catch-up is achieved; the known floor (fixed overhead with " +
                      $"buffer) is ~{cutover.MinimumKnownDowntime:hh\\:mm\\:ss}.",
                Steps =
                {
                    "Quiesce the application / set the source to read-only so no new changes are generated.",
                    "Run the final change-feed drain until residual lag reaches zero (verify, do not assume).",
                    "Run the reconciliation gate (counts/checksums/sample) — this is the go/no-go decision point.",
                    "Switch the application connection string / DNS to the SQL target.",
                    "Run smoke and business-validation tests before re-enabling writes.",
                },
                EntryCriteria =
                {
                    "Cutover preparation complete; rollback runbook ready.",
                    "Go decision recorded at the decision gate.",
                },
                ExitCriteria =
                {
                    "Final residual change-feed lag confirmed at zero.",
                    "Reconciliation gate passed; smoke/business tests green.",
                    "Application operating against SQL with writes enabled; stakeholder sign-off.",
                },
                Risks =
                {
                    "Enabling SQL writes makes rollback non-trivial (state can diverge); treat the decision gate as a hard stop.",
                    bounded
                        ? "Downtime overruns if the final drain or validation takes longer than estimated; the safety buffer mitigates but does not guarantee."
                        : "The downtime window is unbounded until lag is drained pre-cutover; do not commit a maintenance window yet.",
                },
                RollbackGuidance =
                    "If the reconciliation gate or smoke tests fail BEFORE writes are enabled on SQL, abort and keep serving " +
                    "from Cosmos (low risk). If failure is detected AFTER SQL writes are enabled, execute the reverse-sync/" +
                    "compensation plan or accept restoring service to Cosmos with possible loss of SQL-side writes. Never " +
                    "silently repoint to Cosmos once SQL has accepted writes.",
            };
        }

        private static MigrationPlanPhase BuildVerificationPhase() => new()
        {
            Order = 5,
            Name = "Verification & Decommission",
            Objective = "Confirm sustained healthy operation on SQL, then retire the migration infrastructure and the source.",
            PrimaryTooling = "Application monitoring/alerting, reconciliation tooling, Azure resource management.",
            EstimatedDuration = null,
            DurationBasis = "Operationally determined (a post-cutover monitoring soak before decommissioning).",
            Steps =
            {
                "Monitor application health, latency, and error rates against SQL for an agreed soak period.",
                "Run a final full reconciliation between the (now frozen) Cosmos source and SQL.",
                "Keep the Cosmos source and pipelines available as a fallback until the soak passes.",
                "Archive Cosmos backups; decommission change-feed processors, ADF pipelines, and interim resources.",
                "Capture lessons learned and update the runbook.",
            },
            EntryCriteria =
            {
                "Cutover complete; application live on SQL.",
            },
            ExitCriteria =
            {
                "Monitoring soak passed with no data-integrity or performance regressions.",
                "Final reconciliation clean; stakeholder acceptance received.",
                "Interim resources decommissioned; source retired or archived per policy.",
            },
            Risks =
            {
                "Decommissioning the source before the soak passes removes the safe fallback — keep it until sign-off.",
            },
            RollbackGuidance =
                "Retain the Cosmos source and pipelines until the monitoring soak passes so a fallback remains possible; " +
                "after decommission, rollback is no longer a simple option.",
        };

        private static void EvaluateReadiness(
            PhasedMigrationPlan plan,
            ChangeFeedAvailabilityAnalysis changeFeed,
            IncrementalSyncEstimate sync,
            CutoverWindowEstimate cutover,
            IReadOnlyCollection<string> ttlDeleteContainers)
        {
            var hasScope = changeFeed.Containers.Count > 0 || sync.Containers.Count > 0;
            if (!hasScope)
            {
                plan.OverallReadiness = MigrationReadiness.Unknown;
                plan.ReadinessFactors.Add("No containers found in scope; nothing to migrate incrementally.");
                return;
            }

            // Blocking conditions ⇒ NotReady (sync infeasibility wins over a feasible cutover).
            var blocking = new List<string>();
            if (sync.OverallRisk == SyncSustainabilityRisk.Unsustainable || !sync.SteadyStateSustainable)
            {
                blocking.Add("Steady-state incremental sync is estimated as unsustainable at the current capacity/change rate.");
            }

            if (!changeFeed.AllContainersSupportLatestVersionIncrementalSync)
            {
                blocking.Add("One or more containers cannot support latest-version change-feed incremental sync.");
            }

            if (blocking.Count > 0)
            {
                plan.OverallReadiness = MigrationReadiness.NotReady;
                plan.ReadinessFactors.AddRange(blocking);
                return;
            }

            // Caveats ⇒ ReadyWithCaveats.
            var caveats = new List<string>();
            if (cutover.Feasibility == CutoverFeasibility.RequiresPreCutoverCatchUp)
            {
                caveats.Add("Cutover requires draining change-feed lag to zero before the window can be bounded.");
            }

            if (ttlDeleteContainers.Count > 0 || changeFeed.AnyContainerHasKnownServerSideDeletes)
            {
                caveats.Add("TTL-based server-side deletes are not surfaced by the latest-version feed; delete fidelity needs manual verification/design.");
            }

            if (sync.OverallRisk == SyncSustainabilityRisk.High ||
                cutover.Risk is CutoverDowntimeRisk.High or CutoverDowntimeRisk.Moderate)
            {
                caveats.Add("Elevated sync and/or cutover risk; validate capacity and downtime budget with a dress rehearsal.");
            }

            plan.OverallReadiness = caveats.Count > 0 ? MigrationReadiness.ReadyWithCaveats : MigrationReadiness.Ready;
            plan.ReadinessFactors.AddRange(caveats.Count > 0
                ? caveats
                : new List<string> { "Change-feed availability, sync sustainability, and cutover feasibility are all healthy." });
        }

        private static void AddWarningsAndAssumptions(
            PhasedMigrationPlan plan,
            ChangeFeedAvailabilityAnalysis changeFeed,
            IncrementalSyncEstimate sync,
            CutoverWindowEstimate cutover,
            IReadOnlyCollection<string> ttlDeleteContainers,
            int soakIntervals)
        {
            // Consistency diagnostics so the plan cannot be silently misread.
            if (!sync.SteadyStateSustainable && cutover.Feasibility == CutoverFeasibility.Feasible)
            {
                plan.PlanWarnings.Add(
                    "Inconsistent upstream signals: steady-state sync is unsustainable yet the cutover appears feasible. " +
                    "Sync infeasibility governs — the plan is treated as NotReady.");
            }

            if (sync.EstimatedBacklogCatchUpAfterInitialLoad is null)
            {
                plan.PlanWarnings.Add("Backlog catch-up is unavailable because at least one container is unsustainable; the preparation duration is unknown.");
            }

            if (cutover.TotalDowntime is null)
            {
                plan.PlanWarnings.Add($"Cutover downtime is unavailable; only the minimum fixed overhead (~{cutover.MinimumKnownDowntime:hh\\:mm\\:ss}) is known.");
            }

            if (ttlDeleteContainers.Count > 0)
            {
                plan.PlanWarnings.Add($"Change-feed delete fidelity requires manual verification for {ttlDeleteContainers.Count} container(s) with TTL deletes.");
            }

            plan.PlanWarnings.Add("Estimated totals exclude the operationally-scheduled cutover-preparation and verification phases.");

            // Aggregated key risks.
            plan.KeyRisks.Add("The latest-version change feed never emits deletes (hard or TTL); a delete-reconciliation design is mandatory.");
            if (!sync.SteadyStateSustainable)
            {
                plan.KeyRisks.Add("Unsustainable steady-state sync: incremental sync cannot keep pace at the estimated capacity.");
            }
            if (cutover.Feasibility == CutoverFeasibility.RequiresPreCutoverCatchUp)
            {
                plan.KeyRisks.Add("Cutover window is unbounded until pre-cutover catch-up is achieved.");
            }
            plan.KeyRisks.Add("Post-cutover rollback is non-trivial once SQL accepts writes; the decision gate is a hard stop.");

            // Assumptions.
            plan.Assumptions.Add("Elapsed preparation and business downtime are reported separately and must not be summed together.");
            plan.Assumptions.Add($"Stabilization uses a minimum soak of {soakIntervals} sync interval(s) plus lag-threshold exit criteria.");
            plan.Assumptions.Add("Incremental sync applies idempotent upserts keyed by a deterministic Cosmos→SQL key mapping.");
            plan.Assumptions.Add("The SQL target schema and indexes are provisioned and write-ready before the initial load.");
            plan.Assumptions.Add("All duration figures are heuristic planning estimates, not guaranteed SLAs.");

            if (sync.Containers.Count == 1)
            {
                plan.Notes.Add("Single-container migration: concentration risk — the whole migration depends on one container's sync behavior.");
            }
        }
    }
}
