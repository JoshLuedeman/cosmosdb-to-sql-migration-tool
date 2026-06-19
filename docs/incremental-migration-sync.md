# Incremental Migration & Change Feed Sync Process

This runbook documents how the assessment tool models an **online (near-zero-downtime) migration** from Azure Cosmos DB (SQL/Core API) to Azure SQL using the **Cosmos DB change feed**, and how to operate the sync process the assessment recommends. It corresponds to the *Incremental Migration* worksheet in the Excel report and the *Incremental Migration and Sync Process* section of the Word report.

> All durations the tool produces are **heuristic planning estimates**, not guaranteed SLAs. Validate against a representative test load before committing to a cutover window.

## Why incremental migration

A bulk-only ("lift and shift") migration requires downtime for the entire initial load. For large or write-heavy Cosmos containers that window is often unacceptable. The incremental pattern instead:

1. Performs a **bulk initial load** of existing data while the application keeps running against Cosmos.
2. Continuously **tails the change feed** to replicate creates/updates that happen during and after the initial load.
3. Cuts over during a **short maintenance window** that only needs to drain the small residual backlog accumulated since the last sync checkpoint.

## The five phases

The phased plan (rendered in both reports) follows this structure:

| # | Phase | Objective |
|---|-------|-----------|
| 1 | Initial Bulk Load | Copy all existing documents to Azure SQL (Azure Data Factory). |
| 2 | Incremental Sync & Stabilization | Tail the change feed; drain the post-load backlog; soak until lag is stable. |
| 3 | Cutover Preparation | Freeze schema, finalize validation queries, rehearse the cutover. |
| 4 | Cutover | Quiesce the source, drain the residual backlog, validate, switch connections. |
| 5 | Verification & Decommission | Reconcile row counts, monitor the target, decommission the pipeline. |

The tool separates **elapsed preparation time** (wall-clock time to reach a stable, low-lag state — phases 1–2) from **business downtime** (the cutover maintenance window only — phase 4). These are never conflated.

## Change feed modes

| Mode | Emits | Availability | Use when |
|------|-------|--------------|----------|
| **Latest-version** (a.k.a. incremental) | Most recent version of each created/updated item | Always available on the SQL API | Creates/updates are sufficient and deletes are handled separately. |
| **All-versions-and-deletes** (a.k.a. full-fidelity) | Every intermediate version **and delete tombstones**, within a retention window | Requires an explicit container `ChangeFeedPolicy` retention that the public .NET SDK cannot read | You must replicate deletes/intermediate versions through the feed. |

> **Deletes never appear in the latest-version feed.** Application hard deletes and TTL expirations are invisible to it. The assessment always flags `RequiresDeleteHandlingValidation`. Choose one of: a soft-delete pattern, periodic full reconciliation, or all-versions-and-deletes mode (after confirming retention in the portal/ARM).

### TTL and deletes

If a container has TTL enabled (`DefaultTimeToLive` is set), documents expire **server-side** and are deleted without any latest-version change-feed event. Plan an explicit strategy to age out the corresponding SQL rows. The assessment surfaces `KnownServerSideTtlDeletes` per container and downgrades plan readiness to *ReadyWithCaveats* accordingly.

## Initial load vs incremental sync

The assessment compares the estimated **initial bulk-load throughput** (reused from the Data Factory estimate) against the estimated **incremental sync capacity** to produce a sustainability verdict per container:

- **Utilization %** = steady-state change rate ÷ estimated incremental capacity.
- Risk bands: `Healthy` (<50%), `Moderate` (50–80%), `High` (80–100%), `Unsustainable` (≥100%).
- **Post-load backlog catch-up**: time to drain the changes that accumulated during the initial load. `Unsustainable` containers have no bounded catch-up — the lag grows without bound and is a blocking issue.
- **Steady-state sync lag**: the sync interval plus one processing cycle.

Tune assumptions under the `IncrementalMigration` section of `appsettings.json` (`DailyChangeRatePercent`, `SyncIntervalMinutes`, `IncrementalThroughputFactor`).

## Cutover downtime window

The cutover window models the maintenance window during which the source is quiesced (read-only):

```
downtime = ( app-quiesce + residual-drain + validation + connection-switch ) × (1 + safety-buffer)
```

- **Residual drain** blends a fully-parallel bound (`max(residualᵢ ÷ capacityᵢ)`) and a fully-contended serial bound (`Σ(residualᵢ ÷ capacityᵢ)`) using `CutoverDrainParallelismPercent`.
- A **minimum known downtime floor** (fixed overhead + buffer) is always reported, even when the full window cannot be bounded.
- If any container is unsustainable or has unknown capacity, feasibility is `RequiresPreCutoverCatchUp`: you must drive its lag to zero **before** the window can be bounded.
- Risk is assessed relative to your target RTO (`CutoverTargetDowntimeMinutes`): `Low` ≤ target, `Moderate` ≤ 2× target, `High` > 2× target.

