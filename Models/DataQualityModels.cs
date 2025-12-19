namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Comprehensive data quality analysis results for pre-migration assessment
    /// </summary>
    public class DataQualityAnalysis
    {
        public DateTime AnalysisDate { get; set; } = DateTime.UtcNow;
        public int TotalDocumentsAnalyzed { get; set; }
        public int TotalFieldsAnalyzed { get; set; }
        public int CriticalIssuesCount { get; set; }
        public int WarningIssuesCount { get; set; }
        public int InfoIssuesCount { get; set; }
        
        public List<ContainerQualityAnalysis> ContainerAnalyses { get; set; } = new();
        public List<DataQualityIssue> TopIssues { get; set; } = new();
        public DataQualitySummary Summary { get; set; } = new();
    }

    /// <summary>
    /// Data quality analysis for a specific container
    /// </summary>
    public class ContainerQualityAnalysis
    {
        public string ContainerName { get; set; } = string.Empty;
        public long DocumentCount { get; set; }
        public int SampleSize { get; set; }
        
        public List<NullAnalysisResult> NullAnalysis { get; set; } = new();
        public List<DuplicateAnalysisResult> DuplicateAnalysis { get; set; } = new();
        public List<TypeConsistencyResult> TypeConsistency { get; set; } = new();
        public List<OutlierAnalysisResult> OutlierAnalysis { get; set; } = new();
        public List<StringLengthAnalysisResult> StringLengthAnalysis { get; set; } = new();
        public List<EncodingIssue> EncodingIssues { get; set; } = new();
        public List<DateValidationResult> DateValidation { get; set; } = new();
        public List<DataQualityIssue> AllIssues { get; set; } = new();
    }

    /// <summary>
    /// Individual data quality issue with severity and recommendations
    /// </summary>
    public class DataQualityIssue
    {
        public string IssueId { get; set; } = Guid.NewGuid().ToString();
        public string ContainerName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public DataQualitySeverity Severity { get; set; }
        public string Category { get; set; } = string.Empty; // "Null", "Duplicate", "Type", "Outlier", "Length", "Encoding", "Date"
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Impact { get; set; } = string.Empty;
        public List<string> SampleRecordIds { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    /// <summary>
    /// Severity levels for data quality issues
    /// </summary>
    public enum DataQualitySeverity
    {
        Info = 0,       // Informational, no action required
        Warning = 1,    // Should be reviewed, may cause issues
        Critical = 2    // Must be addressed before migration
    }

    /// <summary>
    /// Null value analysis for a field
    /// </summary>
    public class NullAnalysisResult
    {
        public string FieldName { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty; // For nested fields
        public long TotalDocuments { get; set; }
        public long NullCount { get; set; }
        public long MissingCount { get; set; } // Field doesn't exist in document
        public double NullPercentage { get; set; }
        public double MissingPercentage { get; set; }
        public bool IsRecommendedRequired { get; set; } // Based on prevalence
        public bool WillImpactNotNullConstraint { get; set; }
        public List<string> SampleNullDocumentIds { get; set; } = new();
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Duplicate detection results
    /// </summary>
    public class DuplicateAnalysisResult
    {
        public string KeyType { get; set; } = string.Empty; // "ID", "BusinessKey", "PartitionKey"
        public List<string> KeyFields { get; set; } = new();
        public int DuplicateGroupCount { get; set; }
        public long TotalDuplicateRecords { get; set; }
        public double DuplicatePercentage { get; set; }
        public List<DuplicateGroup> TopDuplicateGroups { get; set; } = new();
        public string RecommendedResolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// Group of duplicate records
    /// </summary>
    public class DuplicateGroup
    {
        public string KeyValue { get; set; } = string.Empty;
        public int OccurrenceCount { get; set; }
        public List<string> DocumentIds { get; set; } = new();
        public Dictionary<string, object> SampleData { get; set; } = new();
    }

    /// <summary>
    /// Type consistency check across documents
    /// </summary>
    public class TypeConsistencyResult
    {
        public string FieldName { get; set; } = string.Empty;
        public Dictionary<string, long> TypeDistribution { get; set; } = new(); // Type -> Count
        public bool IsConsistent { get; set; }
        public string DominantType { get; set; } = string.Empty;
        public double DominantTypePercentage { get; set; }
        public List<TypeMismatchSample> Mismatches { get; set; } = new();
        public string RecommendedSqlType { get; set; } = string.Empty;
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of a type mismatch
    /// </summary>
    public class TypeMismatchSample
    {
        public string DocumentId { get; set; } = string.Empty;
        public string ActualType { get; set; } = string.Empty;
        public string ExpectedType { get; set; } = string.Empty;
        public object? SampleValue { get; set; }
    }

    /// <summary>
    /// Outlier detection for numeric fields
    /// </summary>
    public class OutlierAnalysisResult
    {
        public string FieldName { get; set; } = string.Empty;
        public long TotalValues { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double StandardDeviation { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public double Q1 { get; set; } // First quartile
        public double Q3 { get; set; } // Third quartile
        public double IQR { get; set; } // Interquartile range
        public int OutlierCount { get; set; }
        public double OutlierPercentage { get; set; }
        public List<OutlierSample> OutlierSamples { get; set; } = new();
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of an outlier value
    /// </summary>
    public class OutlierSample
    {
        public string DocumentId { get; set; } = string.Empty;
        public double Value { get; set; }
        public double ZScore { get; set; }
        public string OutlierType { get; set; } = string.Empty; // "Low" or "High"
    }

    /// <summary>
    /// String length analysis for VARCHAR sizing
    /// </summary>
    public class StringLengthAnalysisResult
    {
        public string FieldName { get; set; } = string.Empty;
        public long TotalValues { get; set; }
        public int MinLength { get; set; }
        public int MaxLength { get; set; }
        public double AverageLength { get; set; }
        public int MedianLength { get; set; }
        public int P95Length { get; set; } // 95th percentile
        public int P99Length { get; set; } // 99th percentile
        public Dictionary<int, int> LengthDistribution { get; set; } = new(); // Length bucket -> Count
        public string RecommendedSqlType { get; set; } = string.Empty;
        public List<StringLengthSample> ExtremeValueSamples { get; set; } = new();
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of extreme string length
    /// </summary>
    public class StringLengthSample
    {
        public string DocumentId { get; set; } = string.Empty;
        public int Length { get; set; }
        public string PreviewText { get; set; } = string.Empty; // First 100 chars
    }

    /// <summary>
    /// Character encoding and special character issues
    /// </summary>
    public class EncodingIssue
    {
        public string FieldName { get; set; } = string.Empty;
        public string IssueType { get; set; } = string.Empty; // "NonASCII", "ControlCharacters", "InvalidUTF8", "Emoji"
        public int AffectedDocumentCount { get; set; }
        public double AffectedPercentage { get; set; }
        public List<EncodingSample> Samples { get; set; } = new();
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of encoding issue
    /// </summary>
    public class EncodingSample
    {
        public string DocumentId { get; set; } = string.Empty;
        public string ProblematicValue { get; set; } = string.Empty;
        public string CharacterCodes { get; set; } = string.Empty; // Hex representation
        public string IssueDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// Date/timestamp validation results
    /// </summary>
    public class DateValidationResult
    {
        public string FieldName { get; set; } = string.Empty;
        public long TotalValues { get; set; }
        public int InvalidDateCount { get; set; }
        public int FutureDateCount { get; set; }
        public int VeryOldDateCount { get; set; } // Dates before reasonable threshold
        public double InvalidPercentage { get; set; }
        public DateTime? MinDate { get; set; }
        public DateTime? MaxDate { get; set; }
        public List<InvalidDateSample> InvalidSamples { get; set; } = new();
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of invalid date
    /// </summary>
    public class InvalidDateSample
    {
        public string DocumentId { get; set; } = string.Empty;
        public string RawValue { get; set; } = string.Empty;
        public string IssueType { get; set; } = string.Empty; // "Invalid", "Future", "TooOld"
        public string IssueDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// Summary of overall data quality
    /// </summary>
    public class DataQualitySummary
    {
        public double OverallQualityScore { get; set; } // 0-100
        public string QualityRating { get; set; } = string.Empty; // "Excellent", "Good", "Fair", "Poor"
        public bool ReadyForMigration { get; set; }
        public List<string> BlockingIssues { get; set; } = new(); // Critical issues that must be fixed
        public List<string> TopRecommendations { get; set; } = new();
        public Dictionary<string, int> IssuesByCategory { get; set; } = new();
        public Dictionary<DataQualitySeverity, int> IssuesBySeverity { get; set; } = new();
        public int EstimatedCleanupHours { get; set; }
    }

    /// <summary>
    /// Configuration for data quality analysis
    /// </summary>
    public class DataQualityAnalysisOptions
    {
        public int SampleSize { get; set; } = 1000; // Number of documents to sample per container
        public int MaxSampleRecords { get; set; } = 5; // Max sample records to include in results
        public double NullThresholdCritical { get; set; } = 0.15; // 15% nulls = critical
        public double NullThresholdWarning { get; set; } = 0.05; // 5% nulls = warning
        public double DuplicateThresholdCritical { get; set; } = 0.01; // 1% duplicates = critical
        public int OutlierZScoreThreshold { get; set; } = 3; // Z-score for outlier detection
        public int MaxStringLengthForVarchar { get; set; } = 4000; // Max length before suggesting TEXT/NVARCHAR(MAX)
        public DateTime MinReasonableDate { get; set; } = new DateTime(1900, 1, 1);
        public DateTime MaxReasonableDate { get; set; } = DateTime.UtcNow.AddYears(10);
        public bool IncludeEncodingChecks { get; set; } = true;
        public bool IncludeOutlierDetection { get; set; } = true;
        public bool IncludeDuplicateDetection { get; set; } = true;
    }
}
