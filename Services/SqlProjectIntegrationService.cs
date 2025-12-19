using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.SqlProject;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for integrating SQL Database Project generation with the main application workflow
    /// Provides orchestration between migration assessment and SQL project creation
    /// </summary>
    public class SqlProjectIntegrationService
    {
        private readonly SqlDatabaseProjectService _sqlProjectService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SqlProjectIntegrationService> _logger;

        public SqlProjectIntegrationService(
            SqlDatabaseProjectService sqlProjectService,
            IConfiguration configuration,
            ILogger<SqlProjectIntegrationService> logger)
        {
            _sqlProjectService = sqlProjectService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Generates SQL Database Project as part of the migration assessment workflow
        /// </summary>
        public async Task<SqlProjectGenerationResult> GenerateSqlProjectAsync(
            SqlMigrationAssessment assessment,
            SqlProjectOptions options,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting SQL Database Project generation integration");

            var result = new SqlProjectGenerationResult
            {
                StartTime = DateTime.UtcNow,
                Options = options
            };

            try
            {
                // Validate assessment
                ValidateAssessment(assessment);

                // Determine project name and output path
                var projectName = DetermineProjectName(assessment, options);
                var outputPath = DetermineOutputPath(options);

                _logger.LogInformation("Generating SQL project: {ProjectName} at {OutputPath}", projectName, outputPath);

                // Generate the SQL Database Project
                var sqlProject = await _sqlProjectService.GenerateProjectAsync(
                    assessment, 
                    projectName, 
                    outputPath, 
                    cancellationToken);

                // Post-process the project
                await PostProcessProjectAsync(sqlProject, options, cancellationToken);

                // Generate deployment artifacts
                if (options.GenerateDeploymentArtifacts)
                {
                    await GenerateDeploymentArtifactsAsync(sqlProject, options, cancellationToken);
                }

                // Generate documentation
                if (options.GenerateDocumentation)
                {
                    await GenerateProjectDocumentationAsync(sqlProject, options, cancellationToken);
                }

                result.Project = sqlProject;
                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogInformation("SQL Database Project generation completed successfully in {Duration}ms", 
                    result.Duration.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogError(ex, "Error during SQL Database Project generation");
                throw;
            }
        }

        /// <summary>
        /// Validates that the assessment contains sufficient data for SQL project generation
        /// </summary>
        private void ValidateAssessment(SqlMigrationAssessment assessment)
        {
            if (assessment == null)
                throw new ArgumentNullException(nameof(assessment));

            if (!assessment.DatabaseMappings.Any())
                throw new InvalidOperationException("Assessment must contain at least one database mapping");

            if (!assessment.DatabaseMappings.Any(dm => dm.ContainerMappings.Any()))
                throw new InvalidOperationException("Assessment must contain at least one container mapping");

            _logger.LogDebug("Assessment validation passed");
        }

        /// <summary>
        /// Determines the project name based on assessment and options
        /// </summary>
        private string DetermineProjectName(SqlMigrationAssessment assessment, SqlProjectOptions options)
        {
            if (!string.IsNullOrEmpty(options.ProjectName))
                return SanitizeProjectName(options.ProjectName);

            // Generate name from source database if available
            var firstDatabase = assessment.DatabaseMappings.FirstOrDefault();
            if (firstDatabase != null && !string.IsNullOrEmpty(firstDatabase.SourceDatabase))
            {
                return SanitizeProjectName($"{firstDatabase.SourceDatabase}_Migration");
            }

            // Generate timestamp-based name as fallback
            return $"CosmosToSql_Migration_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        }

        /// <summary>
        /// Sanitizes project name to be valid for file system and SQL Server
        /// </summary>
        private string SanitizeProjectName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "SqlDatabaseProject";

            // Remove invalid characters
            var invalidChars = Path.GetInvalidFileNameChars().Concat(new char[] { ' ', '-', '.', '(', ')', '[', ']' }).ToArray();
            var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            // Ensure it starts with a letter
            if (!char.IsLetter(sanitized.FirstOrDefault()))
                sanitized = "Db_" + sanitized;

            return sanitized;
        }

        /// <summary>
        /// Determines the output path for the SQL project
        /// </summary>
        private string DetermineOutputPath(SqlProjectOptions options)
        {
            if (!string.IsNullOrEmpty(options.OutputPath))
            {
                return Path.GetFullPath(options.OutputPath);
            }

            // Use configured output path or current directory
            var configuredPath = _configuration["SqlProject:OutputPath"];
            if (!string.IsNullOrEmpty(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            // Default to SqlProject subdirectory in current directory
            return Path.Combine(Directory.GetCurrentDirectory(), "Generated_SqlProject");
        }

        /// <summary>
        /// Performs post-processing on the generated SQL project
        /// </summary>
        private async Task PostProcessProjectAsync(
            SqlDatabaseProject sqlProject, 
            SqlProjectOptions options, 
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Post-processing SQL project: {ProjectName}", sqlProject.ProjectName);

            // Add custom metadata
            sqlProject.Metadata.GeneratorVersion = GetGeneratorVersion();
            sqlProject.Metadata.ComplexityLevel = DetermineComplexityLevel(sqlProject.Assessment);

            // Add generation warnings for complex transformations
            AddGenerationWarnings(sqlProject);

            // Add manual intervention notes
            AddManualInterventionNotes(sqlProject);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Generates deployment artifacts (publish profiles, dacpac configurations, etc.)
        /// </summary>
        private async Task GenerateDeploymentArtifactsAsync(
            SqlDatabaseProject sqlProject, 
            SqlProjectOptions options, 
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating deployment artifacts for: {ProjectName}", sqlProject.ProjectName);

            // Generate publish profile for Azure SQL
            await GenerateAzureSqlPublishProfileAsync(sqlProject, cancellationToken);

            // Generate Azure DevOps pipeline template
            if (options.GenerateAzureDevOpsPipeline)
            {
                await GenerateAzureDevOpsPipelineAsync(sqlProject, cancellationToken);
            }

            // Generate PowerShell deployment script
            if (options.GeneratePowerShellDeployment)
            {
                await GeneratePowerShellDeploymentAsync(sqlProject, cancellationToken);
            }
        }

        /// <summary>
        /// Generates project documentation
        /// </summary>
        private async Task GenerateProjectDocumentationAsync(
            SqlDatabaseProject sqlProject, 
            SqlProjectOptions options, 
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating documentation for: {ProjectName}", sqlProject.ProjectName);

            // Generate README.md
            var readmeContent = GenerateReadmeContent(sqlProject);
            var readmePath = Path.Combine(sqlProject.OutputPath, "README.md");
            await File.WriteAllTextAsync(readmePath, readmeContent, cancellationToken);

            // Generate deployment guide
            var deploymentGuide = GenerateDeploymentGuide(sqlProject);
            var guidePath = Path.Combine(sqlProject.OutputPath, "DeploymentGuide.md");
            await File.WriteAllTextAsync(guidePath, deploymentGuide, cancellationToken);

            // Generate schema documentation
            var schemaDoc = GenerateSchemaDocumentation(sqlProject);
            var schemaPath = Path.Combine(sqlProject.OutputPath, "SchemaDocumentation.md");
            await File.WriteAllTextAsync(schemaPath, schemaDoc, cancellationToken);
        }

        /// <summary>
        /// Generates Azure SQL publish profile
        /// </summary>
        private async Task GenerateAzureSqlPublishProfileAsync(SqlDatabaseProject sqlProject, CancellationToken cancellationToken)
        {
            var publishProfile = GeneratePublishProfileContent(sqlProject);
            var profilePath = Path.Combine(sqlProject.OutputPath, $"{sqlProject.ProjectName}.publish.xml");
            await File.WriteAllTextAsync(profilePath, publishProfile, cancellationToken);
        }

        /// <summary>
        /// Generates Azure DevOps pipeline YAML
        /// </summary>
        private async Task GenerateAzureDevOpsPipelineAsync(SqlDatabaseProject sqlProject, CancellationToken cancellationToken)
        {
            var pipelineContent = GenerateAzureDevOpsPipelineContent(sqlProject);
            var pipelinePath = Path.Combine(sqlProject.OutputPath, "azure-pipelines.yml");
            await File.WriteAllTextAsync(pipelinePath, pipelineContent, cancellationToken);
        }

        /// <summary>
        /// Generates PowerShell deployment script
        /// </summary>
        private async Task GeneratePowerShellDeploymentAsync(SqlDatabaseProject sqlProject, CancellationToken cancellationToken)
        {
            var scriptContent = GeneratePowerShellDeploymentContent(sqlProject);
            var scriptPath = Path.Combine(sqlProject.OutputPath, "Deploy.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);
        }

        #region Helper Methods

        private string GetGeneratorVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        }

        private string DetermineComplexityLevel(SqlMigrationAssessment assessment)
        {
            var totalTransformations = assessment.TransformationRules.Count;
            var complexTransformations = assessment.TransformationRules.Count(tr => 
                tr.TransformationType.ToLower() == "split" || tr.TransformationType.ToLower() == "combine");

            if (totalTransformations > 20 || complexTransformations > 5)
                return "High";
            else if (totalTransformations > 10 || complexTransformations > 2)
                return "Medium";
            else
                return "Low";
        }

        private void AddGenerationWarnings(SqlDatabaseProject sqlProject)
        {
            // Check for complex transformations
            var complexTransformations = sqlProject.Assessment.TransformationRules
                .Where(tr => tr.TransformationType.ToLower() == "split" || tr.TransformationType.ToLower() == "combine")
                .ToList();

            if (complexTransformations.Any())
            {
                sqlProject.Metadata.GenerationWarnings.Add(
                    $"Found {complexTransformations.Count} complex transformations that may require custom logic implementation");
            }

            // Check for large tables with detailed warnings based on thresholds
            var allContainerMappings = sqlProject.Assessment.DatabaseMappings
                .SelectMany(dm => dm.ContainerMappings)
                .ToList();

            // Define warning thresholds
            const long WarningThreshold = 1_000_000;        // 1M rows - Yellow warning
            const long HighPriorityThreshold = 10_000_000;  // 10M rows - Orange warning
            const long CriticalThreshold = 100_000_000;     // 100M rows - Red warning

            var warningTables = allContainerMappings.Where(cm => cm.EstimatedRowCount >= WarningThreshold).ToList();
            var highPriorityTables = allContainerMappings.Where(cm => cm.EstimatedRowCount >= HighPriorityThreshold).ToList();
            var criticalTables = allContainerMappings.Where(cm => cm.EstimatedRowCount >= CriticalThreshold).ToList();

            // Add warnings for large tables
            if (criticalTables.Any())
            {
                var criticalTableNames = string.Join(", ", criticalTables.Select(t => $"{t.TargetTable} ({t.EstimatedRowCount:N0} rows)"));
                sqlProject.Metadata.GenerationWarnings.Add(
                    $"🔴 CRITICAL: Found {criticalTables.Count} very large table(s) with >100M rows: {criticalTableNames}. " +
                    "Carefully review partitioning strategies, indexing plans, and migration approach.");
            }

            if (highPriorityTables.Any())
            {
                var highPriorityTableNames = string.Join(", ", highPriorityTables.Select(t => $"{t.TargetTable} ({t.EstimatedRowCount:N0} rows)"));
                sqlProject.Metadata.GenerationWarnings.Add(
                    $"🟠 HIGH PRIORITY: Found {highPriorityTables.Count} large table(s) with >10M rows: {highPriorityTableNames}. " +
                    "Review partitioning strategies and consider incremental migration.");
            }

            if (warningTables.Any())
            {
                sqlProject.Metadata.GenerationWarnings.Add(
                    $"🟡 WARNING: Found {warningTables.Count} table(s) with >1M rows. Monitor migration performance and consider batch processing.");
            }

            // Check for large data sizes (note: we'd need to calculate total size, but DocumentCount * avg doc size can approximate)
            // For now, just warn about total container count if none of the row count warnings triggered
            if (!warningTables.Any() && allContainerMappings.Count > 5)
            {
                sqlProject.Metadata.GenerationWarnings.Add(
                    $"Found {allContainerMappings.Count} containers. Consider reviewing table sizes and partitioning strategies");
            }
        }

        private void AddManualInterventionNotes(SqlDatabaseProject sqlProject)
        {
            // Add notes for stored procedures that need implementation
            if (sqlProject.StoredProcedureScripts.Any())
            {
                sqlProject.Metadata.ManualInterventionRequired.Add(
                    "Review and implement transformation logic in generated stored procedures");
            }

            // Add notes for index optimization
            if (sqlProject.IndexScripts.Any())
            {
                sqlProject.Metadata.ManualInterventionRequired.Add(
                    "Review index recommendations and adjust based on actual query patterns");
            }

            // Add notes for data validation
            sqlProject.Metadata.ManualInterventionRequired.Add(
                "Execute data validation scripts after migration to ensure data integrity");
        }

        private string GenerateReadmeContent(SqlDatabaseProject sqlProject)
        {
            return $@"# {sqlProject.ProjectName}

## Overview
This SQL Database Project was generated from Cosmos DB migration assessment on {sqlProject.CreatedDate:yyyy-MM-dd HH:mm:ss} UTC.

## Project Contents
{sqlProject.GetProjectSummary()}

## Deployment Instructions
1. Open the project in Visual Studio or Azure Data Studio
2. Configure connection string in the publish profile
3. Build the project to generate DACPAC
4. Deploy to Azure SQL Database

## Important Notes
### Manual Intervention Required:
{string.Join("\n", sqlProject.Metadata.ManualInterventionRequired.Select(note => $"- {note}"))}

### Warnings:
{string.Join("\n", sqlProject.Metadata.GenerationWarnings.Select(warning => $"- {warning}"))}

## Generated by
Cosmos DB to SQL Migration Tool v{sqlProject.Metadata.GeneratorVersion}
Complexity Level: {sqlProject.Metadata.ComplexityLevel}
";
        }

        private string GenerateDeploymentGuide(SqlDatabaseProject sqlProject)
        {
            return $@"# Deployment Guide for {sqlProject.ProjectName}

## Prerequisites
- Visual Studio 2019+ with SQL Server Data Tools (SSDT)
- Azure SQL Database instance
- Appropriate permissions on target database

## Deployment Steps

### 1. Review Generated Scripts
- Examine table definitions in Tables/ folder
- Review index recommendations in Indexes/ folder
- Check stored procedures in StoredProcedures/ folder

### 2. Configure Connection
- Update the publish profile with your Azure SQL connection details
- Test connection before deployment

### 3. Deploy Schema
- Build the SQL project to generate DACPAC
- Use SqlPackage.exe or Visual Studio to deploy
- Monitor deployment for errors

### 4. Post-Deployment
- Execute data validation scripts
- Run performance tests
- Monitor index usage

## Troubleshooting
- Check deployment logs for detailed error messages
- Verify Azure SQL compatibility levels
- Ensure sufficient permissions on target database
";
        }

        private string GenerateSchemaDocumentation(SqlDatabaseProject sqlProject)
        {
            var content = $@"# Schema Documentation for {sqlProject.ProjectName}

## Database Mappings
";
            foreach (var dbMapping in sqlProject.Assessment.DatabaseMappings)
            {
                content += $@"
### Database: {dbMapping.SourceDatabase} → {dbMapping.TargetDatabase}

| Source Container | Target Table | Schema | Estimated Rows |
|-----------------|-------------|--------|----------------|
";
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    content += $"| {containerMapping.SourceContainer} | {containerMapping.TargetTable} | {containerMapping.TargetSchema} | N/A |\n";
                }
            }

            content += $@"
## Index Recommendations
| Index Name | Table | Type | Columns | Priority |
|------------|-------|------|---------|----------|
";
            foreach (var indexRec in sqlProject.Assessment.IndexRecommendations)
            {
                var columns = string.Join(", ", indexRec.Columns);
                content += $"| {indexRec.IndexName} | {indexRec.TableName} | {indexRec.IndexType} | {columns} | {indexRec.Priority} |\n";
            }

            return content;
        }

        private string GeneratePublishProfileContent(SqlDatabaseProject sqlProject)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""14.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <IncludeCompositeObjects>True</IncludeCompositeObjects>
    <TargetDatabaseName>{sqlProject.ProjectName}</TargetDatabaseName>
    <ProfileVersionNumber>1</ProfileVersionNumber>
    <BlockOnPossibleDataLoss>{sqlProject.Metadata.DeploymentOptions.BlockOnPossibleDataLoss}</BlockOnPossibleDataLoss>
    <BackupDatabaseBeforeChanges>{sqlProject.Metadata.DeploymentOptions.BackupDatabaseBeforeChanges}</BackupDatabaseBeforeChanges>
    <DropObjectsNotInSource>{sqlProject.Metadata.DeploymentOptions.DropObjectsNotInSource}</DropObjectsNotInSource>
    <DoNotDropObjectTypes />
    <IgnoreWhitespace>{sqlProject.Metadata.DeploymentOptions.IgnoreWhitespace}</IgnoreWhitespace>
    <IgnoreColumnCollation>{sqlProject.Metadata.DeploymentOptions.IgnoreColumnCollation}</IgnoreColumnCollation>
    <VerifyDeployment>{sqlProject.Metadata.DeploymentOptions.VerifyDeployment}</VerifyDeployment>
    <CommandTimeout>{sqlProject.Metadata.DeploymentOptions.CommandTimeout}</CommandTimeout>
    <TargetConnectionString>Server=tcp:your-server.database.windows.net,1433;Initial Catalog={sqlProject.ProjectName};Persist Security Info=False;User ID=your-username;Password=your-password;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;</TargetConnectionString>
  </PropertyGroup>
</Project>";
        }

        private string GenerateAzureDevOpsPipelineContent(SqlDatabaseProject sqlProject)
        {
            return $@"# Azure DevOps Pipeline for {sqlProject.ProjectName}
trigger:
  branches:
    include:
    - main
    - develop

pool:
  vmImage: 'windows-latest'

variables:
  buildConfiguration: 'Release'
  sqlProjectPath: '{sqlProject.ProjectName}.sqlproj'
  dacpacPath: '$(Build.ArtifactStagingDirectory)/{sqlProject.ProjectName}.dacpac'

stages:
- stage: Build
  displayName: 'Build SQL Project'
  jobs:
  - job: BuildJob
    displayName: 'Build DACPAC'
    steps:
    - task: MSBuild@1
      displayName: 'Build SQL Project'
      inputs:
        solution: '$(sqlProjectPath)'
        configuration: '$(buildConfiguration)'
        msbuildArguments: '/p:OutputPath=""$(Build.ArtifactStagingDirectory)""'

    - task: PublishBuildArtifacts@1
      displayName: 'Publish DACPAC Artifact'
      inputs:
        pathToPublish: '$(dacpacPath)'
        artifactName: 'dacpac'

- stage: Deploy
  displayName: 'Deploy to Azure SQL'
  dependsOn: Build
  condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
  jobs:
  - deployment: DeployToAzureSQL
    displayName: 'Deploy DACPAC to Azure SQL'
    environment: 'production'
    strategy:
      runOnce:
        deploy:
          steps:
          - task: SqlAzureDacpacDeployment@1
            displayName: 'Deploy to Azure SQL Database'
            inputs:
              azureSubscription: '$(AzureServiceConnection)'
              ServerName: '$(SqlServerName)'
              DatabaseName: '$(SqlDatabaseName)'
              SqlUsername: '$(SqlUsername)'
              SqlPassword: '$(SqlPassword)'
              DacpacFile: '$(Pipeline.Workspace)/dacpac/{sqlProject.ProjectName}.dacpac'
              AdditionalArguments: '/p:BlockOnPossibleDataLoss=true'
";
        }

        private string GeneratePowerShellDeploymentContent(SqlDatabaseProject sqlProject)
        {
            return $@"# PowerShell Deployment Script for {sqlProject.ProjectName}
# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

param(
    [Parameter(Mandatory=$true)]
    [string]$ServerName,
    
    [Parameter(Mandatory=$true)]
    [string]$DatabaseName,
    
    [Parameter(Mandatory=$true)]
    [string]$Username,
    
    [Parameter(Mandatory=$true)]
    [string]$Password,
    
    [Parameter(Mandatory=$false)]
    [string]$DacpacPath = "".\\bin\\Release\\{sqlProject.ProjectName}.dacpac""
)

Write-Host ""Deploying {sqlProject.ProjectName} to Azure SQL Database"" -ForegroundColor Green

try {{
    # Import SqlServer module
    if (-not (Get-Module -ListAvailable -Name SqlServer)) {{
        Write-Host ""Installing SqlServer PowerShell module..."" -ForegroundColor Yellow
        Install-Module -Name SqlServer -Force -AllowClobber
    }}
    Import-Module SqlServer

    # Build connection string
    $connectionString = ""Server=tcp:${{ServerName}},1433;Initial Catalog=${{DatabaseName}};Persist Security Info=False;User ID=${{Username}};Password=${{Password}};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;""

    # Deploy DACPAC
    Write-Host ""Deploying DACPAC: $DacpacPath"" -ForegroundColor Yellow
    
    $publishProfile = @{{
        'TargetConnectionString' = $connectionString
        'BlockOnPossibleDataLoss' = $true
        'BackupDatabaseBeforeChanges' = $true
        'DropObjectsNotInSource' = $false
    }}

    Publish-DacPackage -DacpacPath $DacpacPath -PublishProfile $publishProfile

    Write-Host ""Deployment completed successfully!"" -ForegroundColor Green
}}
catch {{
    Write-Error ""Deployment failed: $($_.Exception.Message)""
    exit 1
}}
";
        }

        #endregion
    }
}
