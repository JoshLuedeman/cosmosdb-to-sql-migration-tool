namespace CosmosToSqlAssessment.Tests.Infrastructure;

/// <summary>
/// Factory for creating test data objects aligned with current API
/// </summary>
public static class TestDataFactory
{
    public static AssessmentResult CreateSampleAssessmentResult()
    {
        return new AssessmentResult
        {
            AssessmentId = Guid.NewGuid().ToString(),
            AssessmentDate = DateTime.UtcNow,
            CosmosAccountName = "test-cosmos-account",
            DatabaseName = "TestDatabase",
            CosmosAnalysis = CreateSampleCosmosAnalysis(),
            SqlAssessment = CreateSampleSqlAssessment(),
            DataFactoryEstimate = CreateSampleDataFactoryEstimate(),
            Recommendations = new List<RecommendationItem>
            {
                new() { Category = "Performance", Title = "Optimize Indexing", Priority = "High" }
            }
        };
    }

    public static CosmosDbAnalysis CreateSampleCosmosAnalysis()
    {
        return new CosmosDbAnalysis
        {
            DatabaseMetrics = new DatabaseMetrics
            {
                TotalDocuments = 1000000,
                TotalSizeBytes = 10_000_000_000,
                ContainerCount = 3,
                ConsistencyLevel = "Session",
                IsServerlessAccount = false,
                ProvisionedThroughput = 400
            },
            Containers = new List<ContainerAnalysis>
            {
                new()
                {
                    ContainerName = "users",
                    DocumentCount = 500000,
                    SizeBytes = 5_000_000_000,
                    PartitionKey = "/userId",
                    ProvisionedRUs = 400,
                    DetectedSchemas = new List<DocumentSchema>
                    {
                        new()
                        {
                            SchemaName = "UserProfile",
                            Fields = new Dictionary<string, FieldInfo>
                            {
                                ["userId"] = new() { FieldName = "userId", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(100)" },
                                ["email"] = new() { FieldName = "email", DetectedTypes = new List<string> { "string" }, RecommendedSqlType = "NVARCHAR(255)" }
                            },
                            SampleCount = 100,
                            Prevalence = 1.0
                        }
                    },
                    IndexingPolicy = new ContainerIndexingPolicy(),
                    Performance = new ContainerPerformanceMetrics
                    {
                        AverageRUConsumption = 10.5,
                        PeakRUConsumption = 50.0,
                        AverageLatencyMs = 5.2,
                        TotalRequestCount = 1000000,
                        ThrottlingRate = 0.01
                    }
                }
            },
            PerformanceMetrics = new PerformanceMetrics
            {
                AnalysisPeriod = new TimeRange
                {
                    StartTime = DateTime.UtcNow.AddDays(-180),
                    EndTime = DateTime.UtcNow
                },
                TotalRUConsumption = 1000000,
                AverageRUsPerSecond = 15.5,
                PeakRUsPerSecond = 100.0,
                AverageRequestLatencyMs = 10.5,
                TotalRequests = 5000000,
                ErrorRate = 0.001,
                ThrottlingRate = 0.01
            }
        };
    }

    public static SqlMigrationAssessment CreateSampleSqlAssessment()
    {
        return new SqlMigrationAssessment
        {
            RecommendedPlatform = "Azure SQL Database",
            RecommendedTier = "General Purpose",
            DatabaseMappings = new List<DatabaseMapping>
            {
                new()
                {
                    SourceDatabase = "TestDatabase",
                    TargetDatabase = "TestDatabase_SQL",
                    ContainerMappings = new List<ContainerMapping>
                    {
                        new()
                        {
                            SourceContainer = "users",
                            TargetSchema = "dbo",
                            TargetTable = "Users",
                            FieldMappings = new List<FieldMapping>
                            {
                                new()
                                {
                                    SourceField = "userId",
                                    SourceType = "string",
                                    TargetColumn = "UserId",
                                    TargetType = "NVARCHAR(100)",
                                    RequiresTransformation = false,
                                    IsPartitionKey = true,
                                    IsNullable = false
                                }
                            }
                        }
                    }
                }
            },
            IndexRecommendations = new List<IndexRecommendation>
            {
                new()
                {
                    TableName = "Users",
                    IndexName = "IX_Users_Email",
                    IndexType = "NonClustered",
                    Columns = new List<string> { "Email" },
                    Justification = "Improve query performance on email lookups",
                    Priority = 1,
                    EstimatedImpactRUs = 50
                }
            },
            Complexity = new MigrationComplexity
            {
                OverallComplexity = "Medium",
                EstimatedMigrationDays = 5,
                ComplexityFactors = new List<ComplexityFactor>
                {
                    new() { Factor = "Schema Complexity", Impact = "Medium", Description = "Moderate nesting and arrays" }
                },
                Risks = new List<string> { "Data type compatibility" },
                Assumptions = new List<string> { "No custom collations required" }
            },
            TransformationRules = new List<TransformationRule>
            {
                new()
                {
                    RuleName = "Flatten User Profile",
                    TransformationType = "Flatten",
                    SourcePattern = "/profile/*",
                    TargetPattern = "profile_*",
                    Logic = "Flatten nested profile object to top-level columns",
                    AffectedTables = new List<string> { "Users" }
                }
            }
        };
    }

    public static DataFactoryEstimate CreateSampleDataFactoryEstimate()
    {
        return new DataFactoryEstimate
        {
            EstimatedDuration = TimeSpan.FromHours(2),
            TotalDataSizeGB = 10,
            RecommendedDIUs = 4,
            RecommendedParallelCopies = 2,
            EstimatedCostUSD = 15.50m,
            PipelineEstimates = new List<PipelineEstimate>
            {
                new()
                {
                    SourceContainer = "users",
                    TargetTable = "Users",
                    DataSizeGB = 5,
                    EstimatedDuration = TimeSpan.FromHours(1),
                    RequiresTransformation = true,
                    TransformationComplexity = "Medium"
                }
            },
            Prerequisites = new List<string>
            {
                "Azure Data Factory instance created",
                "Source Cosmos DB access configured"
            },
            Recommendations = new List<string>
            {
                "Consider incremental copy for large datasets",
                "Monitor throttling during migration"
            }
        };
    }
}
