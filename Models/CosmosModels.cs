namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Represents the complete assessment result for a Cosmos DB to SQL migration
    /// </summary>
    public class AssessmentResult
    {
        public string AssessmentId { get; set; } = Guid.NewGuid().ToString();
        public DateTime AssessmentDate { get; set; } = DateTime.UtcNow;
        public string CosmosAccountName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        
        public CosmosDbAnalysis CosmosAnalysis { get; set; } = new();
        public SqlMigrationAssessment SqlAssessment { get; set; } = new();
        public DataFactoryEstimate DataFactoryEstimate { get; set; } = new();
        public DataQualityAnalysis? DataQualityAnalysis { get; set; } = null;
        public List<RecommendationItem> Recommendations { get; set; } = new();
        
        // Properties for multi-database handling
        public bool GenerateSeparateExcel { get; set; } = false;
        public List<AssessmentResult> IndividualDatabaseResults { get; set; } = new();
        
        // Output path information (not serialized to JSON)
        public string AnalysisFolderPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Comprehensive analysis of Cosmos DB performance and structure
    /// </summary>
    public class CosmosDbAnalysis
    {
        public DatabaseMetrics DatabaseMetrics { get; set; } = new();
        public List<ContainerAnalysis> Containers { get; set; } = new();
        public PerformanceMetrics PerformanceMetrics { get; set; } = new();
        public List<string> MonitoringLimitations { get; set; } = new();
    }

    /// <summary>
    /// Database-level metrics and information
    /// </summary>
    public class DatabaseMetrics
    {
        public long TotalDocuments { get; set; }
        public long TotalSizeBytes { get; set; }
        public int ContainerCount { get; set; }
        public string ConsistencyLevel { get; set; } = string.Empty;
        public bool IsServerlessAccount { get; set; }
        public int ProvisionedThroughput { get; set; }
    }

    /// <summary>
    /// Detailed analysis of a single Cosmos DB container
    /// </summary>
    public class ContainerAnalysis
    {
        public string ContainerName { get; set; } = string.Empty;
        public long DocumentCount { get; set; }
        public long SizeBytes { get; set; }
        public string PartitionKey { get; set; } = string.Empty;
        public int ProvisionedRUs { get; set; }
        
        public List<DocumentSchema> DetectedSchemas { get; set; } = new();
        public Dictionary<string, ChildTableSchema> ChildTables { get; set; } = new();
        public ContainerIndexingPolicy IndexingPolicy { get; set; } = new();
        public ContainerPerformanceMetrics Performance { get; set; } = new();
    }

    /// <summary>
    /// Detected document schema structure
    /// </summary>
    public class DocumentSchema
    {
        public string SchemaName { get; set; } = string.Empty;
        public Dictionary<string, FieldInfo> Fields { get; set; } = new();
        public long SampleCount { get; set; }
        public double Prevalence { get; set; }
    }

    /// <summary>
    /// Information about a field in the document schema
    /// </summary>
    public class FieldInfo
    {
        public string FieldName { get; set; } = string.Empty;
        public List<string> DetectedTypes { get; set; } = new();
        public string RecommendedSqlType { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
        public bool IsNested { get; set; }
        public int MaxLength { get; set; }
        public double Selectivity { get; set; }
    }

    /// <summary>
    /// Schema information for child tables (normalized from arrays and nested objects)
    /// </summary>
    public class ChildTableSchema
    {
        public string TableName { get; set; } = string.Empty;
        public string SourceFieldPath { get; set; } = string.Empty;
        public string ChildTableType { get; set; } = string.Empty; // "Array" or "NestedObject" or "ManyToMany"
        public Dictionary<string, FieldInfo> Fields { get; set; } = new();
        public long SampleCount { get; set; }
        public string ParentKeyField { get; set; } = "ParentId"; // Foreign key to parent table
        
        // Many-to-many analysis properties
        public List<IndexRecommendation> RecommendedIndexes { get; set; } = new();
        public List<string> TransformationNotes { get; set; } = new();
    }

    /// <summary>
    /// Container-level indexing policy information
    /// </summary>
    public class ContainerIndexingPolicy
    {
        public List<string> IncludedPaths { get; set; } = new();
        public List<string> ExcludedPaths { get; set; } = new();
        public List<CompositeIndex> CompositeIndexes { get; set; } = new();
        public List<SpatialIndex> SpatialIndexes { get; set; } = new();
    }

    public class CompositeIndex
    {
        public List<IndexPath> Paths { get; set; } = new();
    }

    public class IndexPath
    {
        public string Path { get; set; } = string.Empty;
        public string Order { get; set; } = string.Empty;
    }

    public class SpatialIndex
    {
        public string Path { get; set; } = string.Empty;
        public List<string> Types { get; set; } = new();
    }

    /// <summary>
    /// Performance metrics for a container over the analysis period
    /// </summary>
    public class ContainerPerformanceMetrics
    {
        public double AverageRUConsumption { get; set; }
        public double PeakRUConsumption { get; set; }
        public double AverageLatencyMs { get; set; }
        public long TotalRequestCount { get; set; }
        public double ThrottlingRate { get; set; }
        
        public Dictionary<string, QueryMetrics> TopQueries { get; set; } = new();
        public List<HotPartition> HotPartitions { get; set; } = new();
    }

    public class QueryMetrics
    {
        public string QueryPattern { get; set; } = string.Empty;
        public long ExecutionCount { get; set; }
        public double AverageRUs { get; set; }
        public double AverageLatencyMs { get; set; }
    }

    public class HotPartition
    {
        public string PartitionKeyValue { get; set; } = string.Empty;
        public double RUConsumptionPercentage { get; set; }
        public long RequestCount { get; set; }
    }

    /// <summary>
    /// Overall performance metrics for the database
    /// </summary>
    public class PerformanceMetrics
    {
        public TimeRange AnalysisPeriod { get; set; } = new();
        public double TotalRUConsumption { get; set; }
        public double AverageRUsPerSecond { get; set; }
        public double PeakRUsPerSecond { get; set; }
        public double AverageRequestLatencyMs { get; set; }
        public long TotalRequests { get; set; }
        public double ErrorRate { get; set; }
        public double ThrottlingRate { get; set; }
        
        public List<PerformanceTrend> Trends { get; set; } = new();
    }

    public class TimeRange
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class PerformanceTrend
    {
        public string MetricName { get; set; } = string.Empty;
        public string Trend { get; set; } = string.Empty; // "Increasing", "Decreasing", "Stable"
        public double ChangePercentage { get; set; }
    }

    /// <summary>
    /// Analysis result for array storage strategy decisions
    /// </summary>
    public class ArrayAnalysis
    {
        public string ArrayName { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public bool ShouldCreateTable { get; set; }
        public string RecommendedStorage { get; set; } = string.Empty; // "JSON", "DelimitedString", "RelationalTable"
        public string RecommendedSqlType { get; set; } = string.Empty;
        public string TransformationLogic { get; set; } = string.Empty;
    }
}
