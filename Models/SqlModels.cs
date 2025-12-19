namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// SQL migration assessment results and recommendations
    /// </summary>
    public class SqlMigrationAssessment
    {
        public string RecommendedPlatform { get; set; } = string.Empty;
        public string RecommendedTier { get; set; } = string.Empty;
        public List<DatabaseMapping> DatabaseMappings { get; set; } = new();
        public List<SharedSchema> SharedSchemas { get; set; } = new(); // New: Tracks deduplicated schemas
        public List<IndexRecommendation> IndexRecommendations { get; set; } = new();
        public List<ForeignKeyConstraint> ForeignKeyConstraints { get; set; } = new();
        public List<UniqueConstraint> UniqueConstraints { get; set; } = new();
        public MigrationComplexity Complexity { get; set; } = new();
        public List<TransformationRule> TransformationRules { get; set; } = new();
    }

    /// <summary>
    /// Mapping from Cosmos DB structure to SQL database structure
    /// </summary>
    public class DatabaseMapping
    {
        public string SourceDatabase { get; set; } = string.Empty;
        public string TargetDatabase { get; set; } = string.Empty;
        public List<ContainerMapping> ContainerMappings { get; set; } = new();
    }

    /// <summary>
    /// Mapping from Cosmos DB container to SQL table
    /// </summary>
    public class ContainerMapping
    {
        public string SourceContainer { get; set; } = string.Empty;
        public string TargetSchema { get; set; } = "dbo";
        public string TargetTable { get; set; } = string.Empty;
        public List<FieldMapping> FieldMappings { get; set; } = new();
        public List<ChildTableMapping> ChildTableMappings { get; set; } = new();
        public List<string> RequiredTransformations { get; set; } = new();
        public long EstimatedRowCount { get; set; }
    }

    /// <summary>
    /// Mapping from document field to SQL column
    /// </summary>
    public class FieldMapping
    {
        public string SourceField { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string TargetColumn { get; set; } = string.Empty;
        public string TargetType { get; set; } = string.Empty;
        public bool RequiresTransformation { get; set; }
        public string TransformationLogic { get; set; } = string.Empty;
        public bool IsPartitionKey { get; set; }
        public bool IsNullable { get; set; } = true;
    }

    /// <summary>
    /// Mapping for child tables (normalized from arrays and nested objects)
    /// </summary>
    public class ChildTableMapping
    {
        public string SourceFieldPath { get; set; } = string.Empty;
        public string ChildTableType { get; set; } = string.Empty; // "Array" or "NestedObject"
        public string TargetSchema { get; set; } = "dbo";
        public string TargetTable { get; set; } = string.Empty;
        public string ParentKeyColumn { get; set; } = "ParentId";
        public List<FieldMapping> FieldMappings { get; set; } = new();
        public List<string> RequiredTransformations { get; set; } = new();
        public string? SharedSchemaId { get; set; } = null; // Reference to shared schema if deduplicated
    }

    /// <summary>
    /// Represents a shared schema that multiple child tables can reference
    /// </summary>
    public class SharedSchema
    {
        public string SchemaId { get; set; } = string.Empty; // Unique identifier based on schema hash
        public string SchemaName { get; set; } = string.Empty; // Friendly name (e.g., "Address", "ContactInfo")
        public string TargetSchema { get; set; } = "dbo";
        public string TargetTable { get; set; } = string.Empty; // The shared table name
        public List<FieldMapping> FieldMappings { get; set; } = new();
        public List<string> SourceContainers { get; set; } = new(); // Containers that use this schema
        public List<string> SourceFieldPaths { get; set; } = new(); // Field paths that use this schema
        public int UsageCount { get; set; } // Number of times this schema is used
        public string SchemaHash { get; set; } = string.Empty; // Hash of field structure for comparison
    }

    /// <summary>
    /// Index recommendations for SQL tables
    /// </summary>
    public class IndexRecommendation
    {
        public string TableName { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
        public string IndexType { get; set; } = string.Empty; // "Clustered", "NonClustered", "Unique", "ColumnStore"
        public List<string> Columns { get; set; } = new();
        public List<string> IncludedColumns { get; set; } = new();
        public string Justification { get; set; } = string.Empty;
        public int Priority { get; set; }
        public long EstimatedImpactRUs { get; set; }
    }

    /// <summary>
    /// Foreign key constraint recommendations for referential integrity
    /// </summary>
    public class ForeignKeyConstraint
    {
        public string ConstraintName { get; set; } = string.Empty;
        public string ChildTable { get; set; } = string.Empty;
        public string ChildColumn { get; set; } = string.Empty;
        public string ParentTable { get; set; } = string.Empty;
        public string ParentColumn { get; set; } = string.Empty;
        public string OnDeleteAction { get; set; } = "CASCADE"; // CASCADE, SET NULL, RESTRICT, NO ACTION
        public string OnUpdateAction { get; set; } = "CASCADE";
        public string Justification { get; set; } = string.Empty;
        public bool IsDeferrable { get; set; } = false;
    }

    /// <summary>
    /// Unique constraint recommendations for business keys
    /// </summary>
    public class UniqueConstraint
    {
        public string ConstraintName { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public string ConstraintType { get; set; } = string.Empty; // "UNIQUE", "PRIMARY KEY"
        public string Justification { get; set; } = string.Empty;
        public bool IsComposite { get; set; } = false;
    }

    /// <summary>
    /// Assessment of migration complexity
    /// </summary>
    public class MigrationComplexity
    {
        public string OverallComplexity { get; set; } = string.Empty; // "Low", "Medium", "High"
        public List<ComplexityFactor> ComplexityFactors { get; set; } = new();
        public int EstimatedMigrationDays { get; set; }
        public List<string> Risks { get; set; } = new();
        public List<string> Assumptions { get; set; } = new();
    }

    public class ComplexityFactor
    {
        public string Factor { get; set; } = string.Empty;
        public string Impact { get; set; } = string.Empty; // "Low", "Medium", "High"
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Data transformation rules for migration
    /// </summary>
    public class TransformationRule
    {
        public string RuleName { get; set; } = string.Empty;
        public string SourcePattern { get; set; } = string.Empty;
        public string TargetPattern { get; set; } = string.Empty;
        public string TransformationType { get; set; } = string.Empty; // "Flatten", "Split", "Combine", "TypeConvert"
        public string Logic { get; set; } = string.Empty;
        public List<string> AffectedTables { get; set; } = new();
    }

    /// <summary>
    /// Azure Data Factory migration estimates
    /// </summary>
    public class DataFactoryEstimate
    {
        public TimeSpan EstimatedDuration { get; set; }
        public long TotalDataSizeGB { get; set; }
        public int RecommendedDIUs { get; set; }
        public int RecommendedParallelCopies { get; set; }
        public decimal EstimatedCostUSD { get; set; }
        
        public List<PipelineEstimate> PipelineEstimates { get; set; } = new();
        public List<string> Prerequisites { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    public class PipelineEstimate
    {
        public string SourceContainer { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public long DataSizeGB { get; set; }
        public TimeSpan EstimatedDuration { get; set; }
        public bool RequiresTransformation { get; set; }
        public string TransformationComplexity { get; set; } = string.Empty;
    }

    /// <summary>
    /// General recommendation item
    /// </summary>
    public class RecommendationItem
    {
        public string Category { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty; // "High", "Medium", "Low"
        public string Impact { get; set; } = string.Empty;
        public List<string> ActionItems { get; set; } = new();
    }
}
