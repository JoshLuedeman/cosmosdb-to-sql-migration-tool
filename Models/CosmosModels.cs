namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Represents the complete assessment result for a Cosmos DB to SQL migration
    /// </summary>
    public class AssessmentResult
    {
        /// <summary>Unique identifier for this assessment run, generated automatically as a GUID string.</summary>
        public string AssessmentId { get; set; } = Guid.NewGuid().ToString();
        /// <summary>UTC timestamp at which this assessment was performed.</summary>
        public DateTime AssessmentDate { get; set; } = DateTime.UtcNow;
        /// <summary>Name of the Azure Cosmos DB account that was assessed.</summary>
        public string CosmosAccountName { get; set; } = string.Empty;
        /// <summary>Name of the Cosmos DB database that was assessed.</summary>
        public string DatabaseName { get; set; } = string.Empty;
        
        /// <summary>Full structural and performance analysis of the Cosmos DB database.</summary>
        public CosmosDbAnalysis CosmosAnalysis { get; set; } = new();
        /// <summary>Assessment of how the Cosmos DB data maps to Azure SQL, including schema and index recommendations.</summary>
        public SqlMigrationAssessment SqlAssessment { get; set; } = new();
        /// <summary>Estimated effort and configuration for an Azure Data Factory pipeline to execute the migration.</summary>
        public DataFactoryEstimate DataFactoryEstimate { get; set; } = new();
        /// <summary>Optional data-quality analysis results; null when the analysis was skipped or unavailable.</summary>
        public DataQualityAnalysis? DataQualityAnalysis { get; set; } = null;
        /// <summary>Optional incremental (change-feed) migration analysis; null when the analysis was skipped or unavailable.</summary>
        public Migration.IncrementalMigrationAnalysis? IncrementalMigration { get; set; } = null;
        /// <summary>Ordered list of actionable recommendations produced by the assessment engine.</summary>
        public List<RecommendationItem> Recommendations { get; set; } = new();

        /// <summary>
        /// The result of refining (or confirming) the recommended Azure SQL platform and tier
        /// against prior, anonymized migration outcomes from the opt-in feedback loop. Is
        /// <see langword="null"/> when refinement was not run, and carries an attributable
        /// "based on N prior similar migrations" rationale for inclusion in generated reports
        /// when it is present.
        /// </summary>
        public RecommendationRefinement? RecommendationRefinement { get; set; } = null;
        
        // Properties for multi-database handling
        /// <summary>When true, individual Excel reports are generated per database rather than a single combined report.</summary>
        public bool GenerateSeparateExcel { get; set; } = false;
        /// <summary>Per-database assessment results when the Cosmos DB account contains multiple databases assessed together.</summary>
        public List<AssessmentResult> IndividualDatabaseResults { get; set; } = new();
        
        // Output path information (not serialized to JSON)
        /// <summary>File-system path of the folder where analysis output files are written; not included in JSON serialization.</summary>
        public string AnalysisFolderPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Comprehensive analysis of Cosmos DB performance and structure
    /// </summary>
    public class CosmosDbAnalysis
    {
        /// <summary>Aggregate metrics for the database as a whole, such as total document count, size, and provisioned throughput.</summary>
        public DatabaseMetrics DatabaseMetrics { get; set; } = new();
        /// <summary>Per-container structural and performance analyses for every container in the database.</summary>
        public List<ContainerAnalysis> Containers { get; set; } = new();
        /// <summary>Database-wide performance metrics aggregated over the 6-month analysis window.</summary>
        public PerformanceMetrics PerformanceMetrics { get; set; } = new();
        /// <summary>Human-readable descriptions of Azure Monitor or SDK limitations that reduced the completeness of this analysis.</summary>
        public List<string> MonitoringLimitations { get; set; } = new();
    }

    /// <summary>
    /// Database-level metrics and information
    /// </summary>
    public class DatabaseMetrics
    {
        /// <summary>Total number of documents stored across all containers in the database.</summary>
        public long TotalDocuments { get; set; }
        /// <summary>Total on-disk storage consumed by all containers, in bytes.</summary>
        public long TotalSizeBytes { get; set; }
        /// <summary>Number of containers (collections) present in the database.</summary>
        public int ContainerCount { get; set; }
        /// <summary>Cosmos DB consistency level configured for the account (e.g., "Strong", "BoundedStaleness", "Session", "ConsistentPrefix", "Eventual").</summary>
        public string ConsistencyLevel { get; set; } = string.Empty;
        /// <summary>Indicates whether the Cosmos DB account is configured in serverless mode, which has no provisioned RU/s.</summary>
        public bool IsServerlessAccount { get; set; }
        /// <summary>Database-level provisioned throughput in request units per second (RU/s); 0 when throughput is set per-container or the account is serverless.</summary>
        public int ProvisionedThroughput { get; set; }
    }

    /// <summary>
    /// Detailed analysis of a single Cosmos DB container
    /// </summary>
    public class ContainerAnalysis
    {
        /// <summary>Name of the Cosmos DB container.</summary>
        public string ContainerName { get; set; } = string.Empty;
        /// <summary>Number of documents stored in this container.</summary>
        public long DocumentCount { get; set; }
        /// <summary>On-disk storage consumed by this container, in bytes.</summary>
        public long SizeBytes { get; set; }
        /// <summary>JSON path of the partition key for this container (e.g., "/tenantId").</summary>
        public string PartitionKey { get; set; } = string.Empty;
        /// <summary>Throughput provisioned for this container in request units per second (RU/s); 0 when throughput is shared at the database level.</summary>
        public int ProvisionedRUs { get; set; }
        
        /// <summary>Distinct document schemas detected by sampling this container's documents.</summary>
        public List<DocumentSchema> DetectedSchemas { get; set; } = new();
        /// <summary>Child tables to be created during normalization, keyed by the source field path that produces each child table.</summary>
        public Dictionary<string, ChildTableSchema> ChildTables { get; set; } = new();
        /// <summary>Indexing policy currently active on this container, including included/excluded paths and composite indexes.</summary>
        public ContainerIndexingPolicy IndexingPolicy { get; set; } = new();
        /// <summary>Container-scoped performance metrics collected during the analysis window.</summary>
        public ContainerPerformanceMetrics Performance { get; set; } = new();

        /// <summary>
        /// Raw container <c>DefaultTimeToLive</c>: <c>null</c> = TTL disabled, <c>-1</c> = enabled with no
        /// container default (item-level TTL only), <c>&gt;0</c> = default expiration in seconds. Used by the
        /// change feed availability analysis (#134) to reason about server-side TTL deletes.
        /// </summary>
        public int? DefaultTimeToLiveSeconds { get; set; }

        /// <summary>Custom TTL property path when the container expires items by a property other than <c>_ts</c>.</summary>
        public string? TimeToLivePropertyPath { get; set; }

        /// <summary>
        /// Full set of partition-key paths. Contains more than one entry for hierarchical (sub-)partition
        /// keys; falls back to the single <see cref="PartitionKey"/> path for classic keys.
        /// </summary>
        public List<string> PartitionKeyPaths { get; set; } = new();

        /// <summary>
        /// Approximate number of feed ranges (physical partitions), captured during live analysis via
        /// <c>GetFeedRangesAsync</c>. Drives change feed processor lease sizing (#140) and incremental-sync
        /// parallelism (#135). <c>0</c> when the value could not be read.
        /// </summary>
        public int FeedRangeCount { get; set; }
    }

    /// <summary>
    /// Detected document schema structure
    /// </summary>
    public class DocumentSchema
    {
        /// <summary>Human-readable label assigned to this schema variant (e.g., "OrderDocument", "UserProfile").</summary>
        public string SchemaName { get; set; } = string.Empty;
        /// <summary>Map of field names to their inferred type and SQL mapping information for this schema variant.</summary>
        public Dictionary<string, FieldInfo> Fields { get; set; } = new();
        /// <summary>Number of documents sampled that matched this schema variant.</summary>
        public long SampleCount { get; set; }
        /// <summary>Fraction of sampled documents that match this schema, expressed as a ratio between 0.0 and 1.0.</summary>
        public double Prevalence { get; set; }
    }

    /// <summary>
    /// Information about a field in the document schema
    /// </summary>
    public class FieldInfo
    {
        /// <summary>Name of the JSON field as it appears in the Cosmos DB document.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>All JSON data types observed for this field across sampled documents (e.g., "string", "number", "null").</summary>
        public List<string> DetectedTypes { get; set; } = new();
        /// <summary>SQL Server data type recommended for this field during migration (e.g., "NVARCHAR(256)", "BIGINT", "BIT").</summary>
        public string RecommendedSqlType { get; set; } = string.Empty;
        /// <summary>True when this field is present in every sampled document; false when it is optional or sometimes null.</summary>
        public bool IsRequired { get; set; }
        /// <summary>True when this field contains a JSON sub-object or array that will be normalized into a separate table.</summary>
        public bool IsNested { get; set; }
        /// <summary>Maximum observed string length across all sampled documents, in characters; used to size NVARCHAR/VARCHAR columns.</summary>
        public int MaxLength { get; set; }
        /// <summary>Estimated proportion of unique values relative to total documents, between 0.0 (all identical) and 1.0 (fully unique); informs index recommendations.</summary>
        public double Selectivity { get; set; }
    }

    /// <summary>
    /// Schema information for child tables (normalized from arrays and nested objects)
    /// </summary>
    public class ChildTableSchema
    {
        /// <summary>Name of the SQL table that will be created to hold the normalized child rows.</summary>
        public string TableName { get; set; } = string.Empty;
        /// <summary>Dot-separated JSON path in the parent document from which this child table is derived (e.g., "orderLines" or "address.tags").</summary>
        public string SourceFieldPath { get; set; } = string.Empty;
        /// <summary>Relationship pattern that drives normalization: "Array", "NestedObject", or "ManyToMany".</summary>
        public string ChildTableType { get; set; } = string.Empty; // "Array" or "NestedObject" or "ManyToMany"
        /// <summary>Fields present in this child table, keyed by field name, with type and SQL mapping details.</summary>
        public Dictionary<string, FieldInfo> Fields { get; set; } = new();
        /// <summary>Number of child-table rows observed across the sampled parent documents.</summary>
        public long SampleCount { get; set; }
        /// <summary>Name of the foreign-key column that references the parent table's primary key; defaults to "ParentId".</summary>
        public string ParentKeyField { get; set; } = "ParentId"; // Foreign key to parent table
        
        // Many-to-many analysis properties
        /// <summary>Index recommendations for the child table, especially relevant for many-to-many junction tables.</summary>
        public List<IndexRecommendation> RecommendedIndexes { get; set; } = new();
        /// <summary>Free-text notes describing the ETL transformation steps required to populate this child table from the source JSON.</summary>
        public List<string> TransformationNotes { get; set; } = new();
    }

    /// <summary>
    /// Container-level indexing policy information
    /// </summary>
    public class ContainerIndexingPolicy
    {
        /// <summary>JSON property paths explicitly included in the container's index (e.g., "/*" for all paths).</summary>
        public List<string> IncludedPaths { get; set; } = new();
        /// <summary>JSON property paths explicitly excluded from the container's index to reduce RU/s overhead.</summary>
        public List<string> ExcludedPaths { get; set; } = new();
        /// <summary>Composite index definitions that cover multi-property ORDER BY and filter combinations.</summary>
        public List<CompositeIndex> CompositeIndexes { get; set; } = new();
        /// <summary>Spatial index definitions used for geospatial queries on Point, LineString, or Polygon fields.</summary>
        public List<SpatialIndex> SpatialIndexes { get; set; } = new();
    }

    /// <summary>A single composite index definition consisting of an ordered set of property paths.</summary>
    public class CompositeIndex
    {
        /// <summary>Ordered list of property paths and sort directions that together form this composite index.</summary>
        public List<IndexPath> Paths { get; set; } = new();
    }

    /// <summary>A single path entry within a composite index, combining the JSON property path with its sort order.</summary>
    public class IndexPath
    {
        /// <summary>JSON property path covered by this index entry (e.g., "/lastName").</summary>
        public string Path { get; set; } = string.Empty;
        /// <summary>Sort direction for this path in the composite index: "ascending" or "descending".</summary>
        public string Order { get; set; } = string.Empty;
    }

    /// <summary>A spatial index definition applied to a geospatial field, enabling geography-aware queries.</summary>
    public class SpatialIndex
    {
        /// <summary>JSON property path of the geospatial field covered by this spatial index (e.g., "/location").</summary>
        public string Path { get; set; } = string.Empty;
        /// <summary>GeoJSON geometry types indexed at this path (e.g., "Point", "LineString", "Polygon", "MultiPolygon").</summary>
        public List<string> Types { get; set; } = new();
    }

    /// <summary>
    /// Performance metrics for a container over the analysis period
    /// </summary>
    public class ContainerPerformanceMetrics
    {
        /// <summary>Mean RU/s consumed by this container across the analysis window.</summary>
        public double AverageRUConsumption { get; set; }
        /// <summary>Highest observed RU/s consumed by this container at any single data point in the analysis window.</summary>
        public double PeakRUConsumption { get; set; }
        /// <summary>Mean end-to-end request latency for this container during the analysis window, in milliseconds.</summary>
        public double AverageLatencyMs { get; set; }
        /// <summary>Total number of requests (reads, writes, queries) issued against this container over the analysis period.</summary>
        public long TotalRequestCount { get; set; }
        /// <summary>Fraction of requests that were throttled (HTTP 429) due to insufficient RU/s, between 0.0 and 1.0.</summary>
        public double ThrottlingRate { get; set; }
        
        /// <summary>Most frequently executed query patterns for this container, keyed by a normalized query signature, with associated cost and latency metrics.</summary>
        public Dictionary<string, QueryMetrics> TopQueries { get; set; } = new();
        /// <summary>Partition key values identified as disproportionately hot, indicating skewed request distribution.</summary>
        public List<HotPartition> HotPartitions { get; set; } = new();
    }

    /// <summary>Execution statistics for a single normalized query pattern observed on a container.</summary>
    public class QueryMetrics
    {
        /// <summary>Normalized query template with literal values abstracted (e.g., "SELECT * FROM c WHERE c.status = @p1").</summary>
        public string QueryPattern { get; set; } = string.Empty;
        /// <summary>Total number of times this query pattern was executed during the analysis window.</summary>
        public long ExecutionCount { get; set; }
        /// <summary>Mean RU charge per execution of this query pattern.</summary>
        public double AverageRUs { get; set; }
        /// <summary>Mean end-to-end latency per execution of this query pattern, in milliseconds.</summary>
        public double AverageLatencyMs { get; set; }
    }

    /// <summary>Identifies a logical partition that receives a disproportionately high share of traffic within a container.</summary>
    public class HotPartition
    {
        /// <summary>Value of the container's partition key that identifies the hot partition (e.g., a specific tenant ID or region).</summary>
        public string PartitionKeyValue { get; set; } = string.Empty;
        /// <summary>Percentage of the container's total RU consumption attributed to this partition, between 0 and 100.</summary>
        public double RUConsumptionPercentage { get; set; }
        /// <summary>Total number of requests routed to this partition during the analysis window.</summary>
        public long RequestCount { get; set; }
    }

    /// <summary>
    /// Overall performance metrics for the database
    /// </summary>
    public class PerformanceMetrics
    {
        /// <summary>Start and end timestamps of the monitoring window used to gather these metrics (typically 6 months).</summary>
        public TimeRange AnalysisPeriod { get; set; } = new();
        /// <summary>Cumulative RU consumption across the entire analysis window for the database.</summary>
        public double TotalRUConsumption { get; set; }
        /// <summary>Mean throughput consumed, expressed in request units per second (RU/s), averaged over the analysis window.</summary>
        public double AverageRUsPerSecond { get; set; }
        /// <summary>Highest observed throughput at any single data point during the analysis window, in RU/s.</summary>
        public double PeakRUsPerSecond { get; set; }
        /// <summary>Mean end-to-end request latency across all operations in the analysis window, in milliseconds.</summary>
        public double AverageRequestLatencyMs { get; set; }
        /// <summary>Total count of all requests (reads, writes, queries, deletes) issued to the database during the analysis window.</summary>
        public long TotalRequests { get; set; }
        /// <summary>Fraction of all requests that resulted in an error (non-2xx response), between 0.0 and 1.0.</summary>
        public double ErrorRate { get; set; }
        /// <summary>Fraction of all requests that were rate-limited (HTTP 429) due to provisioned throughput being exceeded, between 0.0 and 1.0.</summary>
        public double ThrottlingRate { get; set; }
        
        /// <summary>Time-series trend observations for individual metrics over the analysis window (e.g., rising latency, increasing RU usage).</summary>
        public List<PerformanceTrend> Trends { get; set; } = new();
    }

    /// <summary>Represents a closed UTC time interval defined by a start and end timestamp.</summary>
    public class TimeRange
    {
        /// <summary>Inclusive start of the time interval (UTC).</summary>
        public DateTime StartTime { get; set; }
        /// <summary>Inclusive end of the time interval (UTC).</summary>
        public DateTime EndTime { get; set; }
    }

    /// <summary>Describes the directional trend of a single performance metric over the analysis window.</summary>
    public class PerformanceTrend
    {
        /// <summary>Name of the metric being trended (e.g., "RU/s", "Latency", "ThrottlingRate").</summary>
        public string MetricName { get; set; } = string.Empty;
        /// <summary>Qualitative direction of the trend over the analysis window: "Increasing", "Decreasing", or "Stable".</summary>
        public string Trend { get; set; } = string.Empty; // "Increasing", "Decreasing", "Stable"
        /// <summary>Relative change of the metric from the start to the end of the analysis window, expressed as a percentage (positive = increase, negative = decrease).</summary>
        public double ChangePercentage { get; set; }
    }

    /// <summary>
    /// Analysis result for array storage strategy decisions
    /// </summary>
    public class ArrayAnalysis
    {
        /// <summary>Name of the JSON array field being analyzed (e.g., "tags", "orderLines").</summary>
        public string ArrayName { get; set; } = string.Empty;
        /// <summary>Mean number of elements per array instance observed across sampled documents.</summary>
        public int ItemCount { get; set; }
        /// <summary>True when the array's cardinality and structure warrant creating a dedicated relational child table rather than storing it inline.</summary>
        public bool ShouldCreateTable { get; set; }
        /// <summary>Recommended storage strategy for this array: "JSON" (store as a JSON column), "DelimitedString" (pipe/comma-separated scalars), or "RelationalTable" (normalize into a child table).</summary>
        public string RecommendedStorage { get; set; } = string.Empty; // "JSON", "DelimitedString", "RelationalTable"
        /// <summary>SQL Server data type recommended for the storage column when the array is stored inline (e.g., "NVARCHAR(MAX)" for JSON).</summary>
        public string RecommendedSqlType { get; set; } = string.Empty;
        /// <summary>Description of the ADF or T-SQL transformation logic needed to convert the source JSON array into the recommended SQL storage format.</summary>
        public string TransformationLogic { get; set; } = string.Empty;
    }
}
