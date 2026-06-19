# CosmosDB to SQL Migration Tool — API Reference

Welcome to the developer API reference for the **Cosmos DB to Azure SQL Migration Assessment Tool** — a .NET 8 console application that analyzes an Azure Cosmos DB account and produces migration assessments, indexing recommendations, Azure Data Factory pipeline artifacts, and Excel/Word reports for Azure SQL targets.

This site documents the tool's **public API surface** so you can extend, embed, or integrate the assessment engine in your own automation. For installation and end-user usage, see the [project README](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/blob/main/README.md) and the [docs/](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/tree/main/docs) folder.

> [!TIP]
> Browse the full type listing under **[API Reference](xref:CosmosToSqlAssessment)** in the top navigation.

## Public namespaces

| Namespace | What lives here |
|-----------|-----------------|
| `CosmosToSqlAssessment.Services` | The analysis engine — the primary integration entry points: `CosmosDbAnalysisService`, `SqlMigrationAssessmentService`, `DataQualityAnalysisService`, `DataFactoryEstimateService`, `SqlProjectGenerationService`, `SqlProjectIntegrationService`, and `ValidationScriptGeneratorService`. |
| `CosmosToSqlAssessment.Services.DataFactory` | Azure Data Factory pipeline / ARM-template generation: `DataFactoryPipelineGenerationService`, the `IDataFactoryPipelineGenerator` contract, and the builder/escaper helpers that assemble the generated artifacts. |
| `CosmosToSqlAssessment.Reporting` | `ReportGenerationService` — Excel and Word report rendering. |
| `CosmosToSqlAssessment.SqlProject` | SQL database project (`.sqlproj`) generation. |
| `CosmosToSqlAssessment.Models` | Data-model types passed across the pipeline — Cosmos analysis results, SQL assessment output, data-quality findings, and Data Factory artifact models (`CosmosToSqlAssessment.Models.DataFactory`). |

> [!NOTE]
> The command-line front end (`Cli`), the dependency-injection composition root (`DependencyInjection`), and the run orchestrator (`Orchestration`) are **internal** implementation details — exposed to the test project via `InternalsVisibleTo` — and are intentionally not part of the supported public API. Integrators compose the public services above directly.

## Integration entry points

The public services are designed to be used individually. Each is registered in the application's dependency-injection container, but they can equally be constructed directly and called from your own host: `CosmosDbAnalysisService` analyzes an account/database, `SqlMigrationAssessmentService` turns that analysis into Azure SQL recommendations, `DataFactoryPipelineGenerationService` emits the migration pipeline artifacts, and `ReportGenerationService` renders the result to Excel/Word.

Services that walk potentially large Cosmos datasets expose **async-streaming** signatures (`IAsyncEnumerable<T>`), so results are produced lazily and the working set stays bounded regardless of collection size. Each streaming method accepts a `CancellationToken`.

See the [API Reference](xref:CosmosToSqlAssessment) for full type, member, parameter, and return documentation.

## Guides

- [Extension points](articles/extension-points.md) — how to extend, customize, and integrate the assessment engine (custom Data Factory generation, options, configuration, streaming, and authentication seams).
- [Architecture overview](articles/architecture.md) — the layered, service-oriented architecture and the multi-agent orchestration layer, with diagrams. (Rendered from [`docs/architecture.md`](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/blob/main/docs/architecture.md).)
