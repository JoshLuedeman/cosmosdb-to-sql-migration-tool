namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Comprehensive data quality analysis results for pre-migration assessment
    /// </summary>
    public class DataQualityAnalysis
    {
        /// <summary>UTC timestamp when this data-quality analysis was executed.</summary>
        public DateTime AnalysisDate { get; set; } = DateTime.UtcNow;
        /// <summary>Total number of documents examined across all sampled containers.</summary>
        public int TotalDocumentsAnalyzed { get; set; }
        /// <summary>Total number of distinct field paths evaluated during the analysis.</summary>
        public int TotalFieldsAnalyzed { get; set; }
        /// <summary>Count of issues classified as Critical severity — must be resolved before migration.</summary>
        public int CriticalIssuesCount { get; set; }
        /// <summary>Count of issues classified as Warning severity — should be reviewed before migration.</summary>
        public int WarningIssuesCount { get; set; }
        /// <summary>Count of informational findings that require no immediate action.</summary>
        public int InfoIssuesCount { get; set; }

        /// <summary>Per-container quality analyses included in this assessment.</summary>
        public List<ContainerQualityAnalysis> ContainerAnalyses { get; set; } = new();
        /// <summary>Highest-priority issues selected for prominent display in summary reports.</summary>
        public List<DataQualityIssue> TopIssues { get; set; } = new();
        /// <summary>Rolled-up quality metrics and migration-readiness verdict for the entire assessment.</summary>
        public DataQualitySummary Summary { get; set; } = new();
    }

    /// <summary>
    /// Data quality analysis for a specific container
    /// </summary>
    public class ContainerQualityAnalysis
    {
        /// <summary>Name of the Cosmos DB container whose documents were profiled.</summary>
        public string ContainerName { get; set; } = string.Empty;
        /// <summary>Total number of documents in the container at analysis time.</summary>
        public long DocumentCount { get; set; }
        /// <summary>Number of documents randomly sampled for quality profiling.</summary>
        public int SampleSize { get; set; }

        /// <summary>Null and missing-value analysis results for each field in the container.</summary>
        public List<NullAnalysisResult> NullAnalysis { get; set; } = new();
        /// <summary>Duplicate-record detection results keyed by candidate key types.</summary>
        public List<DuplicateAnalysisResult> DuplicateAnalysis { get; set; } = new();
        /// <summary>Data-type consistency results for each field observed in the sampled documents.</summary>
        public List<TypeConsistencyResult> TypeConsistency { get; set; } = new();
        /// <summary>Statistical outlier analysis results for numeric fields in the container.</summary>
        public List<OutlierAnalysisResult> OutlierAnalysis { get; set; } = new();
        /// <summary>String-length distribution analysis results used to size SQL VARCHAR/NVARCHAR columns.</summary>
        public List<StringLengthAnalysisResult> StringLengthAnalysis { get; set; } = new();
        /// <summary>Character-encoding and special-character issues detected in string fields.</summary>
        public List<EncodingIssue> EncodingIssues { get; set; } = new();
        /// <summary>Date and timestamp validation results for fields whose values are parsed as dates.</summary>
        public List<DateValidationResult> DateValidation { get; set; } = new();
        /// <summary>Complete list of every data-quality issue found across all checks for this container.</summary>
        public List<DataQualityIssue> AllIssues { get; set; } = new();
    }

    /// <summary>
    /// Individual data quality issue with severity and recommendations
    /// </summary>
    public class DataQualityIssue
    {
        /// <summary>Unique identifier for this issue instance, generated at creation time.</summary>
        public string IssueId { get; set; } = Guid.NewGuid().ToString();
        /// <summary>Name of the Cosmos DB container in which this issue was detected.</summary>
        public string ContainerName { get; set; } = string.Empty;
        /// <summary>Dot-notation path of the field associated with this issue (e.g. "address.postalCode").</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Severity classification that determines whether migration should be blocked or just warned.</summary>
        public DataQualitySeverity Severity { get; set; }
        /// <summary>Broad category grouping this issue (e.g. "Null", "Duplicate", "Type", "Outlier", "Length", "Encoding", "Date").</summary>
        public string Category { get; set; } = string.Empty; // "Null", "Duplicate", "Type", "Outlier", "Length", "Encoding", "Date"
        /// <summary>Short, human-readable title summarising the issue for display in reports.</summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>Detailed explanation of what was detected and why it is a concern for migration.</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>Description of the potential migration impact if this issue is not addressed.</summary>
        public string Impact { get; set; } = string.Empty;
        /// <summary>Cosmos DB document IDs of representative records that exhibit this issue.</summary>
        public List<string> SampleRecordIds { get; set; } = new();
        /// <summary>Key-value bag of quantitative metrics supporting this issue (e.g. counts, percentages, thresholds).</summary>
        public Dictionary<string, object> Metrics { get; set; } = new();
        /// <summary>Ordered list of remediation steps to resolve or mitigate this issue before migration.</summary>
        public List<string> Recommendations { get; set; } = new();
    }

    /// <summary>
    /// Severity levels for data quality issues
    /// </summary>
    public enum DataQualitySeverity
    {
        /// <summary>Informational finding; no corrective action is required before migration.</summary>
        Info = 0,       // Informational, no action required
        /// <summary>Potential concern that should be reviewed; may cause data-loss or type-mismatch issues if left unaddressed.</summary>
        Warning = 1,    // Should be reviewed, may cause issues
        /// <summary>Blocking defect that must be resolved before migration can proceed safely.</summary>
        Critical = 2    // Must be addressed before migration
    }

    /// <summary>
    /// Null value analysis for a field
    /// </summary>
    public class NullAnalysisResult
    {
        /// <summary>Name of the field for which null and missing values were counted.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Dot-notation path to this field within the document hierarchy, used for nested fields.</summary>
        public string FieldPath { get; set; } = string.Empty; // For nested fields
        /// <summary>Total number of documents in the sample evaluated for this field.</summary>
        public long TotalDocuments { get; set; }
        /// <summary>Number of documents where the field is present but its value is explicitly null.</summary>
        public long NullCount { get; set; }
        /// <summary>Number of documents where the field is entirely absent (not just null).</summary>
        public long MissingCount { get; set; } // Field doesn't exist in document
        /// <summary>Fraction of sampled documents with an explicit null value, expressed as a percentage (0–100).</summary>
        public double NullPercentage { get; set; }
        /// <summary>Fraction of sampled documents where the field is missing entirely, expressed as a percentage (0–100).</summary>
        public double MissingPercentage { get; set; }
        /// <summary>Indicates whether the field's prevalence suggests it should be NOT NULL in the target SQL schema.</summary>
        public bool IsRecommendedRequired { get; set; } // Based on prevalence
        /// <summary>Indicates whether migrating this field with a NOT NULL constraint would be violated by the observed null or missing values.</summary>
        public bool WillImpactNotNullConstraint { get; set; }
        /// <summary>Cosmos DB document IDs of representative documents that contain a null value for this field.</summary>
        public List<string> SampleNullDocumentIds { get; set; } = new();
        /// <summary>Suggested remediation step, such as defaulting nulls, dropping the field, or relaxing the SQL constraint.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Duplicate detection results
    /// </summary>
    public class DuplicateAnalysisResult
    {
        /// <summary>Category of key used to detect duplicates (e.g. "ID", "BusinessKey", "PartitionKey").</summary>
        public string KeyType { get; set; } = string.Empty; // "ID", "BusinessKey", "PartitionKey"
        /// <summary>List of field names whose combined values form the duplicate-detection key.</summary>
        public List<string> KeyFields { get; set; } = new();
        /// <summary>Number of distinct duplicate groups found — each group shares the same key value.</summary>
        public int DuplicateGroupCount { get; set; }
        /// <summary>Total number of individual documents participating in at least one duplicate group.</summary>
        public long TotalDuplicateRecords { get; set; }
        /// <summary>Percentage of sampled documents that are duplicates, on a 0–100 scale.</summary>
        public double DuplicatePercentage { get; set; }
        /// <summary>Representative sample of the largest or most significant duplicate groups discovered.</summary>
        public List<DuplicateGroup> TopDuplicateGroups { get; set; } = new();
        /// <summary>Recommended strategy for resolving duplicates before migration (e.g. keep-latest, merge, manual review).</summary>
        public string RecommendedResolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// Group of duplicate records
    /// </summary>
    public class DuplicateGroup
    {
        /// <summary>Serialised representation of the duplicate key value shared by all documents in this group.</summary>
        public string KeyValue { get; set; } = string.Empty;
        /// <summary>Number of documents that share this key value.</summary>
        public int OccurrenceCount { get; set; }
        /// <summary>Cosmos DB document IDs of all records belonging to this duplicate group.</summary>
        public List<string> DocumentIds { get; set; } = new();
        /// <summary>Representative field values from one of the duplicate documents, useful for manual review.</summary>
        public Dictionary<string, object> SampleData { get; set; } = new();
    }

    /// <summary>
    /// Type consistency check across documents
    /// </summary>
    public class TypeConsistencyResult
    {
        /// <summary>Name of the field whose data-type consistency was evaluated across sampled documents.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Frequency distribution of JSON types observed for this field, mapping type name (e.g. "String", "Number") to occurrence count.</summary>
        public Dictionary<string, long> TypeDistribution { get; set; } = new(); // Type -> Count
        /// <summary>Indicates whether all sampled documents store this field using the same JSON type.</summary>
        public bool IsConsistent { get; set; }
        /// <summary>The JSON type observed most frequently for this field (e.g. "String", "Number", "Boolean").</summary>
        public string DominantType { get; set; } = string.Empty;
        /// <summary>Percentage of sampled documents whose value for this field matches the dominant type, on a 0–100 scale.</summary>
        public double DominantTypePercentage { get; set; }
        /// <summary>Sample documents where the field's type deviates from the dominant type.</summary>
        public List<TypeMismatchSample> Mismatches { get; set; } = new();
        /// <summary>Suggested Azure SQL column type (e.g. NVARCHAR, INT, FLOAT) inferred from the dominant JSON type and value range.</summary>
        public string RecommendedSqlType { get; set; } = string.Empty;
        /// <summary>Suggested corrective action such as casting, filtering, or splitting the field into multiple columns.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of a type mismatch
    /// </summary>
    public class TypeMismatchSample
    {
        /// <summary>Cosmos DB document ID of the record containing the mismatched value.</summary>
        public string DocumentId { get; set; } = string.Empty;
        /// <summary>JSON type of the value found in this document (e.g. "Number" when "String" is dominant).</summary>
        public string ActualType { get; set; } = string.Empty;
        /// <summary>The dominant JSON type that was expected for this field based on the rest of the sample.</summary>
        public string ExpectedType { get; set; } = string.Empty;
        /// <summary>The raw field value from the mismatched document, provided for manual inspection; may be null.</summary>
        public object? SampleValue { get; set; }
    }

    /// <summary>
    /// Outlier detection for numeric fields
    /// </summary>
    public class OutlierAnalysisResult
    {
        /// <summary>Name of the numeric field for which outlier analysis was performed.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Total number of non-null numeric values examined for this field.</summary>
        public long TotalValues { get; set; }
        /// <summary>Arithmetic mean of all numeric values for this field in the sample.</summary>
        public double Mean { get; set; }
        /// <summary>Middle value of the sorted distribution; more robust to outliers than the mean.</summary>
        public double Median { get; set; }
        /// <summary>Population standard deviation of all values, indicating spread around the mean.</summary>
        public double StandardDeviation { get; set; }
        /// <summary>Smallest numeric value observed for this field in the sample.</summary>
        public double MinValue { get; set; }
        /// <summary>Largest numeric value observed for this field in the sample.</summary>
        public double MaxValue { get; set; }
        /// <summary>First quartile (25th percentile) of the value distribution.</summary>
        public double Q1 { get; set; } // First quartile
        /// <summary>Third quartile (75th percentile) of the value distribution.</summary>
        public double Q3 { get; set; } // Third quartile
        /// <summary>Interquartile range (Q3 minus Q1), used as the basis for fence-based outlier detection.</summary>
        public double IQR { get; set; } // Interquartile range
        /// <summary>Number of values classified as outliers using the configured Z-score or IQR threshold.</summary>
        public int OutlierCount { get; set; }
        /// <summary>Percentage of total values classified as outliers, on a 0–100 scale.</summary>
        public double OutlierPercentage { get; set; }
        /// <summary>Representative sample of individual outlier values, including the document that contains each.</summary>
        public List<OutlierSample> OutlierSamples { get; set; } = new();
        /// <summary>Suggested action for handling outliers, such as capping, filtering, or reviewing specific records.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of an outlier value
    /// </summary>
    public class OutlierSample
    {
        /// <summary>Cosmos DB document ID of the record containing the outlier value.</summary>
        public string DocumentId { get; set; } = string.Empty;
        /// <summary>The numeric value that was classified as an outlier.</summary>
        public double Value { get; set; }
        /// <summary>Number of standard deviations this value lies from the field's mean; higher magnitude indicates a more extreme outlier.</summary>
        public double ZScore { get; set; }
        /// <summary>Direction of the outlier relative to the distribution — "Low" for values far below the mean, "High" for values far above.</summary>
        public string OutlierType { get; set; } = string.Empty; // "Low" or "High"
    }

    /// <summary>
    /// String length analysis for VARCHAR sizing
    /// </summary>
    public class StringLengthAnalysisResult
    {
        /// <summary>Name of the string field whose character-length distribution was analysed.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Total number of non-null string values examined for this field.</summary>
        public long TotalValues { get; set; }
        /// <summary>Shortest string length observed for this field in the sample (in characters).</summary>
        public int MinLength { get; set; }
        /// <summary>Longest string length observed for this field in the sample (in characters).</summary>
        public int MaxLength { get; set; }
        /// <summary>Mean character length across all sampled string values for this field.</summary>
        public double AverageLength { get; set; }
        /// <summary>Median character length of the sorted length distribution for this field.</summary>
        public int MedianLength { get; set; }
        /// <summary>95th-percentile character length — 95 % of observed values are at or below this length.</summary>
        public int P95Length { get; set; } // 95th percentile
        /// <summary>99th-percentile character length — 99 % of observed values are at or below this length.</summary>
        public int P99Length { get; set; } // 99th percentile
        /// <summary>Histogram of observed string lengths; maps each length bucket (in characters) to the count of values falling in that bucket.</summary>
        public Dictionary<int, int> LengthDistribution { get; set; } = new(); // Length bucket -> Count
        /// <summary>Suggested Azure SQL column type (e.g. VARCHAR(255), NVARCHAR(4000), NVARCHAR(MAX)) derived from the observed length distribution.</summary>
        public string RecommendedSqlType { get; set; } = string.Empty;
        /// <summary>Representative samples of the shortest and longest string values observed, for manual review.</summary>
        public List<StringLengthSample> ExtremeValueSamples { get; set; } = new();
        /// <summary>Suggested action for handling values that exceed SQL column size limits or that warrant special storage consideration.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of extreme string length
    /// </summary>
    public class StringLengthSample
    {
        /// <summary>Cosmos DB document ID of the record containing the extreme-length string value.</summary>
        public string DocumentId { get; set; } = string.Empty;
        /// <summary>Character length of the string value in this document.</summary>
        public int Length { get; set; }
        /// <summary>Truncated preview of the string value (up to the first 100 characters) for manual inspection.</summary>
        public string PreviewText { get; set; } = string.Empty; // First 100 chars
    }

    /// <summary>
    /// Character encoding and special character issues
    /// </summary>
    public class EncodingIssue
    {
        /// <summary>Name of the string field in which encoding or special-character issues were detected.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Category of encoding problem detected (e.g. "NonASCII", "ControlCharacters", "InvalidUTF8", "Emoji").</summary>
        public string IssueType { get; set; } = string.Empty; // "NonASCII", "ControlCharacters", "InvalidUTF8", "Emoji"
        /// <summary>Number of documents containing at least one instance of this encoding issue.</summary>
        public int AffectedDocumentCount { get; set; }
        /// <summary>Percentage of sampled documents affected by this encoding issue, on a 0–100 scale.</summary>
        public double AffectedPercentage { get; set; }
        /// <summary>Representative samples of documents exhibiting this encoding issue.</summary>
        public List<EncodingSample> Samples { get; set; } = new();
        /// <summary>Suggested remediation, such as normalising to UTF-8, stripping control characters, or replacing emojis.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of encoding issue
    /// </summary>
    public class EncodingSample
    {
        /// <summary>Cosmos DB document ID of the record containing the problematic string value.</summary>
        public string DocumentId { get; set; } = string.Empty;
        /// <summary>The field value that triggered the encoding issue, shown as stored in Cosmos DB.</summary>
        public string ProblematicValue { get; set; } = string.Empty;
        /// <summary>Hexadecimal representation of the problematic characters' code points, for precise identification.</summary>
        public string CharacterCodes { get; set; } = string.Empty; // Hex representation
        /// <summary>Human-readable explanation of the specific encoding problem found in this value.</summary>
        public string IssueDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// Date/timestamp validation results
    /// </summary>
    public class DateValidationResult
    {
        /// <summary>Name of the field whose values were parsed and validated as dates or timestamps.</summary>
        public string FieldName { get; set; } = string.Empty;
        /// <summary>Total number of non-null values examined for this field.</summary>
        public long TotalValues { get; set; }
        /// <summary>Number of values that could not be parsed as a valid date or timestamp.</summary>
        public int InvalidDateCount { get; set; }
        /// <summary>Number of values that are valid dates but fall after the current date, which may indicate data-entry errors.</summary>
        public int FutureDateCount { get; set; }
        /// <summary>Number of values that fall before the configured minimum reasonable date threshold, suggesting erroneous or sentinel values.</summary>
        public int VeryOldDateCount { get; set; } // Dates before reasonable threshold
        /// <summary>Percentage of values that failed date validation (unparseable, future, or too old), on a 0–100 scale.</summary>
        public double InvalidPercentage { get; set; }
        /// <summary>Earliest valid date observed for this field in the sample; null if no valid dates were found.</summary>
        public DateTime? MinDate { get; set; }
        /// <summary>Latest valid date observed for this field in the sample; null if no valid dates were found.</summary>
        public DateTime? MaxDate { get; set; }
        /// <summary>Representative samples of values that failed date validation, for manual review.</summary>
        public List<InvalidDateSample> InvalidSamples { get; set; } = new();
        /// <summary>Suggested remediation such as correcting formats, replacing sentinel values, or marking invalid dates as null.</summary>
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample of invalid date
    /// </summary>
    public class InvalidDateSample
    {
        /// <summary>Cosmos DB document ID of the record containing the invalid date value.</summary>
        public string DocumentId { get; set; } = string.Empty;
        /// <summary>The raw field value as stored in the document, before any parsing attempt.</summary>
        public string RawValue { get; set; } = string.Empty;
        /// <summary>Classification of the date problem: "Invalid" (unparseable), "Future" (after today), or "TooOld" (before the minimum reasonable threshold).</summary>
        public string IssueType { get; set; } = string.Empty; // "Invalid", "Future", "TooOld"
        /// <summary>Human-readable explanation of why this date value failed validation.</summary>
        public string IssueDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// Summary of overall data quality
    /// </summary>
    public class DataQualitySummary
    {
        /// <summary>Composite data-quality score on a 0–100 scale; higher values indicate cleaner data and lower migration risk.</summary>
        public double OverallQualityScore { get; set; } // 0-100
        /// <summary>Qualitative migration-readiness band derived from the score: "Excellent", "Good", "Fair", or "Poor".</summary>
        public string QualityRating { get; set; } = string.Empty; // "Excellent", "Good", "Fair", "Poor"
        /// <summary>Indicates whether all Critical-severity issues have been resolved and the data is safe to migrate.</summary>
        public bool ReadyForMigration { get; set; }
        /// <summary>Human-readable descriptions of Critical issues that must be resolved before migration can proceed.</summary>
        public List<string> BlockingIssues { get; set; } = new(); // Critical issues that must be fixed
        /// <summary>Prioritised list of remediation actions that would most improve overall data quality.</summary>
        public List<string> TopRecommendations { get; set; } = new();
        /// <summary>Count of issues grouped by category (e.g. "Null", "Duplicate"), for use in summary charts.</summary>
        public Dictionary<string, int> IssuesByCategory { get; set; } = new();
        /// <summary>Count of issues grouped by severity level, for use in summary charts and traffic-light indicators.</summary>
        public Dictionary<DataQualitySeverity, int> IssuesBySeverity { get; set; } = new();
        /// <summary>Rough estimate of the engineering effort required to remediate all identified issues, in person-hours.</summary>
        public int EstimatedCleanupHours { get; set; }
    }

    /// <summary>
    /// Configuration for data quality analysis
    /// </summary>
    public class DataQualityAnalysisOptions
    {
        /// <summary>Number of documents to randomly sample from each container; defaults to 1,000.</summary>
        public int SampleSize { get; set; } = 1000; // Number of documents to sample per container
        /// <summary>Maximum number of representative document IDs or values to embed in each issue result; defaults to 5.</summary>
        public int MaxSampleRecords { get; set; } = 5; // Max sample records to include in results
        /// <summary>Null-rate threshold above which an issue is promoted to Critical severity; expressed as a ratio (0–1), default 0.15 (15 %).</summary>
        public double NullThresholdCritical { get; set; } = 0.15; // 15% nulls = critical
        /// <summary>Null-rate threshold above which an issue is promoted to Warning severity; expressed as a ratio (0–1), default 0.05 (5 %).</summary>
        public double NullThresholdWarning { get; set; } = 0.05; // 5% nulls = warning
        /// <summary>Null-rate threshold above which an informational finding is raised; fields below this rate are ignored; expressed as a ratio (0–1), default 0.01 (1 %).</summary>
        public double NullThresholdInfo { get; set; } = 0.01; // 1% nulls = info (below this is ignored)
        /// <summary>Duplicate-rate threshold above which an issue is promoted to Critical severity; expressed as a ratio (0–1), default 0.01 (1 %).</summary>
        public double DuplicateThresholdCritical { get; set; } = 0.01; // 1% duplicates = critical
        /// <summary>Number of standard deviations from the mean at which a value is classified as a statistical outlier; default 3.</summary>
        public int OutlierZScoreThreshold { get; set; } = 3; // Z-score for outlier detection
        /// <summary>Minimum outlier percentage (0–100) required before an outlier finding is included in results; default 1.0 %.</summary>
        public double OutlierPercentageReportThreshold { get; set; } = 1.0; // Report outliers if >1% of values
        /// <summary>Character-length ceiling beyond which a string column is recommended as TEXT or NVARCHAR(MAX) rather than a fixed-length type; default 4,000.</summary>
        public int MaxStringLengthForVarchar { get; set; } = 4000; // Max length before suggesting TEXT/NVARCHAR(MAX)
        /// <summary>Minimum percentage (0–100) of values that must share the same JSON type for a field to be considered type-consistent; default 95.0 %.</summary>
        public double TypeConsistencyThreshold { get; set; } = 95.0; // 95% of values should be same type
        /// <summary>Minimum number of non-null numeric values required before outlier detection is run on a field; default 10.</summary>
        public int MinNumericValuesForOutlierAnalysis { get; set; } = 10; // Minimum values needed for outlier detection
        /// <summary>Minimum number of non-null string values required before string-length analysis is run on a field; default 5.</summary>
        public int MinStringValuesForLengthAnalysis { get; set; } = 5; // Minimum strings needed for length analysis
        /// <summary>Earliest date considered plausible for domain data; dates before this value are flagged as "TooOld"; defaults to 1 January 1900.</summary>
        public DateTime MinReasonableDate { get; set; } = new DateTime(1900, 1, 1);
        /// <summary>Latest date considered plausible for domain data; dates after this value are flagged as "Future"; defaults to 10 years from the current UTC date.</summary>
        public DateTime MaxReasonableDate { get; set; } = DateTime.UtcNow.AddYears(10);
        /// <summary>When true, checks for non-ASCII characters, control characters, invalid UTF-8 sequences, and emoji in string fields.</summary>
        public bool IncludeEncodingChecks { get; set; } = true;
        /// <summary>When true, runs IQR and Z-score outlier detection on numeric fields.</summary>
        public bool IncludeOutlierDetection { get; set; } = true;
        /// <summary>When true, scans for duplicate documents using the configured candidate key types.</summary>
        public bool IncludeDuplicateDetection { get; set; } = true;
    }
}
