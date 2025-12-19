using System;
using System.Collections.Generic;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.SqlProject
{
    /// <summary>
    /// Represents a complete SQL Database Project generated from migration assessment
    /// Contains all necessary files and metadata for deployment to Azure SQL
    /// </summary>
    public class SqlDatabaseProject
    {
        /// <summary>
        /// Name of the SQL Database Project
        /// </summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// Base output directory path for the project
        /// </summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the main .sqlproj file
        /// </summary>
        public string ProjectFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Date and time when the project was created
        /// </summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// Source migration assessment used to generate this project
        /// </summary>
        public SqlMigrationAssessment Assessment { get; set; } = new();

        /// <summary>
        /// List of directory paths created for the project structure
        /// </summary>
        public List<string> ProjectStructure { get; set; } = new();

        /// <summary>
        /// List of table creation script file paths
        /// </summary>
        public List<string> TableScripts { get; set; } = new();

        /// <summary>
        /// List of index creation script file paths
        /// </summary>
        public List<string> IndexScripts { get; set; } = new();

        /// <summary>
        /// List of stored procedure script file paths
        /// </summary>
        public List<string> StoredProcedureScripts { get; set; } = new();

        /// <summary>
        /// List of deployment script file paths (pre and post deployment)
        /// </summary>
        public List<string> DeploymentScripts { get; set; } = new();

        /// <summary>
        /// List of data migration script file paths
        /// </summary>
        public List<string> DataMigrationScripts { get; set; } = new();

        /// <summary>
        /// Additional metadata about the project generation
        /// </summary>
        public SqlProjectMetadata Metadata { get; set; } = new();

        /// <summary>
        /// Gets the total number of files in the project
        /// </summary>
        public int TotalFileCount => 
            TableScripts.Count + 
            IndexScripts.Count + 
            StoredProcedureScripts.Count + 
            DeploymentScripts.Count + 
            DataMigrationScripts.Count + 
            1; // +1 for the .sqlproj file

        /// <summary>
        /// Gets a summary of the project contents
        /// </summary>
        public string GetProjectSummary()
        {
            return $"SQL Database Project '{ProjectName}' created with {TotalFileCount} files:\n" +
                   $"- Tables: {TableScripts.Count}\n" +
                   $"- Indexes: {IndexScripts.Count}\n" +
                   $"- Stored Procedures: {StoredProcedureScripts.Count}\n" +
                   $"- Deployment Scripts: {DeploymentScripts.Count}\n" +
                   $"- Data Migration Scripts: {DataMigrationScripts.Count}\n" +
                   $"- Project File: {ProjectFilePath}";
        }
    }

    /// <summary>
    /// Additional metadata for SQL Database Project
    /// </summary>
    public class SqlProjectMetadata
    {
        /// <summary>
        /// Version of the tool that generated this project
        /// </summary>
        public string GeneratorVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Target SQL Server version/compatibility level
        /// </summary>
        public string TargetSqlVersion { get; set; } = "Azure SQL Database";

        /// <summary>
        /// List of any warnings or notes generated during project creation
        /// </summary>
        public List<string> GenerationWarnings { get; set; } = new();

        /// <summary>
        /// List of manual intervention points that require user attention
        /// </summary>
        public List<string> ManualInterventionRequired { get; set; } = new();

        /// <summary>
        /// Estimated complexity level of the migration
        /// </summary>
        public string ComplexityLevel { get; set; } = "Medium";

        /// <summary>
        /// Deployment configuration options
        /// </summary>
        public DeploymentOptions DeploymentOptions { get; set; } = new();
    }

    /// <summary>
    /// Configuration options for database deployment
    /// </summary>
    public class DeploymentOptions
    {
        /// <summary>
        /// Whether to drop objects not in the project
        /// </summary>
        public bool DropObjectsNotInSource { get; set; } = false;

        /// <summary>
        /// Whether to backup database before deployment
        /// </summary>
        public bool BackupDatabaseBeforeChanges { get; set; } = true;

        /// <summary>
        /// Whether to block incremental deployment if data loss might occur
        /// </summary>
        public bool BlockOnPossibleDataLoss { get; set; } = true;

        /// <summary>
        /// Whether to ignore whitespace differences
        /// </summary>
        public bool IgnoreWhitespace { get; set; } = true;

        /// <summary>
        /// Whether to ignore column collation differences
        /// </summary>
        public bool IgnoreColumnCollation { get; set; } = false;

        /// <summary>
        /// Whether to verify deployment scripts
        /// </summary>
        public bool VerifyDeployment { get; set; } = true;

        /// <summary>
        /// Command timeout for deployment operations (in seconds)
        /// </summary>
        public int CommandTimeout { get; set; } = 60;

        /// <summary>
        /// Whether to include composite objects in deployment
        /// </summary>
        public bool IncludeCompositeObjects { get; set; } = true;

        /// <summary>
        /// Whether to ignore index options differences
        /// </summary>
        public bool IgnoreIndexOptions { get; set; } = false;

        /// <summary>
        /// Whether to ignore login security identifiers
        /// </summary>
        public bool IgnoreLoginSids { get; set; } = true;

        /// <summary>
        /// Whether to ignore permissions differences
        /// </summary>
        public bool IgnorePermissions { get; set; } = false;

        /// <summary>
        /// Whether to ignore role membership differences
        /// </summary>
        public bool IgnoreRoleMembership { get; set; } = false;
    }
}
