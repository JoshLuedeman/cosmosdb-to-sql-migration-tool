# Azure SDK Mock Harness

This folder contains a **reusable, hermetic mock harness** for the Azure SDKs
this project depends on. It lets every unit and integration test run with **no
real Azure resources** — no network, no auth, no Cosmos account, no Log
Analytics workspace.

The harness was introduced by sub-issue **#180** under parent **#125** and is
the foundation every Wave-2+ parent should build on **before** refactoring
production code or adding new service paths.

---

## Quick start

```csharp
using CosmosToSqlAssessment.Tests.Mocks;
using Newtonsoft.Json.Linq;

var cosmosClient = new CosmosClientMockBuilder()
    .WithDatabase("AppDb", db => db
        .WithContainer("orders", c => c
            .WithPartitionKey("/orderId")
            .WithThroughput(400)
            .WithDocuments(
                JObject.Parse("{ \"id\": \"o1\", \"total\": 42.0 }"),
                JObject.Parse("{ \"id\": \"o2\", \"total\": 9.99 }"))))
    .Build();

var logsClient = new LogsQueryClientMockBuilder()
    .WithMetricsRows(new[]
    {
        (DateTimeOffset.UtcNow.AddHours(-1), avg: 12.0, max: 30.0, total: 120.0)
    })
    .Build();

var service = new CosmosDbAnalysisService(
    MockConfiguration.Object,
    CreateMockLogger<CosmosDbAnalysisService>().Object,
    cosmosClient,
    logsClient);

var result = await service.AnalyzeDatabaseAsync("AppDb", CancellationToken.None);
```

`new CosmosDbAnalysisService(IConfiguration, ILogger, CosmosClient, LogsQueryClient?)`
is an **`internal` test-friendly constructor** exposed to the test assembly via
`[InternalsVisibleTo("CosmosToSqlAssessment.Tests")]`. Production DI always uses
the public constructor that builds Azure clients from `IConfiguration`.

`DataQualityAnalysisService` has the same shape:

```csharp
new DataQualityAnalysisService(IConfiguration, ILogger, CosmosClient, DataQualityAnalysisOptions?)
```

---

## What's in the box

| File | What it gives you |
| --- | --- |
| `CosmosClientMockBuilder.cs` | Top-level fluent builder. Composes `Database` / `Container` / documents / throughput / indexing policy. |
| `MockFeedIterator.cs` | `OfDocuments<T>(items)`, `OfPages<T>(pages)`, `Empty<T>()`, `ThrowsOnRead<T>(ex)`. |
| `MockFeedResponse.cs` | Concrete `FeedResponse<T>` that is **safely re-enumerable** (production code does `Count()` then `foreach` on the same instance). |
| `MockContainerResponse.cs` / `MockDatabaseResponse.cs` | Factories used internally by the builder; you usually don't touch them. |
| `CosmosExceptionFactory.cs` | `Throttled()`, `ServiceUnavailable()`, `Timeout()`, `Forbidden()`, `NotFound()`, `BadRequest()`. Used by #184 retry tests. |
| `LogsQueryClientMockBuilder.cs` | Canned `LogsQueryResult` from `MonitorQueryModelFactory`. Wraps in `Response.FromValue(...)`. |
| `MockHarnessTests.cs` / `LogsQueryClientMockBuilderTests.cs` | Meta-tests that pin the harness contract. |

---

## Conventions

### Document shape: always use `JObject`
Production code uses `dynamic` in two incompatible ways:

1. **Member access** — `container.id`, `doc.id` (compiled against `dynamic`).
2. **JSON round-trip** — `dynamic.ToString()` is parsed back via
   `JsonDocument.Parse(...)`.

`Newtonsoft.Json.Linq.JObject` is the **only** type that satisfies both: it
implements `IDynamicMetaObjectProvider` for member access **and** `ToString()`
returns valid JSON. The builder enforces this signature on `WithDocuments`.

### Throughput
`Container.ReadThroughputAsync(cancellationToken: ct)` binds to the
`Task<int?>` overload. The builder targets that overload directly:

```csharp
.WithContainer("c", c => c.WithThroughput(400))
```

Pass `null` to simulate "throughput unknown" / shared-database mode.

### Transient errors
Use `CosmosExceptionFactory` plus a `With…Error` setter on the container builder:

```csharp
.WithContainer("c", c => c
    .WithThroughputError(CosmosExceptionFactory.Forbidden())
    .WithQueryError(CosmosExceptionFactory.Throttled()))
```

For pure feed-level errors:

```csharp
var iterator = MockFeedIterator.ThrowsOnRead<JObject>(CosmosExceptionFactory.Throttled());
```

### Multi-page queries
```csharp
var iterator = MockFeedIterator.OfPages(new[]
{
    new List<JObject> { doc1, doc2 },
    new List<JObject> { doc3 }
});
```

### Missing databases / containers
Calling `GetDatabase("Unknown")` on a builder that did not configure `"Unknown"`
returns a Database whose `ReadAsync` throws `CosmosException(NotFound)`. Same for
`GetContainer("missing")`. This lets you exercise error-handling paths without
extra setup.

---

## Adding new mocks (Wave-2+ workflow)

When your parent introduces a new Azure SDK usage:

1. **Don't** instantiate the SDK client inside a service constructor — that's
   what made `CosmosDbAnalysisService` and `DataQualityAnalysisService`
   un-mockable historically. Add an `internal` test-friendly ctor that accepts
   the client, and rely on `[InternalsVisibleTo]` for the test assembly.
2. Add a builder file here (e.g. `BlobContainerClientMockBuilder.cs`) using the
   same fluent pattern.
3. Add a `…BuilderTests.cs` next to it pinning the contract.
4. Update this README with the new builder and a quick-start snippet.

The full overload signature must be reproduced in Moq setups — `It.IsAny<T>()`
for each slot, including optional parameters that compile to `null` at the
call site. See `CosmosClientMockBuilder.SetupItemQueryIterator` for examples.

---

## Why this harness is required for Wave-2+ work

Without it, the coverage runsettings has to **exclude** every service that
touches an Azure client (currently `CosmosDbAnalysisService` and
`DataQualityAnalysisService`). That hides bugs and makes any refactor a coin
flip. Future parents — multi-agent orchestration, incremental migration,
adaptive loops — will land **more** Azure-touching code; the harness is what
keeps the coverage gate honest as that code arrives.
