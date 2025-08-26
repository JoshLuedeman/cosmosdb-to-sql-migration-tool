using System;
using System.Collections.Generic;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.UnitTests.Infrastructure
{
    /// <summary>
    /// Factory for creating test data objects
    /// </summary>
    public static class TestDataFactory
    {
        /// <summary>
        /// Creates a sample CosmosDbAnalysis
        /// </summary>
        public static CosmosDbAnalysis CreateSampleCosmosAnalysis()
        {
            return new CosmosDbAnalysis
            {
                DatabaseMetrics = CreateSampleDatabaseMetrics(),
                Containers = new List<ContainerAnalysis>
                {
                    CreateSampleContainerAnalysis("users"),
                    CreateSampleContainerAnalysis("orders"),
                    CreateSampleContainerAnalysis("products")
                },
                PerformanceMetrics = CreateSamplePerformanceMetrics(),
                MonitoringLimitations = new List<string>
                {
                    "Limited monitoring history available",
                    "Cross-partition query patterns not fully captured"
                }
            };
        }

        /// <summary>
        /// Creates a sample DatabaseMetrics
        /// </summary>
        public static DatabaseMetrics CreateSampleDatabaseMetrics()
        {
            return new DatabaseMetrics
            {
                TotalDocuments = 1000000L,
                TotalSizeBytes = 5000000000L, // 5GB
                ContainerCount = 3,
                ConsistencyLevel = "Session",
                IsServerlessAccount = false,
                ProvisionedThroughput = 1000
            };
        }

        /// <summary>
        /// Creates a sample ContainerAnalysis
        /// </summary>
        public static ContainerAnalysis CreateSampleContainerAnalysis(string containerName = "sample-container")
        {
            return new ContainerAnalysis
            {
                ContainerName = containerName,
                DocumentCount = 100000,
                SizeBytes = 1000000000L, // 1GB
                PartitionKey = "/id",
                ProvisionedRUs = 400,
                DetectedSchemas = new List<DocumentSchema>
                {
                    CreateSampleDocumentSchema(containerName)
                },
                IndexingPolicy = CreateSampleContainerIndexingPolicy(),
                Performance = CreateSampleContainerPerformanceMetrics()
            };
        }

        /// <summary>
        /// Creates a sample DocumentSchema
        /// </summary>
        public static DocumentSchema CreateSampleDocumentSchema(string schemaName)
        {
            return new DocumentSchema
            {
                SchemaName = $"{schemaName}Schema",
                Fields = new Dictionary<string, FieldInfo>
                {
                    ["id"] = new FieldInfo { FieldName = "id", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "VARCHAR(50)", IsRequired = true },
                    ["name"] = new FieldInfo { FieldName = "name", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(200)", IsRequired = true },
                    ["email"] = new FieldInfo { FieldName = "email", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(300)", IsRequired = false }
                },
                SampleCount = 50000,
                Prevalence = 0.75
            };
        }

        /// <summary>
        /// Creates a sample ContainerIndexingPolicy
        /// </summary>
        public static ContainerIndexingPolicy CreateSampleContainerIndexingPolicy()
        {
            return new ContainerIndexingPolicy
            {
                IncludedPaths = new List<string> { "/*" },
                ExcludedPaths = new List<string> { "/metadata/*" },
                CompositeIndexes = new List<CompositeIndex>
                {
                    new CompositeIndex
                    {
                        Paths = new List<IndexPath>
                        {
                            new IndexPath { Path = "/name", Order = "ascending" },
                            new IndexPath { Path = "/email", Order = "ascending" }
                        }
                    }
                },
                SpatialIndexes = new List<SpatialIndex>()
            };
        }

        /// <summary>
        /// Creates sample ContainerPerformanceMetrics
        /// </summary>
        public static ContainerPerformanceMetrics CreateSampleContainerPerformanceMetrics()
        {
            return new ContainerPerformanceMetrics
            {
                AverageRUConsumption = 15.5,
                PeakRUConsumption = 100.0,
                AverageLatencyMs = 5.2,
                TotalRequestCount = 1000000,
                ThrottlingRate = 0.02,
                TopQueries = new Dictionary<string, QueryMetrics>
                {
                    ["point-read"] = new QueryMetrics
                    {
                        QueryPattern = "SELECT * FROM c WHERE c.id = @id",
                        ExecutionCount = 50000,
                        AverageRUs = 1.0,
                        AverageLatencyMs = 2.5
                    }
                },
                HotPartitions = new List<HotPartition>
                {
                    new HotPartition
                    {
                        PartitionKeyValue = "partition-1",
                        RUConsumptionPercentage = 25.5,
                        RequestCount = 250000
                    }
                }
            };
        }

        /// <summary>
        /// Creates a sample PerformanceMetrics
        /// </summary>
        public static PerformanceMetrics CreateSamplePerformanceMetrics()
        {
            return new PerformanceMetrics
            {
                AnalysisPeriod = new TimeRange
                {
                    StartTime = DateTime.UtcNow.AddDays(-7),
                    EndTime = DateTime.UtcNow
                },
                TotalRUConsumption = 1000000.0,
                AverageRUsPerSecond = 250.5,
                PeakRUsPerSecond = 800.0,
                AverageRequestLatencyMs = 5.5,
                TotalRequests = 5000000,
                ErrorRate = 0.001,
                ThrottlingRate = 0.02,
                Trends = new List<PerformanceTrend>
                {
                    new PerformanceTrend
                    {
                        MetricName = "RU Consumption",
                        Trend = "Stable",
                        ChangePercentage = 2.5
                    }
                }
            };
        }

        /// <summary>
        /// Creates a sample SqlMigrationAssessment
        /// </summary>
        public static SqlMigrationAssessment CreateSampleSqlAssessment()
        {
            return new SqlMigrationAssessment
            {
                RecommendedPlatform = "Azure SQL Database",
                RecommendedTier = "General Purpose",
                DatabaseMappings = new List<DatabaseMapping>
                {
                    CreateSampleDatabaseMapping()
                },
                IndexRecommendations = new List<IndexRecommendation>
                {
                    CreateSampleIndexRecommendation()
                },
                Complexity = CreateSampleMigrationComplexity(),
                TransformationRules = new List<TransformationRule>
                {
                    CreateSampleTransformationRule()
                }
            };
        }

        /// <summary>
        /// Creates a sample DatabaseMapping
        /// </summary>
        public static DatabaseMapping CreateSampleDatabaseMapping()
        {
            return new DatabaseMapping
            {
                SourceDatabase = "cosmos-db",
                TargetDatabase = "sql-db",
                ContainerMappings = new List<ContainerMapping>
                {
                    CreateSampleContainerMapping()
                }
            };
        }

        /// <summary>
        /// Creates a sample ContainerMapping
        /// </summary>
        public static ContainerMapping CreateSampleContainerMapping()
        {
            return new ContainerMapping
            {
                SourceContainer = "users",
                TargetSchema = "dbo",
                TargetTable = "Users",
                FieldMappings = new List<FieldMapping>
                {
                    CreateSampleFieldMapping()
                },
                RequiredTransformations = new List<string> { "Flatten nested objects" }
            };
        }

        /// <summary>
        /// Creates a sample FieldMapping
        /// </summary>
        public static FieldMapping CreateSampleFieldMapping()
        {
            return new FieldMapping
            {
                SourceField = "id",
                SourceType = "string",
                TargetColumn = "Id",
                TargetType = "VARCHAR(50)",
                RequiresTransformation = false,
                TransformationLogic = "",
                IsPartitionKey = true,
                IsNullable = false
            };
        }

        /// <summary>
        /// Creates a sample IndexRecommendation
        /// </summary>
        public static IndexRecommendation CreateSampleIndexRecommendation()
        {
            return new IndexRecommendation
            {
                TableName = "Users",
                IndexName = "IX_Users_Email",
                IndexType = "NonClustered",
                Columns = new List<string> { "Email" },
                IncludedColumns = new List<string> { "Name" },
                Justification = "Improve query performance on email lookups",
                Priority = 1,
                EstimatedImpactRUs = 5
            };
        }

        /// <summary>
        /// Creates a sample MigrationComplexity
        /// </summary>
        public static MigrationComplexity CreateSampleMigrationComplexity()
        {
            return new MigrationComplexity
            {
                OverallComplexity = "Medium",
                ComplexityFactors = new List<ComplexityFactor>
                {
                    new ComplexityFactor
                    {
                        Factor = "Nested Objects",
                        Impact = "Medium",
                        Description = "Documents contain nested objects requiring flattening"
                    }
                },
                EstimatedMigrationDays = 15,
                Risks = new List<string> { "Data transformation complexity" },
                Assumptions = new List<string> { "No breaking schema changes during migration" }
            };
        }

        /// <summary>
        /// Creates a sample TransformationRule
        /// </summary>
        public static TransformationRule CreateSampleTransformationRule()
        {
            return new TransformationRule
            {
                RuleName = "Flatten User Address",
                SourcePattern = "user.address.{field}",
                TargetPattern = "user_{field}",
                TransformationType = "Flatten",
                Logic = "Extract nested address fields to top level",
                AffectedTables = new List<string> { "Users" }
            };
        }

        /// <summary>
        /// Creates a sample DataFactoryEstimate
        /// </summary>
        public static DataFactoryEstimate CreateSampleMigrationEstimate()
        {
            return new DataFactoryEstimate
            {
                EstimatedDuration = TimeSpan.FromHours(4),
                TotalDataSizeGB = 50,
                RecommendedDIUs = 8,
                RecommendedParallelCopies = 4,
                EstimatedCostUSD = 15.50m,
                PipelineEstimates = new List<PipelineEstimate>
                {
                    CreateSamplePipelineEstimate()
                },
                Prerequisites = new List<string> { "Setup Azure Data Factory", "Configure SQL Database" },
                Recommendations = new List<string> { "Use incremental copy for large datasets" }
            };
        }

        /// <summary>
        /// Creates a sample PipelineEstimate
        /// </summary>
        public static PipelineEstimate CreateSamplePipelineEstimate()
        {
            return new PipelineEstimate
            {
                SourceContainer = "users",
                TargetTable = "Users",
                DataSizeGB = 10,
                EstimatedDuration = TimeSpan.FromMinutes(60),
                RequiresTransformation = true,
                TransformationComplexity = "Medium"
            };
        }

        /// <summary>
        /// Creates a sample MigrationEstimate for compatibility
        /// </summary>
        public static MigrationEstimate CreateSampleMigrationEstimate()
        {
            return new MigrationEstimate
            {
                EstimatedDurationMinutes = 240,
                EstimatedCost = 15.50m,
                TotalDataSizeGB = 50,
                RecommendedDIUs = 8,
                RecommendedParallelCopies = 4,
                Considerations = new List<string>
                {
                    "Consider incremental copy for large datasets",
                    "Monitor performance during migration",
                    "Use parallel processing for better throughput"
                }
            };
        }

        /// <summary>
        /// Creates a sample ColumnDefinition for compatibility
        /// </summary>
        public static ColumnDefinition CreateSampleColumnDefinition()
        {
            return new ColumnDefinition
            {
                ColumnName = "Id",
                DataType = "VARCHAR(50)",
                IsNullable = false,
                IsPrimaryKey = true,
                DefaultValue = null,
                MaxLength = 50
            };
        }

        /// <summary>
        /// Creates a sample TableRecommendation for compatibility
        /// </summary>
        public static TableRecommendation CreateSampleTableRecommendation()
        {
            return new TableRecommendation
            {
                TableName = "Users",
                Schema = "dbo",
                Columns = new List<ColumnDefinition>
                {
                    CreateSampleColumnDefinition()
                },
                EstimatedRows = 100000,
                EstimatedSizeGB = 1.5m
            };
        }
    }

    // Helper classes for compatibility with test code
    public class MigrationEstimate
    {
        public int EstimatedDurationMinutes { get; set; }
        public decimal EstimatedCost { get; set; }
        public long TotalDataSizeGB { get; set; }
        public int RecommendedDIUs { get; set; }
        public int RecommendedParallelCopies { get; set; }
        public List<string> Considerations { get; set; } = new();
    }

    public class ColumnDefinition
    {
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public string DefaultValue { get; set; } = string.Empty;
        public int MaxLength { get; set; }
    }

    public class TableRecommendation
    {
        public string TableName { get; set; } = string.Empty;
        public string Schema { get; set; } = "dbo";
        public List<ColumnDefinition> Columns { get; set; } = new();
        public long EstimatedRows { get; set; }
        public decimal EstimatedSizeGB { get; set; }
    }
}
