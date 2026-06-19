# Extension points

This guide describes the supported ways to **extend, customize, and integrate** the
Cosmos DB to Azure SQL migration assessment engine from your own .NET code. Everything
here uses the public API surface documented in the
[API Reference](xref:CosmosToSqlAssessment).

> [!NOTE]
> The command-line front end (`Cli`), the dependency-injection composition root
> (`DependencyInjection`), and the run orchestrator (`Orchestration`) are **internal**
> implementation details. Public extensibility is intended through direct use of the
> public services, the public options types, and
> <xref:CosmosToSqlAssessment.Services.DataFactory.IDataFactoryPipelineGenerator> in
> **caller-owned composition code** — the bundled CLI is not itself a plug-in host.

## 1. Compose the public services in your own host

Every high-level service has a public constructor that takes only
[`IConfiguration`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration.iconfiguration)
and an [`ILogger<T>`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.logging.ilogger-1),
so you can register them in your own dependency-injection container (or construct them
directly) and call them from any host — a worker service, an ASP.NET Core endpoint, or a
custom CLI:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging(b => b.AddConsole());

// Register only the engine pieces you need.
services.AddSingleton<CosmosDbAnalysisService>();
services.AddSingleton<SqlMigrationAssessmentService>();
services.AddSingleton<ReportGenerationService>();

using var provider = services.BuildServiceProvider();
var cosmos = provider.GetRequiredService<CosmosDbAnalysisService>();
```

This is the primary integration seam: the services are independent and can be used
piecemeal — for example, running only the analysis stage and feeding the resulting
<xref:CosmosToSqlAssessment.Models.CosmosDbAnalysis> into your own tooling.

> [!NOTE]
> Composition lets you choose *which* services to use and how to host them, but some
> dependencies are constructed internally rather than injected: for instance
> <xref:CosmosToSqlAssessment.Services.CosmosDbAnalysisService> builds its own
> `CosmosClient` from configuration and `DefaultAzureCredential`, so DI registration does
> not let you substitute a custom Cosmos client, credential, or retry policy.

## 2. Replace Azure Data Factory generation: `IDataFactoryPipelineGenerator`

Data Factory artifact generation is abstracted behind the
<xref:CosmosToSqlAssessment.Services.DataFactory.IDataFactoryPipelineGenerator>
contract. Provide your own implementation to change the generated topology entirely
(for example, to emit Synapse pipelines or a bespoke ARM layout) while keeping the rest
of the pipeline unchanged:

```csharp
public sealed class MyPipelineGenerator : IDataFactoryPipelineGenerator
{
    public Task<DataFactoryGenerationResult> GenerateAsync(
        AssessmentResult assessment,
        string outputDirectory,
        DataFactoryGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // ...emit your own artifacts under outputDirectory...
        return Task.FromResult(new DataFactoryGenerationResult());
    }
}

// Register your implementation instead of the built-in service:
services.AddSingleton<IDataFactoryPipelineGenerator, MyPipelineGenerator>();
```

Consumers should depend on the interface (`IDataFactoryPipelineGenerator`) rather than
the concrete <xref:CosmosToSqlAssessment.Services.DataFactory.DataFactoryPipelineGenerationService>
so the implementation can be swapped without code changes. This applies in your own host
or integration layer; the bundled CLI wires the built-in generator through its internal
composition root and does not discover replacements via DI.

## 3. Tune the built-in Data Factory generator

If the default generator's topology is fine but you want to adjust individual artifacts,
<xref:CosmosToSqlAssessment.Services.DataFactory.DataFactoryPipelineGenerationService>
accepts an optional, pre-configured instance of each of its sub-builders through its
constructor (every parameter defaults to `null`, which selects the standard builder):

| Builder | Responsibility |
|---------|----------------|
| <xref:CosmosToSqlAssessment.Services.DataFactory.LinkedServiceBuilder> | Cosmos / Azure SQL / Key Vault linked services |
| <xref:CosmosToSqlAssessment.Services.DataFactory.DatasetBuilder> | Source Cosmos and sink Azure SQL datasets |
| <xref:CosmosToSqlAssessment.Services.DataFactory.CopyActivityBuilder> | Per-mapping Copy activities |
| <xref:CosmosToSqlAssessment.Services.DataFactory.FailureNotificationBuilder> | Web + Fail notification pairs |
| <xref:CosmosToSqlAssessment.Services.DataFactory.ValidationActivityBuilder> | Row-count validation triplets |
| <xref:CosmosToSqlAssessment.Services.DataFactory.ArmTemplateBuilder> | Deployable ARM template |
| <xref:CosmosToSqlAssessment.Services.DataFactory.IncrementalCopyActivityBuilder> | Incremental watermark activity group |
| <xref:CosmosToSqlAssessment.Services.DataFactory.WatermarkSchemaBuilder> | Watermark DDL / SQL scripts |
| <xref:CosmosToSqlAssessment.Services.DataFactory.DiagnosticSettingsTemplateBuilder> | Diagnostic-settings ARM template |

```csharp
var generator = new DataFactoryPipelineGenerationService(
    logger,
    copyActivityBuilder: new CopyActivityBuilder()); // supply a pre-configured builder
