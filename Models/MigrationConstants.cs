namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Constants used across the migration assessment and reporting
    /// </summary>
    public static class MigrationConstants
    {
        /// <summary>
        /// Row count thresholds for migration warnings
        /// </summary>
        public static class RowCountThresholds
        {
            /// <summary>
            /// Warning threshold: Tables with more than this many rows require monitoring
            /// </summary>
            public const long Warning = 1_000_000; // 1M rows - Yellow warning

            /// <summary>
            /// High priority threshold: Tables with more than this many rows need special consideration
            /// </summary>
            public const long HighPriority = 10_000_000; // 10M rows - Orange warning

            /// <summary>
            /// Critical threshold: Tables with more than this many rows require careful planning
            /// </summary>
            public const long Critical = 100_000_000; // 100M rows - Red warning
        }
    }
}
