using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Benchmarks.Fixtures;

/// <summary>
/// Deterministic synthetic fixtures used by the SQL assessment benchmarks. Generators are
/// seeded loops (no <see cref="Random"/>) so benchmark results are reproducible across runs.
/// Sizing is calibrated to exercise the full <c>SqlMigrationAssessmentService</c> pipeline —
/// platform recommendation, container mappings, schema deduplication, index / FK / unique
/// constraint generation, complexity assessment, and transformation rules — without ballooning
/// benchmark run time.
///
/// Important: <c>DeduplicateSchemas</c> operates on <c>ChildTableMapping</c> instances (not
/// top-level container schemas), so Medium and Large fixtures deliberately share a handful of
/// common child-table shapes across containers (<c>addresses</c>, <c>contacts</c>,
/// <c>lineItems</c>, <c>tags</c>) — that mix of shared + distinct child tables is what real
/// workloads look like and is what makes the dedupe branch do meaningful work.
/// </summary>
public static class SqlAssessmentFixtures
{
    public enum AnalysisSize
    {
        Small,
        Medium,
        Large
    }

    public static CosmosDbAnalysis BuildCosmosAnalysis(AnalysisSize size)
    {
        var (containerCount, schemasPerContainer, fieldsPerSchema, sharedChildRatio) = size switch
        {
            AnalysisSize.Small => (1, 2, 5, 0.0),
            AnalysisSize.Medium => (5, 3, 15, 0.5),
            AnalysisSize.Large => (20, 5, 30, 0.4),
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };

        var analysis = new CosmosDbAnalysis
        {
            DatabaseMetrics = new DatabaseMetrics
            {
                TotalDocuments = containerCount * 1_000_000L,
                TotalSizeBytes = containerCount * 10_000_000_000L,
                ContainerCount = containerCount,
                ConsistencyLevel = "Session",
                IsServerlessAccount = false,
                ProvisionedThroughput = 4000
            },
            PerformanceMetrics = BuildPerformanceMetrics(containerCount)
        };

        for (var c = 0; c < containerCount; c++)
        {
            analysis.Containers.Add(BuildContainer(c, schemasPerContainer, fieldsPerSchema, sharedChildRatio));
        }

        return analysis;
    }

    private static ContainerAnalysis BuildContainer(int containerIndex, int schemaCount, int fieldsPerSchema, double sharedChildRatio)
    {
        var containerName = $"container_{containerIndex:D3}";

        var container = new ContainerAnalysis
        {
            ContainerName = containerName,
            DocumentCount = 500_000 + containerIndex * 50_000,
            SizeBytes = 5_000_000_000L + containerIndex * 250_000_000L,
            PartitionKey = "/tenantId",
            ProvisionedRUs = 400,
            IndexingPolicy = BuildIndexingPolicy(containerIndex),
            Performance = BuildContainerPerformance(containerIndex)
        };

        for (var s = 0; s < schemaCount; s++)
        {
            container.DetectedSchemas.Add(BuildSchema(containerName, s, fieldsPerSchema, schemaCount));
        }

        // Shared child tables exercise DeduplicateSchemas. Pick from a fixed catalog so identical
        // shapes recur across containers.
        var sharedCatalog = SharedChildCatalog();
        var useShared = containerIndex < (int)Math.Round(sharedChildRatio * 10);
        if (useShared)
        {
            var sharedName = sharedCatalog[containerIndex % sharedCatalog.Length];
            container.ChildTables[sharedName] = BuildSharedChildTable(sharedName);
        }

        // Plus one container-unique child table so distinct-schema bookkeeping also runs.
        container.ChildTables[$"{containerName}_audit"] = BuildUniqueChildTable($"{containerName}_audit");

        return container;
    }

    private static string[] SharedChildCatalog() => new[] { "addresses", "contacts", "lineItems", "tags" };

    private static DocumentSchema BuildSchema(string containerName, int schemaIndex, int fieldCount, int totalSchemas)
    {
        var schema = new DocumentSchema
        {
            SchemaName = $"{containerName}_schema_{schemaIndex:D2}",
            SampleCount = 100 + schemaIndex * 10,
            Prevalence = 1.0 - (double)schemaIndex / Math.Max(totalSchemas, 1)
        };

        for (var f = 0; f < fieldCount; f++)
        {
            var fieldName = $"{containerName}_field_{schemaIndex:D2}_{f:D2}";
            schema.Fields[fieldName] = BuildField(fieldName, f);
        }

        // Include a nested field marker so non-trivial mapping paths run.
        var nestedName = $"{containerName}_profile_{schemaIndex:D2}";
        schema.Fields[nestedName] = new FieldInfo
        {
            FieldName = nestedName,
            DetectedTypes = new List<string> { "object" },
            RecommendedSqlType = "NVARCHAR(MAX)",
            IsRequired = false,
            IsNested = true,
            MaxLength = 0,
            Selectivity = 0.5
        };

        return schema;
    }

