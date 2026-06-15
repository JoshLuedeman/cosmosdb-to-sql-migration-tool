# End-to-End Test Harness

Reusable smoke-test harness for the full **assessment → SQL project generation**
pipeline. Built on top of the mock Azure SDK harness in [`../Mocks/`](../Mocks/README.md).

This is the canonical entry point Wave-2+ parents should use **before
declaring a refactor safe**: stand up an `E2EFixture`, run the assessment,
generate a SQL project, and assert on the on-disk artifacts.

## Pipeline under test

```
CosmosDbAnalysisService            (uses mocked CosmosClient + LogsQueryClient)
  -> SqlMigrationAssessmentService (pure compute)
  -> DataQualityAnalysisService    (uses mocked CosmosClient)
  -> DataFactoryEstimateService    (pure compute)
  -> EITHER SqlProjectGenerationService.GenerateSqlProjectsAsync
            (the path Program.GenerateOutputsAsync uses, produces
             sql-projects/<db>.Database/Tables|Indexes|ForeignKeys|Scripts/)
     OR     SqlProjectIntegrationService.GenerateSqlProjectAsync
            (the path Program.GenerateSqlProjectAsync uses, wraps
             SqlDatabaseProjectService)
```

Both project-generation paths are real production code paths. The
[`E2EPipelineTests`](./E2EPipelineTests.cs) suite exercises each one.

## Quick start

```csharp
using CosmosToSqlAssessment.Tests.EndToEnd;

[Fact]
public async Task My_pipeline_smoke_test()
{
    using var fixture = new E2EFixture()
        .WithDatabase("AppDb", db => db
            .WithContainer("users", c => c
                .WithPartitionKey("/userId").WithThroughput(400)
                .WithDocuments(E2ESampleData.TwoUsers.ToArray()))
            .WithContainer("orders", c => c
                .WithPartitionKey("/orderId").WithThroughput(800)
                .WithDocuments(E2ESampleData.ThreeOrders.ToArray())))
        .WithAzureMonitorMetrics(E2ESampleData.SixHoursOfMetrics)   // optional
        .Build();

    var assessment = await fixture.RunAssessmentAsync("AppDb");

    // Path 1: SqlProjectIntegrationService -> SqlDatabaseProjectService
    var integrationResult = await fixture.GenerateSqlProjectsViaIntegrationAsync(assessment);
    integrationResult.Success.Should().BeTrue();
    File.Exists(integrationResult.Project!.ProjectFilePath).Should().BeTrue();

    // Path 2: SqlProjectGenerationService.GenerateSqlProjectsAsync
    var sqlProjectsDir = await fixture.GenerateSqlProjectsViaGenerationServiceAsync(assessment);
    Directory.Exists(sqlProjectsDir).Should().BeTrue();
}
```

## Required document shape

Documents **must** be `Newtonsoft.Json.Linq.JObject` instances. This is the only
type that satisfies both the dynamic-member access (`obj.id`) **and** the
`ToString()` → valid-JSON requirement that production code relies on. See the
mock harness README for the rationale.

```csharp
var doc = JObject.Parse("{\"id\":\"u1\",\"email\":\"a@example.com\",\"age\":30}");
```

## Azure Monitor metrics

Calling `.WithAzureMonitorMetrics(rows)` does **two** things:

1. Sets the `AzureMonitor:WorkspaceId` and `AzureMonitor:CosmosAccountName`
   configuration keys (the production code short-circuits to default zero
   metrics if either is missing).
2. Injects a `LogsQueryClient` built by
   [`LogsQueryClientMockBuilder`](../Mocks/LogsQueryClientMockBuilder.cs)
   that returns a `LogsQueryResult` containing your synthetic rows.

Each row is a `(DateTimeOffset, double Avg, double Max, double Total)` tuple.
Production averages `Avg`, takes max of `Max`, sums `Total`. Without this call,
`CosmosAnalysis.PerformanceMetrics` will all be zero and a single entry will
appear in `CosmosAnalysis.MonitoringLimitations`.

## Temp directory & cleanup

Each fixture owns a temp subdirectory at `Directory.CreateTempSubdirectory("e2e-")`.
- `GenerateSqlProjectsViaGenerationServiceAsync` writes under
  `<TempRoot>/generation-service/sql-projects/...`.
- `GenerateSqlProjectsViaIntegrationAsync` writes under
  `<TempRoot>/integration-service/...`.

`Dispose` removes the temp root with a short retry loop to tolerate transient
Windows file-lock races. **Always use `using var fixture = ...` so cleanup runs
even on test failure.**

## Granular reuse

The fixture exposes every real service as a public read-only property:

```csharp
fixture.Configuration              // IConfiguration
fixture.CosmosService              // CosmosDbAnalysisService
fixture.SqlAssessmentService       // SqlMigrationAssessmentService
fixture.DataQualityService         // DataQualityAnalysisService
fixture.DataFactoryService         // DataFactoryEstimateService
fixture.SqlProjectService          // SqlProjectGenerationService
fixture.SqlDatabaseProjectService  // SqlDatabaseProjectService
fixture.SqlProjectIntegrationService
fixture.TempRoot                   // path to per-fixture temp dir
```

Use these directly when you want to drive an individual service through a
specific scenario without going through `RunAssessmentAsync`.

## Recommended assertion patterns

- **Validate structure, not text.** Parse the `.sqlproj` with `XDocument.Load`,
  enumerate generated table files with `Directory.EnumerateFiles(... "*.sql"
  ...)`, and check each contains `CREATE TABLE`.
- **Derive expected names from `assessment.SqlAssessment.DatabaseMappings`** —
  do not hard-code `Users.sql`/`Orders.sql` because the sanitisation rules
  may evolve.
- **Avoid** asserting against timestamps, GUIDs, full file text, comment lines,
  or generation order — those are brittle.
- For metrics: assert "> 0" rather than exact values where averages over six
  synthetic rows are involved (rounding is not the point of an E2E test).

## Known limitations

- **Empty containers** are supported but the generated `CREATE TABLE` will be
  near-empty because there are no detected fields. Use at least one container
  with real documents if you need a meaningful schema.
- **Zero-container databases** would crash `DataFactoryEstimateService`
  (`analysis.Containers.Max(...)`). Not a supported scenario — add at least
  one container.
- Behaviour-changing or expensive options like `IncludeOutlierDetection` are
  driven by `DataQualityAnalysisOptions`, which the fixture does not currently
  expose; sub-issue #183 will broaden coverage here on top of this harness.

## Performance budget

Six tests, each touching disk, complete in well under 60 seconds on a typical
developer machine. If a new E2E test pushes this budget, prefer **smaller
samples** (2-3 documents per container) over reducing the assertion surface.

