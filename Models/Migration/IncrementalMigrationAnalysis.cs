namespace CosmosToSqlAssessment.Models.Migration
{
    /// <summary>
    /// Aggregate result for the incremental (change-feed-based) migration assessment of a database.
    /// Attached to <see cref="AssessmentResult"/> as an optional analysis. Each parent #69 sub-issue
    /// populates one facet: change feed availability (#134), sync time estimates (#135), cutover window
    /// (#136), the phased plan (#137), time-based partitioning (#138), and change feed processor
    /// guidance (#140).
    /// </summary>
    public sealed class IncrementalMigrationAnalysis
    {
        /// <summary>
        /// Per-container change feed availability and readiness for incremental synchronization (#134).
        /// </summary>
        public ChangeFeedAvailabilityAnalysis ChangeFeed { get; set; } = new();
    }
}
