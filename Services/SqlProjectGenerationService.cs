using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;
using System.Text;
using System.Xml.Linq;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for generating SQL Server Database Projects (.sqlproj) from migration assessments
    /// Creates SSDT-compatible projects for cloud-native Azure SQL Database deployment
    /// </summary>
    public class SqlProjectGenerationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SqlProjectGenerationService> _logger;

        public SqlProjectGenerationService(IConfiguration configuration, ILogger<SqlProjectGenerationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Generates SQL Database projects for all databases in the assessment
        /// </summary>
        public async Task GenerateSqlProjectsAsync(AssessmentResult assessment, string outputDirectory, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting SQL project generation for assessment {AssessmentId}", assessment.AssessmentId);

            var sqlProjectsDirectory = Path.Combine(outputDirectory, "sql-projects");
            Directory.CreateDirectory(sqlProjectsDirectory);

            try
            {
                foreach (var databaseMapping in assessment.SqlAssessment.DatabaseMappings)
                {
                    await GenerateDatabaseProjectAsync(databaseMapping, assessment, sqlProjectsDirectory, cancellationToken);
                }

                _logger.LogInformation("Successfully generated {DatabaseCount} SQL database projects in {OutputDirectory}", 
                    assessment.SqlAssessment.DatabaseMappings.Count, sqlProjectsDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating SQL projects");
                throw;
            }
        }

        /// <summary>
        /// Generates a single SQL Database project for the specified database mapping
        /// </summary>
        private async Task GenerateDatabaseProjectAsync(DatabaseMapping databaseMapping, AssessmentResult assessment, 
            string baseOutputDirectory, CancellationToken cancellationToken = default)
        {
            var projectName = $"{SanitizeName(databaseMapping.TargetDatabase)}.Database";
            var projectDirectory = Path.Combine(baseOutputDirectory, projectName);
            
            _logger.LogInformation("Generating SQL project {ProjectName} in {ProjectDirectory}", projectName, projectDirectory);

            // Create project directory structure
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Tables"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Indexes"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "ForeignKeys"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Scripts"));

            // Generate project file
            await GenerateProjectFileAsync(projectName, projectDirectory, databaseMapping, cancellationToken);

            // Generate table scripts
            await GenerateTableScriptsAsync(projectDirectory, databaseMapping, assessment, cancellationToken);

            // Generate index scripts
            await GenerateIndexScriptsAsync(projectDirectory, databaseMapping, assessment, cancellationToken);

            // Generate foreign key scripts
            await GenerateForeignKeyScriptsAsync(projectDirectory, databaseMapping, assessment, cancellationToken);

            // Generate deployment script
            await GenerateDeploymentScriptAsync(projectDirectory, databaseMapping, assessment, cancellationToken);

            _logger.LogInformation("Successfully generated SQL project {ProjectName}", projectName);
        }

        /// <summary>
        /// Generates the .sqlproj project file compatible with SSDT and SqlPackage.exe
        /// </summary>
        private async Task GenerateProjectFileAsync(string projectName, string projectDirectory, 
            DatabaseMapping databaseMapping, CancellationToken cancellationToken = default)
        {
            var projectFile = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Project",
                    new XAttribute("DefaultTargets", "Build"),
                    new XAttribute("xmlns", "http://schemas.microsoft.com/developer/msbuild/2003"),
                    new XAttribute("ToolsVersion", "4.0"),

                    // Import Microsoft.Data.Tools.Schema.SqlTasks.targets
                    new XElement("Import",
                        new XAttribute("Project", @"$(MSBuildExtensionsPath)\Microsoft\VisualStudio\v$(VisualStudioVersion)\SSDT\Microsoft.Data.Tools.Schema.SqlTasks.targets")),

                    // Project configuration
                    new XElement("PropertyGroup",
                        new XElement("Configuration",
                            new XAttribute("Condition", " '$(Configuration)' == '' "), "Debug"),
                        new XElement("Platform",
                            new XAttribute("Condition", " '$(Platform)' == '' "), "AnyCPU"),
                        new XElement("Name", projectName),
                        new XElement("SchemaVersion", "2.0"),
                        new XElement("ProjectVersion", "4.1"),
                        new XElement("ProjectGuid", Guid.NewGuid().ToString("B").ToUpper()),
                        new XElement("DSP", "Microsoft.Data.Tools.Schema.Sql.SqlAzureV12DatabaseSchemaProvider"),
                        new XElement("OutputType", "Database"),
                        new XElement("RootPath", ""),
                        new XElement("RootNamespace", projectName),
                        new XElement("AssemblyName", projectName),
                        new XElement("ModelCollation", "1033, CI"),
                        new XElement("DefaultFileStructure", "BySchemaAndSchemaType"),
                        new XElement("DeployToDatabase", "True"),
                        new XElement("TargetFrameworkVersion", "v4.7.2"),
                        new XElement("TargetLanguage", "CS"),
                        new XElement("AppDesignerFolder", "Properties"),
                        new XElement("SqlServerVerification", "False"),
                        new XElement("IncludeCompositeObjects", "True"),
                        new XElement("TargetDatabaseSet", "True")
                    ),

                    // Debug configuration
                    new XElement("PropertyGroup",
                        new XAttribute("Condition", " '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "),
                        new XElement("OutputPath", "bin\\Debug\\"),
                        new XElement("BuildScriptName", $"{projectName}.sql"),
                        new XElement("TreatWarningsAsErrors", "false"),
                        new XElement("DebugSymbols", "true"),
                        new XElement("DebugType", "full"),
                        new XElement("Optimize", "false"),
                        new XElement("DefineDebug", "true"),
                        new XElement("DefineTrace", "true"),
                        new XElement("ErrorReport", "prompt"),
                        new XElement("WarningLevel", "4")
                    ),

                    // Release configuration
                    new XElement("PropertyGroup",
                        new XAttribute("Condition", " '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "),
                        new XElement("OutputPath", "bin\\Release\\"),
                        new XElement("BuildScriptName", $"{projectName}.sql"),
                        new XElement("TreatWarningsAsErrors", "false"),
                        new XElement("DebugType", "pdbonly"),
                        new XElement("Optimize", "true"),
                        new XElement("DefineDebug", "false"),
                        new XElement("DefineTrace", "true"),
                        new XElement("ErrorReport", "prompt"),
                        new XElement("WarningLevel", "4")
                    ),

                    // Reference to Microsoft.Data.Tools.Schema.Sql.UnitTesting
                    new XElement("PropertyGroup",
                        new XElement("SccProjectName", "SAK"),
                        new XElement("SccProvider", "SAK"),
                        new XElement("SccAuxPath", "SAK"),
                        new XElement("SccLocalPath", "SAK")
                    )
                )
            );

            // Add file references (will be populated as we create the files)
            var itemGroup = new XElement("ItemGroup");
            
            // Add table references
            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                var tableFileName = $"Tables\\{containerMapping.TargetTable}.sql";
                itemGroup.Add(new XElement("Build", new XAttribute("Include", tableFileName)));

                // Add child table references
                foreach (var childTableMapping in containerMapping.ChildTableMappings)
                {
                    var childTableFileName = $"Tables\\{childTableMapping.TargetTable}.sql";
                    itemGroup.Add(new XElement("Build", new XAttribute("Include", childTableFileName)));
                }
            }

            projectFile.Root?.Add(itemGroup);

            var projectFilePath = Path.Combine(projectDirectory, $"{projectName}.sqlproj");
            await File.WriteAllTextAsync(projectFilePath, projectFile.ToString(), cancellationToken);

            _logger.LogDebug("Generated project file: {ProjectFilePath}", projectFilePath);
        }

        /// <summary>
        /// Generates table creation scripts for main tables and child tables
        /// </summary>
        private async Task GenerateTableScriptsAsync(string projectDirectory, DatabaseMapping databaseMapping, 
            AssessmentResult assessment, CancellationToken cancellationToken = default)
        {
            var tablesDirectory = Path.Combine(projectDirectory, "Tables");

            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                // Generate main table script
                await GenerateMainTableScriptAsync(tablesDirectory, containerMapping, cancellationToken);

                // Generate child table scripts
                foreach (var childTableMapping in containerMapping.ChildTableMappings)
                {
                    await GenerateChildTableScriptAsync(tablesDirectory, childTableMapping, containerMapping, cancellationToken);
                }
            }

            _logger.LogDebug("Generated table scripts in {TablesDirectory}", tablesDirectory);
        }

        /// <summary>
        /// Generates the main table creation script
        /// </summary>
        private async Task GenerateMainTableScriptAsync(string tablesDirectory, ContainerMapping containerMapping, 
            CancellationToken cancellationToken = default)
        {
            var tableName = containerMapping.TargetTable;
            var schemaName = containerMapping.TargetSchema;
            
            var script = new StringBuilder();
            script.AppendLine($"-- Main table for Cosmos DB container: {containerMapping.SourceContainer}");
            script.AppendLine($"-- Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine();
            script.AppendLine($"CREATE TABLE [{schemaName}].[{tableName}] (");

            // Add primary key (always include an identity column for Azure SQL compatibility)
            script.AppendLine("    [Id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,");

            // Add Cosmos DB document id as a unique business key
            script.AppendLine("    [CosmosId] NVARCHAR(255) NOT NULL,");

            // Add field mappings
            foreach (var fieldMapping in containerMapping.FieldMappings.OrderBy(f => f.TargetColumn))
            {
                var nullability = fieldMapping.IsNullable ? "NULL" : "NOT NULL";
                var columnDefinition = $"    [{fieldMapping.TargetColumn}] {fieldMapping.TargetType} {nullability}";
                
                if (fieldMapping.IsPartitionKey)
                {
                    columnDefinition += " -- Cosmos DB Partition Key";
                }
                
                script.AppendLine($"{columnDefinition},");
            }

            // Add audit fields for cloud-native best practices
            script.AppendLine("    [CreatedDate] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),");
            script.AppendLine("    [ModifiedDate] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME())");

            script.AppendLine(");");
            script.AppendLine();

            // Add unique constraint on CosmosId
            script.AppendLine($"-- Unique constraint on Cosmos DB document ID");
            script.AppendLine($"ALTER TABLE [{schemaName}].[{tableName}] ADD CONSTRAINT [UK_{tableName}_CosmosId] UNIQUE ([CosmosId]);");
            script.AppendLine();

            // Add comments about transformations if any
            if (containerMapping.RequiredTransformations.Any())
            {
                script.AppendLine("-- Required Transformations:");
                foreach (var transformation in containerMapping.RequiredTransformations)
                {
                    script.AppendLine($"-- * {transformation}");
                }
                script.AppendLine();
            }

            var scriptPath = Path.Combine(tablesDirectory, $"{tableName}.sql");
            await File.WriteAllTextAsync(scriptPath, script.ToString(), cancellationToken);
        }

        /// <summary>
        /// Generates child table creation scripts for normalized arrays and nested objects
        /// </summary>
        private async Task GenerateChildTableScriptAsync(string tablesDirectory, ChildTableMapping childTableMapping, 
            ContainerMapping parentMapping, CancellationToken cancellationToken = default)
        {
            var tableName = childTableMapping.TargetTable;
            var schemaName = childTableMapping.TargetSchema;
            
            var script = new StringBuilder();
            script.AppendLine($"-- Child table for {childTableMapping.ChildTableType}: {childTableMapping.SourceFieldPath}");
            script.AppendLine($"-- Parent container: {parentMapping.SourceContainer}");
            script.AppendLine($"-- Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine();
            script.AppendLine($"CREATE TABLE [{schemaName}].[{tableName}] (");

            // Add field mappings (includes auto-generated PK and FK)
            foreach (var fieldMapping in childTableMapping.FieldMappings.OrderBy(f => f.TargetColumn))
            {
                var nullability = fieldMapping.IsNullable ? "NULL" : "NOT NULL";
                var columnDefinition = $"    [{fieldMapping.TargetColumn}] {fieldMapping.TargetType} {nullability}";
                
                if (fieldMapping.TargetColumn.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    columnDefinition += " PRIMARY KEY";
                }
                else if (fieldMapping.TargetColumn.Equals(childTableMapping.ParentKeyColumn, StringComparison.OrdinalIgnoreCase))
                {
                    columnDefinition += $" -- Foreign key to {parentMapping.TargetTable}";
                }
                
                script.AppendLine($"{columnDefinition},");
            }

            // Add audit fields
            script.AppendLine("    [CreatedDate] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),");
            script.AppendLine("    [ModifiedDate] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME())");

            script.AppendLine(");");
            script.AppendLine();

            // Add comments about transformations
            if (childTableMapping.RequiredTransformations.Any())
            {
                script.AppendLine("-- Required Transformations:");
                foreach (var transformation in childTableMapping.RequiredTransformations)
                {
                    script.AppendLine($"-- * {transformation}");
                }
                script.AppendLine();
            }

            var scriptPath = Path.Combine(tablesDirectory, $"{tableName}.sql");
            await File.WriteAllTextAsync(scriptPath, script.ToString(), cancellationToken);
        }

        /// <summary>
        /// Generates index creation scripts based on recommendations
        /// </summary>
        private async Task GenerateIndexScriptsAsync(string projectDirectory, DatabaseMapping databaseMapping, 
            AssessmentResult assessment, CancellationToken cancellationToken = default)
        {
            var indexesDirectory = Path.Combine(projectDirectory, "Indexes");
            var script = new StringBuilder();

            script.AppendLine("-- Index creation scripts based on Cosmos DB analysis");
            script.AppendLine($"-- Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine();

            var relevantIndexes = assessment.SqlAssessment.IndexRecommendations
                .Where(idx => databaseMapping.ContainerMappings.Any(cm => 
                    cm.TargetTable.Equals(idx.TableName, StringComparison.OrdinalIgnoreCase) ||
                    cm.ChildTableMappings.Any(ctm => ctm.TargetTable.Equals(idx.TableName, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(idx => idx.Priority)
                .ToList();

            foreach (var indexRecommendation in relevantIndexes)
            {
                script.AppendLine($"-- {indexRecommendation.Justification}");
                script.AppendLine($"-- Priority: {indexRecommendation.Priority}, Estimated Impact: {indexRecommendation.EstimatedImpactRUs} RUs");
                
                var indexScript = GenerateIndexScript(indexRecommendation);
                script.AppendLine(indexScript);
                script.AppendLine();
            }

            if (relevantIndexes.Any())
            {
                var indexScriptPath = Path.Combine(indexesDirectory, "Indexes.sql");
                await File.WriteAllTextAsync(indexScriptPath, script.ToString(), cancellationToken);
                _logger.LogDebug("Generated index script: {IndexScriptPath}", indexScriptPath);
            }
        }

        /// <summary>
        /// Generates foreign key constraint scripts
        /// </summary>
        private async Task GenerateForeignKeyScriptsAsync(string projectDirectory, DatabaseMapping databaseMapping, 
            AssessmentResult assessment, CancellationToken cancellationToken = default)
        {
            var foreignKeysDirectory = Path.Combine(projectDirectory, "ForeignKeys");
            var script = new StringBuilder();

            script.AppendLine("-- Foreign key constraint scripts");
            script.AppendLine($"-- Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine();

            // Generate foreign keys for child tables
            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                foreach (var childTableMapping in containerMapping.ChildTableMappings)
                {
                    var parentKeyMapping = childTableMapping.FieldMappings
                        .FirstOrDefault(fm => fm.TargetColumn.Equals(childTableMapping.ParentKeyColumn, StringComparison.OrdinalIgnoreCase));

                    if (parentKeyMapping != null)
                    {
                        var fkName = $"FK_{childTableMapping.TargetTable}_{containerMapping.TargetTable}";
                        script.AppendLine($"-- Foreign key relationship: {childTableMapping.TargetTable} -> {containerMapping.TargetTable}");
                        script.AppendLine($"ALTER TABLE [{childTableMapping.TargetSchema}].[{childTableMapping.TargetTable}]");
                        script.AppendLine($"ADD CONSTRAINT [{fkName}]");
                        script.AppendLine($"FOREIGN KEY ([{childTableMapping.ParentKeyColumn}])");
                        script.AppendLine($"REFERENCES [{containerMapping.TargetSchema}].[{containerMapping.TargetTable}] ([CosmosId])");
                        script.AppendLine("ON DELETE CASCADE ON UPDATE CASCADE;");
                        script.AppendLine();
                    }
                }
            }

            // Add assessment-recommended foreign keys
            var relevantForeignKeys = assessment.SqlAssessment.ForeignKeyConstraints
                .Where(fk => databaseMapping.ContainerMappings.Any(cm => 
                    cm.TargetTable.Equals(fk.ChildTable, StringComparison.OrdinalIgnoreCase) ||
                    cm.TargetTable.Equals(fk.ParentTable, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var foreignKey in relevantForeignKeys)
            {
                script.AppendLine($"-- {foreignKey.Justification}");
                script.AppendLine($"ALTER TABLE [{foreignKey.ChildTable}]");
                script.AppendLine($"ADD CONSTRAINT [{foreignKey.ConstraintName}]");
                script.AppendLine($"FOREIGN KEY ([{foreignKey.ChildColumn}])");
                script.AppendLine($"REFERENCES [{foreignKey.ParentTable}] ([{foreignKey.ParentColumn}])");
                script.AppendLine($"ON DELETE {foreignKey.OnDeleteAction} ON UPDATE {foreignKey.OnUpdateAction};");
                script.AppendLine();
            }

            if (script.Length > 200) // Only create file if there's meaningful content
            {
                var foreignKeyScriptPath = Path.Combine(foreignKeysDirectory, "ForeignKeys.sql");
                await File.WriteAllTextAsync(foreignKeyScriptPath, script.ToString(), cancellationToken);
                _logger.LogDebug("Generated foreign key script: {ForeignKeyScriptPath}", foreignKeyScriptPath);
            }
        }

        /// <summary>
        /// Generates database deployment script
        /// </summary>
        private async Task GenerateDeploymentScriptAsync(string projectDirectory, DatabaseMapping databaseMapping, 
            AssessmentResult assessment, CancellationToken cancellationToken = default)
        {
            var scriptsDirectory = Path.Combine(projectDirectory, "Scripts");
            var script = new StringBuilder();

            script.AppendLine("-- Database deployment script");
            script.AppendLine($"-- Target Database: {databaseMapping.TargetDatabase}");
            script.AppendLine($"-- Source: Cosmos DB Database '{databaseMapping.SourceDatabase}'");
            script.AppendLine($"-- Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine();

            script.AppendLine("-- This script should be executed after the database project is deployed");
            script.AppendLine("-- to set up any post-deployment configurations");
            script.AppendLine();

            script.AppendLine("-- Enable Query Store for performance monitoring (Azure SQL Database best practice)");
            script.AppendLine("ALTER DATABASE CURRENT SET QUERY_STORE = ON;");
            script.AppendLine("ALTER DATABASE CURRENT SET QUERY_STORE (OPERATION_MODE = READ_WRITE);");
            script.AppendLine();

            script.AppendLine("-- Set database collation for Azure SQL Database");
            script.AppendLine("-- Note: This can only be set during database creation in Azure SQL Database");
            script.AppendLine("-- ALTER DATABASE CURRENT COLLATE SQL_Latin1_General_CP1_CI_AS;");
            script.AppendLine();

            script.AppendLine("-- Database-level settings for optimal performance");
            script.AppendLine("ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS_ASYNC ON;");
            script.AppendLine("ALTER DATABASE CURRENT SET PARAMETERIZATION FORCED;");
            script.AppendLine();

            // Add deployment notes
            script.AppendLine("/*");
            script.AppendLine("DEPLOYMENT NOTES:");
            script.AppendLine($"- Source Cosmos DB: {databaseMapping.SourceDatabase}");
            script.AppendLine($"- Target SQL Database: {databaseMapping.TargetDatabase}");
            script.AppendLine($"- Container Count: {databaseMapping.ContainerMappings.Count}");
            script.AppendLine($"- Total Tables: {databaseMapping.ContainerMappings.Sum(cm => 1 + cm.ChildTableMappings.Count)}");
            script.AppendLine();
            script.AppendLine("NEXT STEPS:");
            script.AppendLine("1. Deploy this database project using SqlPackage.exe or SSDT");
            script.AppendLine("2. Execute this post-deployment script");
            script.AppendLine("3. Set up Azure Data Factory for data migration (separate feature)");
            script.AppendLine("4. Test the schema with sample data");
            script.AppendLine("5. Configure monitoring and alerting");
            script.AppendLine("*/");

            var deploymentScriptPath = Path.Combine(scriptsDirectory, "PostDeployment.sql");
            await File.WriteAllTextAsync(deploymentScriptPath, script.ToString(), cancellationToken);

            _logger.LogDebug("Generated deployment script: {DeploymentScriptPath}", deploymentScriptPath);
        }

        /// <summary>
        /// Generates an individual index creation script
        /// </summary>
        private string GenerateIndexScript(IndexRecommendation indexRecommendation)
        {
            var script = new StringBuilder();
            
            var indexTypeKeyword = indexRecommendation.IndexType.ToUpperInvariant() switch
            {
                "CLUSTERED" => "CLUSTERED",
                "UNIQUE" => "UNIQUE NONCLUSTERED",
                "COLUMNSTORE" => "CLUSTERED COLUMNSTORE",
                _ => "NONCLUSTERED"
            };

            script.Append($"CREATE {indexTypeKeyword} INDEX [{indexRecommendation.IndexName}] ON [{indexRecommendation.TableName}] (");
            script.Append(string.Join(", ", indexRecommendation.Columns.Select(c => $"[{c}]")));
            script.Append(")");

            if (indexRecommendation.IncludedColumns.Any())
            {
                script.Append(" INCLUDE (");
                script.Append(string.Join(", ", indexRecommendation.IncludedColumns.Select(c => $"[{c}]")));
                script.Append(")");
            }

            script.Append(";");

            return script.ToString();
        }

        /// <summary>
        /// Sanitizes names for use in file system and SQL identifiers
        /// </summary>
        private string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Database";

            // Remove invalid characters for file system and SQL identifiers
            var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '-', '.' }).ToArray();
            var sanitized = name;
            
            foreach (var invalidChar in invalidChars)
            {
                sanitized = sanitized.Replace(invalidChar, '_');
            }

            // Ensure it starts with a letter (SQL identifier requirement)
            if (!char.IsLetter(sanitized[0]) && sanitized[0] != '_')
            {
                sanitized = "DB_" + sanitized;
            }

            return sanitized;
        }
    }
}