```

The built-in generator exposes **construction-time customization seams** for its sealed
helper builders: supply a pre-configured builder instance to adjust the corresponding
artifacts. The builders are `sealed`, so they are not polymorphic extension points — for
behavioral changes beyond what those builders and the options below support, implement
`IDataFactoryPipelineGenerator` (section 2).

## 4. Adjust behavior with options objects

Several entry points take an *options* object that toggles behavior without requiring a
custom implementation:

- <xref:CosmosToSqlAssessment.Services.DataFactory.DataFactoryGenerationOptions> —
  settings for the built-in ADF generator, in areas such as full vs. incremental load,
  per-pipeline activity chunking, and validation/monitoring behavior.
- <xref:CosmosToSqlAssessment.Services.DataFactory.IncrementalCopyOptions> — settings for
  incremental copy, such as watermark handling and safety lag, used when incremental load
  is enabled.
- <xref:CosmosToSqlAssessment.Models.SqlProjectOptions> — SQL Database Project generation
  settings such as the project name and output path.

```csharp
var result = await generator.GenerateAsync(
    assessment,
    outputDirectory: "out",
    options: new DataFactoryGenerationOptions
    {
        // enable incremental load, set chunking, etc.
    });
```

## 5. Drive behavior through configuration

Because the services read their settings from `IConfiguration`, much of the engine can be
tuned purely through `appsettings.json` / environment variables — no code changes:

| Key | Used by | Purpose |
|-----|---------|---------|
| `CosmosDb:AccountEndpoint` | analysis / data-quality services | Cosmos account to assess |
| `CosmosDb:DatabaseName` | <xref:CosmosToSqlAssessment.Services.CosmosDbAnalysisService> | Default database when none is passed |
| `AzureMonitor:WorkspaceId` | <xref:CosmosToSqlAssessment.Services.CosmosDbAnalysisService> | Enables Log Analytics performance metrics |
| `DataFactory:NetworkBandwidthMbps`, `DataFactory:SourceRegion`, `DataFactory:TargetRegion` | <xref:CosmosToSqlAssessment.Services.DataFactoryEstimateService> | Tunes migration time/cost estimates |
| `Reporting:OutputDirectory` | <xref:CosmosToSqlAssessment.Reporting.ReportGenerationService> | Default report output folder |

## 6. Consume the streaming analysis API

For large accounts, <xref:CosmosToSqlAssessment.Services.CosmosDbAnalysisService> exposes
an async-streaming overload so you can build your own pipeline that consumes each
container's analysis as it is produced — emitting progress, persisting incrementally, or
fanning out — using `await foreach`:

```csharp
await foreach (ContainerAnalysis container in
    cosmos.AnalyzeContainersStreamingAsync("OrdersDb", cancellationToken))
{
    // your custom per-container handling
}
```

## 7. Control authentication

The Azure-facing services authenticate with
[`DefaultAzureCredential`](https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential),
so authentication follows the Azure Identity default credential chain for the runtime
environment — managed identity, the Azure CLI, environment variables, or interactive
sign-in, depending on what is available. Environment-based credentials can be supplied
with the standard `AZURE_*` variables in your host rather than in code.
