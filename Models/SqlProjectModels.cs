using System;
using System.Collections.Generic;
using CosmosToSqlAssessment.SqlProject;

namespace CosmosToSqlAssessment.Models
{
    /// <summary>
    /// Configuration options for SQL Database Project generation
    /// </summary>
    public class SqlProjectOptions
    {
        /// <summary>
        /// Name for the SQL Database Project (optional - will be auto-generated if not provided)
        /// </summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// Output directory path for the SQL project files
        /// </summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// Whether to generate deployment artifacts (publish profiles, scripts, etc.)
        /// </summary>
        public bool GenerateDeploymentArtifacts { get; set; } = true;

        /// <summary>
        /// Whether to generate project documentation (README, deployment guide, etc.)
        /// </summary>
        public bool GenerateDocumentation { get; set; } = true;

        /// <summary>
        /// Whether to generate Azure DevOps pipeline template
        /// </summary>
        public bool GenerateAzureDevOpsPipeline { get; set; } = true;

        /// <summary>
        /// Whether to generate PowerShell deployment script
        /// </summary>
        public bool GeneratePowerShellDeployment { get; set; } = true;

        /// <summary>
        /// Whether to overwrite existing files in the output directory
        /// </summary>
        public bool OverwriteExistingFiles { get; set; } = false;

        /// <summary>
        /// Target SQL Server compatibility level
        /// </summary>
        public string TargetCompatibilityLevel { get; set; } = "150"; // SQL Server 2019/Azure SQL DB

        /// <summary>
        /// Whether to include sample data scripts
        /// </summary>
        public bool IncludeSampleData { get; set; } = false;

        /// <summary>
        /// Additional custom deployment options
        /// </summary>
        public Dictionary<string, object> CustomOptions { get; set; } = new();

        /// <summary>
        /// Creates default options for SQL project generation
        /// </summary>
        public static SqlProjectOptions CreateDefault()
        {
            return new SqlProjectOptions
            {
                GenerateDeploymentArtifacts = true,
                GenerateDocumentation = true,
                GenerateAzureDevOpsPipeline = true,
                GeneratePowerShellDeployment = true,
                OverwriteExistingFiles = false,
                TargetCompatibilityLevel = "150"
            };
        }

        /// <summary>
        /// Creates minimal options for basic SQL project generation
        /// </summary>
        public static SqlProjectOptions CreateMinimal()
        {
            return new SqlProjectOptions
            {
                GenerateDeploymentArtifacts = false,
                GenerateDocumentation = false,
                GenerateAzureDevOpsPipeline = false,
                GeneratePowerShellDeployment = false,
                OverwriteExistingFiles = false,
                TargetCompatibilityLevel = "150"
            };
        }
    }

    /// <summary>
    /// Result of SQL Database Project generation operation
    /// </summary>
    public class SqlProjectGenerationResult
    {
        /// <summary>
        /// Whether the generation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if generation failed
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// Generated SQL Database Project (null if failed)
        /// </summary>
        public SqlDatabaseProject? Project { get; set; }

        /// <summary>
        /// Options used for generation
        /// </summary>
        public SqlProjectOptions Options { get; set; } = new();

        /// <summary>
        /// Start time of the generation process
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// End time of the generation process
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// Duration of the generation process
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// List of warnings generated during the process
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// List of informational messages
        /// </summary>
        public List<string> Messages { get; set; } = new();

        /// <summary>
        /// Statistics about the generation process
        /// </summary>
        public SqlProjectGenerationStats Stats { get; set; } = new();

        /// <summary>
        /// Gets a summary of the generation result
        /// </summary>
        public string GetSummary()
        {
            if (!Success)
                return $"SQL Project generation failed: {Error}";

            return $"SQL Project '{Project?.ProjectName}' generated successfully in {Duration.TotalSeconds:F2} seconds.\n" +
                   $"Files created: {Project?.TotalFileCount}\n" +
                   $"Output path: {Project?.OutputPath}";
        }
    }

    /// <summary>
    /// Statistics about SQL Database Project generation
    /// </summary>
    public class SqlProjectGenerationStats
    {
        /// <summary>
        /// Number of tables generated
        /// </summary>
        public int TablesGenerated { get; set; }

        /// <summary>
        /// Number of indexes generated
        /// </summary>
        public int IndexesGenerated { get; set; }

        /// <summary>
        /// Number of stored procedures generated
        /// </summary>
        public int StoredProceduresGenerated { get; set; }

        /// <summary>
        /// Number of deployment scripts generated
        /// </summary>
        public int DeploymentScriptsGenerated { get; set; }

        /// <summary>
        /// Total number of files generated
        /// </summary>
        public int TotalFilesGenerated { get; set; }

        /// <summary>
        /// Total size of generated files in bytes
        /// </summary>
        public long TotalFileSizeBytes { get; set; }

        /// <summary>
        /// Number of transformation rules processed
        /// </summary>
        public int TransformationRulesProcessed { get; set; }

        /// <summary>
        /// Number of complex transformations identified
        /// </summary>
        public int ComplexTransformations { get; set; }

        /// <summary>
        /// Gets formatted file size
        /// </summary>
        public string GetFormattedFileSize()
        {
            if (TotalFileSizeBytes < 1024)
                return $"{TotalFileSizeBytes} bytes";
            else if (TotalFileSizeBytes < 1024 * 1024)
                return $"{TotalFileSizeBytes / 1024:F1} KB";
            else
                return $"{TotalFileSizeBytes / (1024 * 1024):F1} MB";
        }
    }
}