    private static FieldInfo BuildField(string fieldName, int fieldIndex)
    {
        // Rotate across primitive types so type-mapping and business-key detection both have data.
        var (types, sqlType, maxLength, selectivity) = (fieldIndex % 5) switch
        {
            0 => (new List<string> { "string" }, "NVARCHAR(255)", 255, 0.95),
            1 => (new List<string> { "integer" }, "BIGINT", 0, 0.85),
            2 => (new List<string> { "number" }, "DECIMAL(18,2)", 0, 0.75),
            3 => (new List<string> { "boolean" }, "BIT", 0, 0.10),
            _ => (new List<string> { "string" }, "NVARCHAR(100)", 100, 0.99)
        };

        return new FieldInfo
        {
            FieldName = fieldName,
            DetectedTypes = types,
            RecommendedSqlType = sqlType,
            IsRequired = fieldIndex % 3 == 0,
            IsNested = false,
            MaxLength = maxLength,
            Selectivity = selectivity
        };
    }

    private static ChildTableSchema BuildSharedChildTable(string name)
    {
        // Same field layout for the same name across containers → DeduplicateSchemas can collapse.
        var table = new ChildTableSchema
        {
            TableName = name,
            SourceFieldPath = name,
            ChildTableType = "Array",
            SampleCount = 250,
            ParentKeyField = "ParentId"
        };

        table.Fields["id"] = SharedField("id", "string", "NVARCHAR(100)", 100, true);
        table.Fields["label"] = SharedField("label", "string", "NVARCHAR(255)", 255, false);
        table.Fields["value"] = SharedField("value", "string", "NVARCHAR(500)", 500, false);
        table.Fields["sortOrder"] = SharedField("sortOrder", "integer", "INT", 0, false);

        return table;
    }

    private static ChildTableSchema BuildUniqueChildTable(string name)
    {
        var table = new ChildTableSchema
        {
            TableName = name,
            SourceFieldPath = name,
            ChildTableType = "Array",
            SampleCount = 100,
            ParentKeyField = "ParentId"
        };

        // Unique fields force each instance into its own SharedSchema group.
        table.Fields[$"{name}_id"] = SharedField($"{name}_id", "string", "NVARCHAR(100)", 100, true);
        table.Fields[$"{name}_event"] = SharedField($"{name}_event", "string", "NVARCHAR(255)", 255, false);
        table.Fields[$"{name}_at"] = SharedField($"{name}_at", "string", "NVARCHAR(50)", 50, false);

        return table;
    }

    private static FieldInfo SharedField(string name, string type, string sqlType, int maxLength, bool required)
    {
        return new FieldInfo
        {
            FieldName = name,
            DetectedTypes = new List<string> { type },
            RecommendedSqlType = sqlType,
            IsRequired = required,
            IsNested = false,
            MaxLength = maxLength,
            Selectivity = required ? 1.0 : 0.7
        };
    }

    private static ContainerIndexingPolicy BuildIndexingPolicy(int containerIndex)
    {
        var policy = new ContainerIndexingPolicy
        {
            IncludedPaths = new List<string> { "/*" },
            ExcludedPaths = new List<string> { "/_etag/?" }
        };

        // One composite index per container so GenerateIndexRecommendations has something to mine.
        policy.CompositeIndexes.Add(new CompositeIndex
        {
            Paths = new List<IndexPath>
            {
                new() { Path = "/tenantId" },
                new() { Path = $"/field_{containerIndex % 5}" }
            }
        });

        return policy;
    }

    private static ContainerPerformanceMetrics BuildContainerPerformance(int containerIndex)
    {
        var perf = new ContainerPerformanceMetrics
        {
            AverageRUConsumption = 10.0 + containerIndex,
            PeakRUConsumption = 50.0 + containerIndex * 5,
            AverageLatencyMs = 5.0 + containerIndex * 0.1,
            TotalRequestCount = 1_000_000 + containerIndex * 100_000,
            ThrottlingRate = 0.01
        };

        // Two query patterns per container so GenerateQueryBasedIndexRecommendation has work.
        perf.TopQueries[$"query_{containerIndex}_a"] = new QueryMetrics
        {
            QueryPattern = "SELECT * FROM c WHERE c.tenantId = @id AND c.status = 'active'",
            ExecutionCount = 50_000 + containerIndex * 1_000,
            AverageRUs = 5.5,
            AverageLatencyMs = 8.2
        };

        perf.TopQueries[$"query_{containerIndex}_b"] = new QueryMetrics
        {
            QueryPattern = "SELECT * FROM c WHERE c.createdAt > @since",
            ExecutionCount = 20_000 + containerIndex * 500,
            AverageRUs = 9.1,
            AverageLatencyMs = 12.5
        };

        return perf;
    }

