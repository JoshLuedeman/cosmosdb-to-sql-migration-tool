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
        public List<IndexRecommendation> IndexRecommendations { get; set; } = new();
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
        public List<string> RequiredTransformations { get; set; } = new();
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
