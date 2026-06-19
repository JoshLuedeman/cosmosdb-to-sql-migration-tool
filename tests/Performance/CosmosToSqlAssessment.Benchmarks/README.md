# CosmosToSqlAssessment.Benchmarks

BenchmarkDotNet harness for the Cosmos → SQL migration assessment tool. Owned by the parent
issue [#79](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/79).

This project is **scaffolding only** in its current form (sub-issue #173). Real benchmarks for
the Cosmos analysis, SQL assessment, and report generation services land in sub-issues
#174–#176. Result tracking, the CI workflow, and the project-level performance targets land in
#177–#179.

## Quickstart

> ⚠️ Always run benchmarks with `-c Release`. BenchmarkDotNet will refuse to run a Debug build.

List every benchmark discovered in the assembly:

```bash
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- --list flat
```

Run every benchmark with default settings (slow — full warmup + multiple iterations):

```bash
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- --filter "*"
```

Run a specific benchmark fast — single warmup + single iteration, for plumbing checks:

```bash
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- --filter "*SmokeBenchmarks*" --job dry --warmupCount 1 --iterationCount 1
```

## Artifacts

By default BenchmarkDotNet writes results under
`tests/Performance/CosmosToSqlAssessment.Benchmarks/BenchmarkDotNet.Artifacts/`. That path is
already covered by the repository `.gitignore`. The harness adds `JsonExporter.Full` to the
default config so every run produces
`BenchmarkDotNet.Artifacts/results/{benchmark-class}-report-full.json` with per-benchmark
statistics + allocation data. The `compare-baseline` subcommand (below) reads those files.

## Result tracking

Sub-issue #177 wires in a small baseline tracker that lives entirely inside this project. It is
local-only at present; CI integration arrives in #178.

### Layout

- `baselines/baseline.json` — committed source of truth. Keys are BenchmarkDotNet `FullName`
  strings (parameterised entries look like `Namespace.Class.Method(Size: Small)`).
- `Tracking/BaselineRecord.cs` — POCO shapes (`BaselineFile`, `BenchmarkBaseline`,
  `ComparisonRow`).
- `Tracking/BaselineComparer.cs` — `compare-baseline` subcommand implementation.

### Baseline schema

```json
{
  "schemaVersion": 1,
  "capturedAt": "<ISO-8601 timestamp>",
  "capturedOn": "<machine / OS / runtime description>",
  "captureCommand": "<command used to produce the report>",
  "defaultToleranceFactor": 1.10,
  "defaultAllocationFloorBytes": 1024,
  "benchmarks": {
    "Namespace.Class.Method(Size: Small)": {
      "meanNs": 12345.6,
      "allocatedBytes": 7890,
      "meanToleranceFactor": 1.50
    }
  }
}
```

A benchmark fails the comparison if:

- `actualMean > baselineMean × meanToleranceFactor`, **or**
- `actualAllocated > max(baselineAllocated × allocationToleranceFactor, baselineAllocated + defaultAllocationFloorBytes)`

The allocation floor avoids brittle alarms when a baseline is near zero.
Tolerances are resolved **independently per axis**, so the mean (wall-clock) and
allocation budgets can be tuned separately:

- `meanToleranceFactor` overrides only the mean axis.
- `allocationToleranceFactor` overrides only the allocation axis.
- `toleranceFactor` is a legacy shared override used for an axis only when its
  axis-specific value is absent.
- Anything still unset falls back to `defaultToleranceFactor`.

All per-benchmark overrides are preserved across `--update` runs. The shipped
baseline keeps allocation pinned at the strict `1.10` default everywhere
(allocations are deterministic) and widens only the mean axis for the
I/O-bound macro-benchmarks (`GenerateAssessmentReportAsync_EndToEnd` → `1.50`,
`AssessMigrationAsync_EndToEnd` → `1.20`) and the GC-heavy
`StreamingMemoryProfileBenchmarks.BufferedRetainAllPattern` (1000 & 10000 docs)
→ `1.30`, whose buffer-everything pattern is noisy on wall-clock but whose real
signal — allocation — stays strict at `1.10`.

### Seeding / refreshing the baseline

The baseline is seeded from real CI runs (see the [root README's CI section](../../../README.md#ci-integration)).
To re-seed it locally — or to refresh it after an intentional perf change:

```bash
# 1. Capture a real measurement run (short job — ~minutes, not seconds, but realistic).
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- --filter "*" --job short

# 2. Promote that report to the baseline file. Default --baseline resolves to
#    tests/Performance/CosmosToSqlAssessment.Benchmarks/baselines/baseline.json relative to the
#    benchmark assembly's output directory.
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- compare-baseline --update \
     --report tests/Performance/CosmosToSqlAssessment.Benchmarks/BenchmarkDotNet.Artifacts/results/CosmosToSqlAssessment.Benchmarks.Benchmarks.SmokeBenchmarks-report-full.json
```

Repeat the `--update` invocation for each `*-report-full.json` produced by the run.

### Comparing a new run against the baseline

```bash
dotnet run -c Release \
  --project tests/Performance/CosmosToSqlAssessment.Benchmarks/CosmosToSqlAssessment.Benchmarks.csproj \
  -- compare-baseline \
     --report tests/Performance/CosmosToSqlAssessment.Benchmarks/BenchmarkDotNet.Artifacts/results/CosmosToSqlAssessment.Benchmarks.Benchmarks.SmokeBenchmarks-report-full.json
```

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | All compared benchmarks within tolerance. |
| `1` | At least one benchmark regressed. |
| `2` | Bad invocation, missing/malformed files, or baseline-vs-report key drift. |

> Cross-machine note: BenchmarkDotNet `Mean` numbers are noticeably hardware-dependent. The
> CI baseline is therefore seeded from the GitHub-hosted `ubuntu-latest` runner (the same SKU
> the workflow runs on) and captured as `max(run1, run2)` of two consecutive runs of the same
> commit. The default tolerance is `1.10` (10% headroom); the mean axis is widened per benchmark
> only where shared-runner timing variance demands it (see the table above). If you run locally,
> expect absolute `Mean` numbers to differ — compare deltas, not absolutes.
>
> The CI workflow invokes `compare-baseline` against the same `*-report-full.json`
> artifacts; the contract here is intended to stay stable.

## CI

The `Performance Regression` workflow (`.github/workflows/performance-regression.yml`, added
in sub-issue #178) wires the harness above into GitHub Actions.

- **Triggers**: every PR to `main`; pushes to `main` that touch perf-relevant paths (the
  benchmarks project, the main project's services / reporting / models / SqlProject, the
  csproj/sln/Program.cs, or the workflow file itself); and a `workflow_dispatch` with an
  `update_baseline` boolean input.
- **What it does**: builds the benchmarks project, runs `dotnet run -- --filter "*"`
  (deliberately omitting `--job` so the in-process toolchain pin from `BuildConfig()` is
  guaranteed to apply), then iterates each `*-report-full.json` invoking
  `compare-baseline --report <each>`. CI fails on the worst exit code: `1` if any benchmark
  regresses past tolerance, `2` if the comparison itself errored (drift, malformed report,
  missing reports).
- **Refreshing the baseline from CI**: trigger the workflow manually
  (`Actions → Performance Regression → Run workflow`) with `update_baseline=true`. The
  comparison step is skipped; instead the workflow runs `compare-baseline --update` for
  every report and uploads the refreshed `baseline.json` as an artifact named
  `refreshed-baseline`. Download it, review the diff, and commit it in a follow-up PR.
  (The workflow intentionally does **not** auto-commit — it stays read-only.)
- **Artifacts**: the full `BenchmarkDotNet.Artifacts/` directory is uploaded as
  `benchmark-artifacts` (retention 14 days) on every run, so the raw reports and HTML/CSV
  exports are available for inspection.

> Phase C of parent #79 will register `Benchmark regression` as a required status check on
> `main`. That's why the PR trigger has no `paths:` filter — a required check that can't
> report on certain PRs would otherwise leave them blocked.

## Adding a new benchmark

1. Add a class under `Benchmarks/` with `[MemoryDiagnoser]` on the class and one or more
   `[Benchmark]`-attributed methods.
2. Keep benchmarks deterministic and in-memory. Construct any dependencies in a
   `[GlobalSetup]`-attributed method using test doubles — never call live Azure resources.
3. Filter against your new class locally before pushing:
   `--filter "*MyNewBenchmarks*" --job dry`.

See [BenchmarkDotNet docs](https://benchmarkdotnet.org/) for the full attribute / CLI surface.