## Time-based partitioning of the SQL target

For large containers the assessment may recommend **time-based table partitioning** in Azure SQL (for manageability — sliding-window aging and partition-level maintenance — not as a guaranteed query-performance win):

- The partition column must be an **immutable creation timestamp**. The Cosmos `_ts` field is the **last-modified** time (mutable) and must **never** be the SQL partition column — updating a partition column forces expensive cross-partition row movement.
- The tool **shortlists** candidate temporal columns with a confidence and mutability-risk rating rather than confidently picking one. A single column is auto-recommended only when exactly one near-universal, high-confidence immutable column exists.
- When no immutable column exists, capture the initial `_ts` once at load time as a stable `InitialLoadTimestamp` column (this is **not** the true creation time; change-feed updates must never mutate it).
- **Sliding-window** aging is only a *consideration* when TTL is enabled, and carries a caveat: Cosmos TTL ages by `_ts` while the partition column ages by creation time, so the two age bases can disagree. Validate before using partition drops for retention.
- Remember Azure SQL partition/index alignment rules: the partition column must be in the clustered index key, any unique/primary key must include it, and a nullable partition column routes NULLs to a boundary partition.

### Initial-load slicing

Parallelize the initial bulk load across **feed ranges** (physical partitions) as an upper bound on workers — subject to RU limits, throttling, hot ranges, and feed-range split/merge at runtime. Within each range you can sub-slice by `_ts` time windows for resumability; size the windows at execution time from the actual `_ts` min/max range (it cannot be sized from static metadata).

## Change Feed Processor

For implementing the continuous sync, the assessment produces **Change Feed Processor (CFP)** guidance (sub-issue #140) — an alternative or complement to the generated Data Factory `_ts`-watermark pipeline. CFP is a push-style library that distributes a container's change feed across worker instances using a **lease container** for coordination and checkpointing.

### Lease container

- Provision a **dedicated** lease container (recommended name `leases`, partition key `/id`).
- The tool suggests a conservative starting throughput of `ChangeFeedProcessorLeaseBaseRUs + ChangeFeedProcessorLeaseRUsPerLease × (total known feed ranges)` RU/s (defaults 400 + 100/lease), clamped to the 400 RU/s minimum, on **autoscale**. This is a starting estimate to monitor (watch for 429s), **not** validated sizing — actual RU is driven by checkpoint frequency, lease renew/acquire cadence, and failovers.
- One lease is taken per **feed range** (≈ physical partition), so feed-range count is the upper bound on useful parallelism.

### Mode selection and delete handling

- **Latest-version** mode delivers creates/updates **at least once** and never emits deletes. The SQL write path must therefore be **idempotent** (upsert/`MERGE` keyed on document id) to tolerate redelivery after a host restart or lease rebalance.
- **All-versions-and-deletes (AVAD)** mode also surfaces delete tombstones and intermediate versions **within the continuous-backup retention window**. It applies to Azure Cosmos DB for **NoSQL only**, requires **continuous backup** and the AVAD feature enabled, cannot start from the beginning or an arbitrary historical time, may be unsupported on accounts with partition-merge history, and needs a **recent** `Microsoft.Azure.Cosmos` SDK (verify current support — it has had preview constraints). Use **isolated lease state** for AVAD (a dedicated lease container or a distinct processor name/lease prefix); never reuse a latest-version checkpoint state.

### Compute and scale-out

- Start with **one** instance and scale out based on the change feed **estimator lag**, host CPU/memory, and SQL write throughput.
- The parallelism ceiling is the feed-range count: the tool reports both a **shared-fleet** ceiling (`max` feed ranges across containers — a single host can run the processors for multiple containers) and an **independent-pools** ceiling (`Σ` feed ranges, one pool per container). When a container's feed-range count is unreadable, its ceiling is **unknown** — determine it at runtime before scaling out.

### Checkpointing

- Default to **automatic per-batch** checkpointing, committed **after** the SQL write succeeds. Combined with at-least-once delivery, idempotent writes make redelivery safe.

### Relationship to the Data Factory pipeline

- The ADF `_ts`-watermark copy is a **scheduled pull** that captures visible creates/updates; deletes need a soft-delete pattern or external reconciliation.
- A latest-version CFP worker is a **continuous push** of creates/updates (still no deletes); an AVAD CFP worker additionally captures deletes within retention.
- Pick **one** incremental path per target table. Do **not** run both the ADF pipeline and a CFP worker into the same SQL target without idempotent upserts and duplicate-boundary handling.

## Operating checklist

1. Confirm the recommended change-feed mode per container; design delete handling.
2. Run the Data Factory bulk initial load.
3. Start the change-feed sync; let the post-load backlog drain and soak until lag is stable.
4. Reconcile counts and validate sample data.
5. Schedule the cutover window using the estimated downtime (and the minimum floor).
6. At cutover: quiesce → drain residual → validate → switch connections.
7. Verify on the target, then decommission the pipeline.
