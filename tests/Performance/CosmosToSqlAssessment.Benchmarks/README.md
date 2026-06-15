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
already covered by the repository `.gitignore`. JSON/Markdown exporters and a stable artifact
layout will be configured in #177.

## Adding a new benchmark

1. Add a class under `Benchmarks/` with `[MemoryDiagnoser]` on the class and one or more
   `[Benchmark]`-attributed methods.
2. Keep benchmarks deterministic and in-memory. Construct any dependencies in a
   `[GlobalSetup]`-attributed method using test doubles — never call live Azure resources.
3. Filter against your new class locally before pushing:
   `--filter "*MyNewBenchmarks*" --job dry`.

See [BenchmarkDotNet docs](https://benchmarkdotnet.org/) for the full attribute / CLI surface.