    private static PerformanceMetrics BuildPerformanceMetrics(int containerCount)
    {
        return new PerformanceMetrics
        {
            AnalysisPeriod = new TimeRange
            {
                StartTime = DateTime.UtcNow.AddDays(-180),
                EndTime = DateTime.UtcNow
            },
            TotalRUConsumption = 1_000_000 * containerCount,
            AverageRUsPerSecond = 15.5,
            PeakRUsPerSecond = 100.0,
            AverageRequestLatencyMs = 10.5,
            TotalRequests = 5_000_000L * containerCount,
            ErrorRate = 0.001,
            ThrottlingRate = 0.01
        };
    }

    /// <summary>
    /// Bank of representative <see cref="IndexRecommendation"/> instances covering every branch
    /// in <c>SqlProjectGenerationService.GenerateIndexScript</c>: each index type (NonClustered,
    /// Unique, Clustered, ColumnStore), single- and multi-column key lists, and the
    /// IncludedColumns.Any() = false / true branches with varying include sizes.
    /// </summary>
    public static IndexRecommendation[] BuildIndexRecommendations()
    {
        return new[]
        {
            new IndexRecommendation
            {
                TableName = "Users",
                IndexName = "IX_Users_Email",
                IndexType = "NonClustered",
                Columns = new List<string> { "Email" },
                IncludedColumns = new List<string>(),
                Justification = "Email lookup",
                Priority = 1,
                EstimatedImpactRUs = 50
            },
            new IndexRecommendation
            {
                TableName = "Orders",
                IndexName = "IX_Orders_Customer_Date",
                IndexType = "NonClustered",
                Columns = new List<string> { "CustomerId", "OrderDate" },
                IncludedColumns = new List<string> { "Status" },
                Justification = "Customer order history",
                Priority = 2,
                EstimatedImpactRUs = 120
            },
            new IndexRecommendation
            {
                TableName = "Products",
                IndexName = "UX_Products_Sku",
                IndexType = "Unique",
                Columns = new List<string> { "Sku" },
                IncludedColumns = new List<string>(),
                Justification = "SKU uniqueness",
                Priority = 1,
                EstimatedImpactRUs = 0
            },
            new IndexRecommendation
            {
                TableName = "Audit",
                IndexName = "IX_Audit_TenantId",
                IndexType = "Clustered",
                Columns = new List<string> { "TenantId", "Timestamp" },
                IncludedColumns = new List<string>(),
                Justification = "Tenant scan",
                Priority = 1,
                EstimatedImpactRUs = 200
            },
            new IndexRecommendation
            {
                TableName = "Telemetry",
                IndexName = "CCI_Telemetry",
                IndexType = "Columnstore",
                Columns = new List<string> { "EventId" },
                IncludedColumns = new List<string>(),
                Justification = "Analytics scan",
                Priority = 3,
                EstimatedImpactRUs = 500
            },
            new IndexRecommendation
            {
                TableName = "Invoices",
                IndexName = "IX_Invoices_Covering",
                IndexType = "NonClustered",
                Columns = new List<string> { "CustomerId", "InvoiceDate" },
                IncludedColumns = new List<string> { "TotalAmount", "Currency", "Status", "DueDate" },
                Justification = "Covering index for invoice listing",
                Priority = 2,
                EstimatedImpactRUs = 180
            },
        };
    }

    /// <summary>
    /// Realistic db/container names exercising every branch in
    /// <c>SqlProjectGenerationService.SanitizeName</c>: whitespace, illegal chars (-, .),
    /// invalid file-system chars, leading digit / non-letter.
    /// </summary>
    public static string[] BuildSanitizeNameInputs()
    {
        return new[]
        {
            "MyDatabase",
            "Customer Profiles",
            "orders-archive",
            "2024_metrics",
            "tenant.shared.config",
            "audit/logs",
            "cosmos:billing",
            "Inventory.Warehouse-East",
            "_internal_db",
            "9-legacy-system",
        };
    }
}
