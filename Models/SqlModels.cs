namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// SQL migration assessment results and recommendations
    /// </summary>
    public class SqlMigrationAssessment
    {
        /// <summary>Recommended Azure SQL target platform for the migrated workload (e.g. "SQL Database", "Managed Instance", "SQL Server on VM").</summary>
        public string RecommendedPlatform { get; set; } = string.Empty;
        /// <summary>Recommended service tier within the target platform (e.g. "General Purpose", "Business Critical", "Hyperscale").</summary>
        public string RecommendedTier { get; set; } = string.Empty;
        /// <summary>Per-database mappings that describe how each Cosmos DB database maps to a SQL database target.</summary>
        public List<DatabaseMapping> DatabaseMappings { get; set; } = new();
        /// <summary>Deduplicated shared schemas identified across containers; child tables that share the same structure reference a single entry here.</summary>
        public List<SharedSchema> SharedSchemas { get; set; } = new(); // New: Tracks deduplicated schemas
        /// <summary>Index recommendations for all target SQL tables, ordered by priority.</summary>
        public List<IndexRecommendation> IndexRecommendations { get; set; } = new();
        /// <summary>Recommended foreign-key constraints to enforce referential integrity between parent and child SQL tables.</summary>
        public List<ForeignKeyConstraint> ForeignKeyConstraints { get; set; } = new();
        /// <summary>Recommended unique constraints to enforce business-key uniqueness on target SQL tables.</summary>
        public List<UniqueConstraint> UniqueConstraints { get; set; } = new();
        /// <summary>Overall migration complexity rating together with the individual factors, risk items, and working assumptions that produced it.</summary>
        public MigrationComplexity Complexity { get; set; } = new();
        /// <summary>Data-transformation rules that must be applied during migration to convert Cosmos DB document shapes into relational rows.</summary>
        public List<TransformationRule> TransformationRules { get; set; } = new();
    }

    /// <summary>
    /// Mapping from Cosmos DB structure to SQL database structure
    /// </summary>
    public class DatabaseMapping
    {
        /// <summary>Name of the Cosmos DB database being migrated.</summary>
        public string SourceDatabase { get; set; } = string.Empty;
        /// <summary>Name of the Azure SQL database that will receive the migrated data.</summary>
        public string TargetDatabase { get; set; } = string.Empty;
        /// <summary>Container-level mappings that describe how each Cosmos DB container within this database maps to one or more SQL tables.</summary>
        public List<ContainerMapping> ContainerMappings { get; set; } = new();
    }

    /// <summary>
    /// Mapping from Cosmos DB container to SQL table
    /// </summary>
    public class ContainerMapping
    {
        /// <summary>Name of the Cosmos DB container (collection) being mapped.</summary>
        public string SourceContainer { get; set; } = string.Empty;
        /// <summary>SQL schema name that owns the target table (defaults to "dbo").</summary>
        public string TargetSchema { get; set; } = "dbo";
        /// <summary>Name of the primary SQL table that will store documents from this container.</summary>
        public string TargetTable { get; set; } = string.Empty;
        /// <summary>Column-level mappings from Cosmos DB document fields to SQL table columns.</summary>
        public List<FieldMapping> FieldMappings { get; set; } = new();
        /// <summary>Mappings for normalized child tables derived from embedded arrays and nested objects in documents.</summary>
        public List<ChildTableMapping> ChildTableMappings { get; set; } = new();
        /// <summary>Descriptions of data transformations required before or during loading rows into the target table.</summary>
        public List<string> RequiredTransformations { get; set; } = new();
        /// <summary>Approximate number of rows expected in the target table after migration, derived from source document count.</summary>
        public long EstimatedRowCount { get; set; }
    }

    /// <summary>
    /// Mapping from document field to SQL column
    /// </summary>
    public class FieldMapping
    {
        /// <summary>Dot-notation path of the field in the Cosmos DB document (e.g. "address.city").</summary>
        public string SourceField { get; set; } = string.Empty;
        /// <summary>JSON/Cosmos DB data type of the source field (e.g. "string", "number", "boolean", "object", "array").</summary>
        public string SourceType { get; set; } = string.Empty;
        /// <summary>Name of the destination column in the target SQL table.</summary>
        public string TargetColumn { get; set; } = string.Empty;
        /// <summary>SQL data type for the target column (e.g. "NVARCHAR(255)", "BIGINT", "BIT", "DATETIME2").</summary>
        public string TargetType { get; set; } = string.Empty;
        /// <summary>Indicates whether the field value must be transformed before it can be stored in the target SQL column.</summary>
        public bool RequiresTransformation { get; set; }
        /// <summary>Description or pseudo-code of the transformation logic needed to convert the source value to the target column type.</summary>
        public string TransformationLogic { get; set; } = string.Empty;
        /// <summary>Indicates whether this field is (or is part of) the Cosmos DB container partition key.</summary>
        public bool IsPartitionKey { get; set; }
        /// <summary>Indicates whether the target SQL column should allow NULL values; defaults to true to reflect the schema-less nature of Cosmos DB documents.</summary>
        public bool IsNullable { get; set; } = true;
    }

    /// <summary>
    /// Mapping for child tables (normalized from arrays and nested objects)
    /// </summary>
    public class ChildTableMapping
    {
        /// <summary>Dot-notation path within the parent document that contains the array or nested object being lifted into this child table (e.g. "orders.lineItems").</summary>
        public string SourceFieldPath { get; set; } = string.Empty;
        /// <summary>Structural kind of the source field: "Array" for JSON arrays normalized into rows, or "NestedObject" for embedded objects extracted into a separate table.</summary>
        public string ChildTableType { get; set; } = string.Empty; // "Array" or "NestedObject"
        /// <summary>SQL schema name that owns the child table (defaults to "dbo").</summary>
        public string TargetSchema { get; set; } = "dbo";
        /// <summary>Name of the child SQL table that will hold the normalized array elements or nested-object rows.</summary>
        public string TargetTable { get; set; } = string.Empty;
        /// <summary>Name of the foreign-key column in this child table that references the parent table's primary key (defaults to "ParentId").</summary>
        public string ParentKeyColumn { get; set; } = "ParentId";
        /// <summary>Column-level mappings from the nested document fields to child table columns.</summary>
        public List<FieldMapping> FieldMappings { get; set; } = new();
        /// <summary>Descriptions of additional transformations required when loading data into this child table.</summary>
        public List<string> RequiredTransformations { get; set; } = new();
        /// <summary>Identifier of the shared schema this child table references when its structure has been deduplicated; null if the child table has its own dedicated schema.</summary>
        public string? SharedSchemaId { get; set; } = null; // Reference to shared schema if deduplicated
    }

    /// <summary>
    /// Represents a shared schema that multiple child tables can reference
    /// </summary>
    public class SharedSchema
    {
        /// <summary>Unique identifier for this shared schema, derived by hashing the canonical field structure so equivalent schemas are always assigned the same key.</summary>
        public string SchemaId { get; set; } = string.Empty; // Unique identifier based on schema hash
        /// <summary>Human-readable name for the shared schema, derived from common business-domain terminology (e.g. "Address", "ContactInfo").</summary>
        public string SchemaName { get; set; } = string.Empty; // Friendly name (e.g., "Address", "ContactInfo")
        /// <summary>SQL schema name that owns the shared table (defaults to "dbo").</summary>
        public string TargetSchema { get; set; } = "dbo";
        /// <summary>Name of the shared SQL table that consolidates equivalent child-table structures from multiple containers.</summary>
        public string TargetTable { get; set; } = string.Empty; // The shared table name
        /// <summary>Column-level mappings that define the structure of the shared SQL table.</summary>
        public List<FieldMapping> FieldMappings { get; set; } = new();
        /// <summary>Names of the Cosmos DB containers whose documents reference this shared schema.</summary>
        public List<string> SourceContainers { get; set; } = new(); // Containers that use this schema
        /// <summary>Dot-notation field paths across all source containers that map to this shared schema.</summary>
        public List<string> SourceFieldPaths { get; set; } = new(); // Field paths that use this schema
        /// <summary>Total number of source field paths (across all containers) that reference this shared schema; higher values indicate broader reuse.</summary>
        public int UsageCount { get; set; } // Number of times this schema is used
        /// <summary>Hash of the normalized field structure used to detect duplicate schemas across containers; two schemas with the same hash are structurally identical.</summary>
        public string SchemaHash { get; set; } = string.Empty; // Hash of field structure for comparison
    }

    /// <summary>
    /// Index recommendations for SQL tables
    /// </summary>
    public class IndexRecommendation
    {
        /// <summary>Name of the SQL table this index applies to, including schema prefix where relevant.</summary>
        public string TableName { get; set; } = string.Empty;
        /// <summary>Proposed name for the index object in the target database (e.g. "IX_Orders_CustomerId").</summary>
        public string IndexName { get; set; } = string.Empty;
        /// <summary>SQL index type: "Clustered", "NonClustered", "Unique", or "ColumnStore".</summary>
        public string IndexType { get; set; } = string.Empty; // "Clustered", "NonClustered", "Unique", "ColumnStore"
        /// <summary>Ordered list of key columns included in the index definition.</summary>
        public List<string> Columns { get; set; } = new();
        /// <summary>Non-key columns included in the index to support covering-index query patterns (relevant for non-clustered indexes).</summary>
        public List<string> IncludedColumns { get; set; } = new();
        /// <summary>Explanation of why this index is recommended, referencing the query patterns or access paths it optimizes.</summary>
        public string Justification { get; set; } = string.Empty;
        /// <summary>Relative implementation priority; lower values indicate higher urgency (e.g. 1 = highest priority).</summary>
        public int Priority { get; set; }
        /// <summary>Estimated query-cost reduction expressed in Cosmos DB Request Units (RUs), indicating the workload benefit of adding this index to the SQL target.</summary>
        public long EstimatedImpactRUs { get; set; }
    }

    /// <summary>
    /// Foreign key constraint recommendations for referential integrity
    /// </summary>
    public class ForeignKeyConstraint
    {
        /// <summary>Name of the foreign-key constraint object in the target database (e.g. "FK_OrderItems_Orders").</summary>
        public string ConstraintName { get; set; } = string.Empty;
        /// <summary>Name of the child (referencing) table that holds the foreign-key column.</summary>
        public string ChildTable { get; set; } = string.Empty;
        /// <summary>Name of the column in the child table that references the parent table's primary key.</summary>
        public string ChildColumn { get; set; } = string.Empty;
        /// <summary>Name of the parent (referenced) table whose primary key is referenced by this constraint.</summary>
        public string ParentTable { get; set; } = string.Empty;
        /// <summary>Name of the column in the parent table (typically the primary key) that is referenced by the child column.</summary>
        public string ParentColumn { get; set; } = string.Empty;
        /// <summary>Referential action taken on child rows when the parent row is deleted: "CASCADE", "SET NULL", "RESTRICT", or "NO ACTION".</summary>
        public string OnDeleteAction { get; set; } = "CASCADE"; // CASCADE, SET NULL, RESTRICT, NO ACTION
        /// <summary>Referential action taken on child rows when the referenced parent key value is updated: "CASCADE", "SET NULL", "RESTRICT", or "NO ACTION".</summary>
        public string OnUpdateAction { get; set; } = "CASCADE";
        /// <summary>Explanation of the business relationship that this foreign-key constraint enforces.</summary>
        public string Justification { get; set; } = string.Empty;
        /// <summary>Indicates whether constraint checking can be deferred to the end of a transaction; useful for bulk-load scenarios where parent and child rows are inserted out of order.</summary>
        public bool IsDeferrable { get; set; } = false;
    }

    /// <summary>
    /// Unique constraint recommendations for business keys
    /// </summary>
    public class UniqueConstraint
    {
        /// <summary>Name of the unique constraint or primary-key object in the target database (e.g. "UQ_Customers_Email").</summary>
        public string ConstraintName { get; set; } = string.Empty;
        /// <summary>Name of the SQL table on which this uniqueness constraint is defined.</summary>
        public string TableName { get; set; } = string.Empty;
        /// <summary>Ordered list of columns whose combined values must be unique across all rows in the table.</summary>
        public List<string> Columns { get; set; } = new();
        /// <summary>Kind of uniqueness constraint: "UNIQUE" for a secondary unique index, or "PRIMARY KEY" for the table's primary identifier.</summary>
        public string ConstraintType { get; set; } = string.Empty; // "UNIQUE", "PRIMARY KEY"
        /// <summary>Explanation of the business rule or data-integrity requirement that this constraint enforces.</summary>
        public string Justification { get; set; } = string.Empty;
        /// <summary>Indicates whether the uniqueness is enforced across multiple columns; true when the Columns list contains more than one entry.</summary>
        public bool IsComposite { get; set; } = false;
    }

    /// <summary>
    /// Assessment of migration complexity
    /// </summary>
    public class MigrationComplexity
    {
        /// <summary>Aggregate complexity rating for the entire migration: "Low", "Medium", or "High".</summary>
        public string OverallComplexity { get; set; } = string.Empty; // "Low", "Medium", "High"
        /// <summary>Individual factors that contributed to the overall complexity rating, each with its own impact level and description.</summary>
        public List<ComplexityFactor> ComplexityFactors { get; set; } = new();
        /// <summary>Estimated calendar days required to complete the full migration, including schema design, data movement, validation, and cutover.</summary>
        public int EstimatedMigrationDays { get; set; }
        /// <summary>Identified risks that could cause delays, data loss, or post-migration issues if not addressed proactively.</summary>
        public List<string> Risks { get; set; } = new();
        /// <summary>Working assumptions made during the assessment that affect the complexity rating and timeline estimate.</summary>
        public List<string> Assumptions { get; set; } = new();
    }

    /// <summary>A single factor that contributes to the overall migration complexity rating.</summary>
    public class ComplexityFactor
    {
        /// <summary>Short name identifying the complexity factor (e.g. "Schema Heterogeneity", "Data Volume", "Cross-Container Joins").</summary>
        public string Factor { get; set; } = string.Empty;
        /// <summary>Severity of this factor's contribution to the overall complexity: "Low", "Medium", or "High".</summary>
        public string Impact { get; set; } = string.Empty; // "Low", "Medium", "High"
        /// <summary>Detailed explanation of how this factor affects the migration effort and what must be done to address it.</summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Data transformation rules for migration
    /// </summary>
    public class TransformationRule
    {
        /// <summary>Short identifier for this transformation rule, used to reference it from container or field mappings (e.g. "FlattenAddress", "SplitFullName").</summary>
        public string RuleName { get; set; } = string.Empty;
        /// <summary>Description or pattern of the source document structure that triggers this rule (e.g. "Nested 'address' object with city/state/zip fields").</summary>
        public string SourcePattern { get; set; } = string.Empty;
        /// <summary>Description or pattern of the expected target SQL structure produced after applying this rule.</summary>
        public string TargetPattern { get; set; } = string.Empty;
        /// <summary>Category of transformation: "Flatten" (nested object to columns), "Split" (one field to many), "Combine" (many fields to one), or "TypeConvert" (data-type coercion).</summary>
        public string TransformationType { get; set; } = string.Empty; // "Flatten", "Split", "Combine", "TypeConvert"
        /// <summary>Pseudo-code or SQL expression describing the exact transformation logic to implement in the migration pipeline.</summary>
        public string Logic { get; set; } = string.Empty;
        /// <summary>Names of the SQL tables whose data is affected by this transformation rule.</summary>
        public List<string> AffectedTables { get; set; } = new();
    }

    /// <summary>
    /// Azure Data Factory migration estimates
    /// </summary>
    public class DataFactoryEstimate
    {
        /// <summary>Total wall-clock time projected for the Azure Data Factory migration run, accounting for parallelism and DIU allocation.</summary>
        public TimeSpan EstimatedDuration { get; set; }
        /// <summary>Total compressed data volume to be migrated, in gigabytes (GB).</summary>
        public long TotalDataSizeGB { get; set; }
        /// <summary>Recommended number of Data Integration Units (DIUs) to allocate to the ADF copy activity; higher values reduce duration at increased cost.</summary>
        public int RecommendedDIUs { get; set; }
        /// <summary>Recommended degree of parallelism for ADF copy activities running simultaneously across different source containers.</summary>
        public int RecommendedParallelCopies { get; set; }
        /// <summary>Projected total Azure Data Factory cost for the migration run, in US dollars (USD).</summary>
        public decimal EstimatedCostUSD { get; set; }
        /// <summary>Per-pipeline cost and duration estimates for each source container being migrated.</summary>
        public List<PipelineEstimate> PipelineEstimates { get; set; } = new();
        /// <summary>Infrastructure and configuration prerequisites that must be completed before the ADF migration pipelines can run successfully.</summary>
        public List<string> Prerequisites { get; set; } = new();
        /// <summary>Operational recommendations for tuning, monitoring, and validating the ADF migration pipelines.</summary>
        public List<string> Recommendations { get; set; } = new();
    }

    /// <summary>Duration and size estimate for a single Azure Data Factory pipeline that migrates one Cosmos DB container to a SQL table.</summary>
    public class PipelineEstimate
    {
        /// <summary>Name of the Cosmos DB container that serves as the data source for this pipeline.</summary>
        public string SourceContainer { get; set; } = string.Empty;
        /// <summary>Name of the target SQL table that this pipeline writes migrated data into.</summary>
        public string TargetTable { get; set; } = string.Empty;
        /// <summary>Estimated size of the data moved by this pipeline, in gigabytes (GB).</summary>
        public long DataSizeGB { get; set; }
        /// <summary>Projected run time for this pipeline, based on data size, DIU allocation, and transformation complexity.</summary>
        public TimeSpan EstimatedDuration { get; set; }
        /// <summary>Indicates whether this pipeline includes a transformation activity (e.g. mapping data flow or stored-procedure call) in addition to a raw copy.</summary>
        public bool RequiresTransformation { get; set; }
        /// <summary>Qualitative complexity of the transformation logic within this pipeline: "Low", "Medium", or "High".</summary>
        public string TransformationComplexity { get; set; } = string.Empty;
    }

    /// <summary>
    /// General recommendation item
    /// </summary>
    public class RecommendationItem
    {
        /// <summary>Broad category grouping this recommendation (e.g. "Performance", "Security", "Cost Optimization", "Schema Design").</summary>
        public string Category { get; set; } = string.Empty;
        /// <summary>Short headline summarising the recommendation.</summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>Full explanation of the recommendation, including context and the problem it addresses.</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>Urgency or importance of this recommendation relative to other items: "High", "Medium", or "Low".</summary>
        public string Priority { get; set; } = string.Empty; // "High", "Medium", "Low"
        /// <summary>Expected benefit or outcome if this recommendation is implemented (e.g. "Reduces query latency by up to 40%").</summary>
        public string Impact { get; set; } = string.Empty;
        /// <summary>Concrete steps the migration team should take to implement this recommendation.</summary>
        public List<string> ActionItems { get; set; } = new();
    }
}
