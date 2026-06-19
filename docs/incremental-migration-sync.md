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

For implementing the continuous sync, see the Change Feed Processor lease-container and mode guidance produced for sub-issue #140.

## Operating checklist

1. Confirm the recommended change-feed mode per container; design delete handling.
2. Run the Data Factory bulk initial load.
3. Start the change-feed sync; let the post-load backlog drain and soak until lag is stable.
4. Reconcile counts and validate sample data.
5. Schedule the cutover window using the estimated downtime (and the minimum floor).
6. At cutover: quiesce → drain residual → validate → switch connections.
7. Verify on the target, then decommission the pipeline.
