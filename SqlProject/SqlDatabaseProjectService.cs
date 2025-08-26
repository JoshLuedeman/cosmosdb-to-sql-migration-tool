using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.SqlProject
{
    /// <summary>
    /// Service for generating SQL Database Project files from migration assessment
    /// Creates a complete Visual Studio SQL Database Project (.sqlproj) for deployment
    /// </summary>
    public class SqlDatabaseProjectService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SqlDatabaseProjectService> _logger;

        public SqlDatabaseProjectService(IConfiguration configuration, ILogger<SqlDatabaseProjectService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Generates a complete SQL Database Project from assessment results
        /// </summary>
        public async Task<SqlDatabaseProject> GenerateProjectAsync(
            SqlMigrationAssessment assessment, 
            string projectName,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting SQL Database Project generation for project: {ProjectName}", projectName);

            var project = new SqlDatabaseProject
            {
                ProjectName = projectName,
                OutputPath = outputPath,
                CreatedDate = DateTime.UtcNow,
                Assessment = assessment
            };

            try
            {
                // Create project directory structure
                await CreateProjectStructureAsync(project, cancellationToken);

                // Generate .sqlproj file
                await GenerateProjectFileAsync(project, cancellationToken);

                // Generate table creation scripts
                await GenerateTableScriptsAsync(project, cancellationToken);

                // Generate index creation scripts
                await GenerateIndexScriptsAsync(project, cancellationToken);

                // Generate stored procedures for data migration
                await GenerateStoredProceduresAsync(project, cancellationToken);

                // Generate deployment scripts
                await GenerateDeploymentScriptsAsync(project, cancellationToken);

                // Generate data migration scripts
                await GenerateDataMigrationScriptsAsync(project, cancellationToken);

                // Generate post-deployment configuration scripts
                await GeneratePostDeploymentScriptsAsync(project, cancellationToken);

                _logger.LogInformation("SQL Database Project generation completed successfully: {ProjectPath}", project.ProjectFilePath);
                
                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating SQL Database Project: {ProjectName}", projectName);
                throw;
            }
        }

        /// <summary>
        /// Creates the standard SQL Database Project directory structure
        /// </summary>
        private async Task CreateProjectStructureAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Creating project directory structure for: {ProjectName}", project.ProjectName);

            var directories = new[]
            {
                project.OutputPath,
                Path.Combine(project.OutputPath, "Tables"),
                Path.Combine(project.OutputPath, "Indexes"),
                Path.Combine(project.OutputPath, "StoredProcedures"),
                Path.Combine(project.OutputPath, "Scripts"),
                Path.Combine(project.OutputPath, "Scripts\\PreDeployment"),
                Path.Combine(project.OutputPath, "Scripts\\PostDeployment"),
                Path.Combine(project.OutputPath, "Scripts\\DataMigration"),
                Path.Combine(project.OutputPath, "Security"),
                Path.Combine(project.OutputPath, "Security\\Schemas")
            };

            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
                _logger.LogDebug("Created directory: {DirectoryPath}", directory);
            }

            project.ProjectStructure = directories.ToList();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Generates the main .sqlproj project file
        /// </summary>
        private async Task GenerateProjectFileAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating .sqlproj file for: {ProjectName}", project.ProjectName);

            var projectContent = GenerateProjectFileContent(project);
            project.ProjectFilePath = Path.Combine(project.OutputPath, $"{project.ProjectName}.sqlproj");

            await File.WriteAllTextAsync(project.ProjectFilePath, projectContent, cancellationToken);
            
            _logger.LogDebug("Generated project file: {ProjectFilePath}", project.ProjectFilePath);
        }

        /// <summary>
        /// Generates table creation scripts from container mappings
        /// </summary>
        private async Task GenerateTableScriptsAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating table scripts for {TableCount} tables", 
                project.Assessment.DatabaseMappings.SelectMany(dm => dm.ContainerMappings).Count());

            var tableScripts = new List<string>();

            foreach (var dbMapping in project.Assessment.DatabaseMappings)
            {
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    var tableScript = GenerateTableScript(containerMapping);
                    var tablePath = Path.Combine(project.OutputPath, "Tables", $"{containerMapping.TargetTable}.sql");
                    
                    await File.WriteAllTextAsync(tablePath, tableScript, cancellationToken);
                    tableScripts.Add(tablePath);
                    
                    _logger.LogDebug("Generated table script: {TableName}", containerMapping.TargetTable);
                }
            }

            project.TableScripts = tableScripts;
        }

        /// <summary>
        /// Generates index creation scripts from recommendations
        /// </summary>
        private async Task GenerateIndexScriptsAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating index scripts for {IndexCount} indexes", 
                project.Assessment.IndexRecommendations.Count);

            var indexScripts = new List<string>();

            foreach (var indexRec in project.Assessment.IndexRecommendations)
            {
                var indexScript = GenerateIndexScript(indexRec);
                var indexPath = Path.Combine(project.OutputPath, "Indexes", $"{indexRec.IndexName}.sql");
                
                await File.WriteAllTextAsync(indexPath, indexScript, cancellationToken);
                indexScripts.Add(indexPath);
                
                _logger.LogDebug("Generated index script: {IndexName}", indexRec.IndexName);
            }

            project.IndexScripts = indexScripts;
        }

        /// <summary>
        /// Generates stored procedures for data migration operations
        /// </summary>
        private async Task GenerateStoredProceduresAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating stored procedures for data migration");

            var storedProcScripts = new List<string>();

            // Generate data migration stored procedures based on transformation rules
            foreach (var transformationRule in project.Assessment.TransformationRules)
            {
                var procScript = GenerateDataMigrationStoredProcedure(transformationRule);
                var procPath = Path.Combine(project.OutputPath, "StoredProcedures", $"sp_Migrate_{transformationRule.RuleName.Replace(" ", "")}.sql");
                
                await File.WriteAllTextAsync(procPath, procScript, cancellationToken);
                storedProcScripts.Add(procPath);
                
                _logger.LogDebug("Generated stored procedure: sp_Migrate_{RuleName}", transformationRule.RuleName);
            }

            project.StoredProcedureScripts = storedProcScripts;
        }

        /// <summary>
        /// Generates deployment scripts for Azure SQL deployment
        /// </summary>
        private async Task GenerateDeploymentScriptsAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating deployment scripts");

            // Generate pre-deployment script
            var preDeployScript = GeneratePreDeploymentScript(project);
            var preDeployPath = Path.Combine(project.OutputPath, "Scripts", "PreDeployment", "Script.PreDeployment.sql");
            await File.WriteAllTextAsync(preDeployPath, preDeployScript, cancellationToken);

            // Generate post-deployment script
            var postDeployScript = GeneratePostDeploymentScript(project);
            var postDeployPath = Path.Combine(project.OutputPath, "Scripts", "PostDeployment", "Script.PostDeployment.sql");
            await File.WriteAllTextAsync(postDeployPath, postDeployScript, cancellationToken);

            project.DeploymentScripts = new List<string> { preDeployPath, postDeployPath };
        }

        /// <summary>
        /// Generates data migration scripts for Azure Data Factory integration
        /// </summary>
        private async Task GenerateDataMigrationScriptsAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating data migration scripts");

            var migrationScripts = new List<string>();

            // Generate validation script
            var validationScript = GenerateDataValidationScript(project);
            var validationPath = Path.Combine(project.OutputPath, "Scripts", "DataMigration", "DataValidation.sql");
            await File.WriteAllTextAsync(validationPath, validationScript, cancellationToken);
            migrationScripts.Add(validationPath);

            // Generate cleanup script
            var cleanupScript = GenerateDataCleanupScript(project);
            var cleanupPath = Path.Combine(project.OutputPath, "Scripts", "DataMigration", "DataCleanup.sql");
            await File.WriteAllTextAsync(cleanupPath, cleanupScript, cancellationToken);
            migrationScripts.Add(cleanupPath);

            project.DataMigrationScripts = migrationScripts;
        }

        /// <summary>
        /// Generates post-deployment configuration scripts
        /// </summary>
        private async Task GeneratePostDeploymentScriptsAsync(SqlDatabaseProject project, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Generating post-deployment configuration scripts");

            // Generate security configuration
            var securityScript = GenerateSecurityConfigurationScript(project);
            var securityPath = Path.Combine(project.OutputPath, "Security", "DatabaseSecurity.sql");
            await File.WriteAllTextAsync(securityPath, securityScript, cancellationToken);

            // Generate schema creation script
            var schemaScript = GenerateSchemaCreationScript(project);
            var schemaPath = Path.Combine(project.OutputPath, "Security", "Schemas", "Schemas.sql");
            await File.WriteAllTextAsync(schemaPath, schemaScript, cancellationToken);
        }

        #region Script Generation Methods

        private string GenerateProjectFileContent(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" ToolsVersion=\"4.0\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine($"    <Name>{project.ProjectName}</Name>");
            sb.AppendLine("    <DSP>Microsoft.Data.Tools.Schema.Sql.SqlAzureV12DatabaseSchemaProvider</DSP>");
            sb.AppendLine("    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>");
            sb.AppendLine("    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>");
            sb.AppendLine("    <ProductVersion>10.0.30319</ProductVersion>");
            sb.AppendLine("    <SchemaVersion>2.0</SchemaVersion>");
            sb.AppendLine($"    <ProjectVersion>4.1</ProjectVersion>");
            sb.AppendLine($"    <ProjectGuid>{Guid.NewGuid()}</ProjectGuid>");
            sb.AppendLine("    <OutputType>Database</OutputType>");
            sb.AppendLine("    <RootPath>");
            sb.AppendLine("    </RootPath>");
            sb.AppendLine("    <RootNamespace></RootNamespace>");
            sb.AppendLine($"    <AssemblyName>{project.ProjectName}</AssemblyName>");
            sb.AppendLine("    <ModelCollation>1033, CI</ModelCollation>");
            sb.AppendLine("    <DefaultFileStructure>BySchemaAndSchemaType</DefaultFileStructure>");
            sb.AppendLine("    <DeployToDatabase>True</DeployToDatabase>");
            sb.AppendLine("    <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>");
            sb.AppendLine("    <TargetLanguage>CS</TargetLanguage>");
            sb.AppendLine("    <AppDesignerFolder>Properties</AppDesignerFolder>");
            sb.AppendLine("    <SqlServerVerification>False</SqlServerVerification>");
            sb.AppendLine("    <IncludeCompositeObjects>True</IncludeCompositeObjects>");
            sb.AppendLine("    <TargetDatabaseSet>True</TargetDatabaseSet>");
            sb.AppendLine("    <DefaultCollation>SQL_Latin1_General_CP1_CI_AS</DefaultCollation>");
            sb.AppendLine("    <AnsiNulls>False</AnsiNulls>");
            sb.AppendLine("    <QuotedIdentifier>False</QuotedIdentifier>");
            sb.AppendLine("    <QueryStoreCaptureMode>Auto</QueryStoreCaptureMode>");
            sb.AppendLine("    <QueryStoreDesiredState>On</QueryStoreDesiredState>");
            sb.AppendLine("    <QueryStoreFlushInterval>900</QueryStoreFlushInterval>");
            sb.AppendLine("    <QueryStoreStatsInterval>60</QueryStoreStatsInterval>");
            sb.AppendLine("    <QueryStoreMaxPlansPerQuery>200</QueryStoreMaxPlansPerQuery>");
            sb.AppendLine("    <QueryStoreMaxStorageSize>1024</QueryStoreMaxStorageSize>");
            sb.AppendLine("    <DbScopedConfigLegacyCardinalityEstimation>Off</DbScopedConfigLegacyCardinalityEstimation>");
            sb.AppendLine("    <DbScopedConfigMaxDOP>0</DbScopedConfigMaxDOP>");
            sb.AppendLine("    <DbScopedConfigParameterSniffing>On</DbScopedConfigParameterSniffing>");
            sb.AppendLine("    <DbScopedConfigQueryOptimizerHotfixes>Off</DbScopedConfigQueryOptimizerHotfixes>");
            sb.AppendLine("    <DelayedDurability>DISABLED</DelayedDurability>");
            sb.AppendLine("    <AutoCreateStatisticsIncremental>False</AutoCreateStatisticsIncremental>");
            sb.AppendLine("    <MemoryOptimizedElevateToSnapshot>False</MemoryOptimizedElevateToSnapshot>");
            sb.AppendLine("    <Containment>None</Containment>");
            sb.AppendLine("    <IsNestedTriggersOn>True</IsNestedTriggersOn>");
            sb.AppendLine("    <IsTransformNoiseWordsOn>False</IsTransformNoiseWordsOn>");
            sb.AppendLine("    <TwoDigitYearCutoff>2049</TwoDigitYearCutoff>");
            sb.AppendLine("    <NonTransactedFileStreamAccess>OFF</NonTransactedFileStreamAccess>");
            sb.AppendLine("    <TargetRecoveryTimePeriod>60</TargetRecoveryTimePeriod>");
            sb.AppendLine("    <TargetRecoveryTimeUnit>SECONDS</TargetRecoveryTimeUnit>");
            sb.AppendLine("    <IsChangeTrackingOn>False</IsChangeTrackingOn>");
            sb.AppendLine("    <IsChangeTrackingAutoCleanupOn>True</IsChangeTrackingAutoCleanupOn>");
            sb.AppendLine("    <ChangeTrackingRetentionPeriod>2</ChangeTrackingRetentionPeriod>");
            sb.AppendLine("    <ChangeTrackingRetentionUnit>Days</ChangeTrackingRetentionUnit>");
            sb.AppendLine("    <IsEncryptionOn>False</IsEncryptionOn>");
            sb.AppendLine("    <IsBrokerPriorityHonored>False</IsBrokerPriorityHonored>");
            sb.AppendLine("    <Trustworthy>False</Trustworthy>");
            sb.AppendLine("    <AutoUpdateStatisticsAsynchronously>False</AutoUpdateStatisticsAsynchronously>");
            sb.AppendLine("    <PageVerify>CHECKSUM</PageVerify>");
            sb.AppendLine("    <ServiceBrokerOption>DisableBroker</ServiceBrokerOption>");
            sb.AppendLine("    <DateCorrelationOptimizationOn>False</DateCorrelationOptimizationOn>");
            sb.AppendLine("    <Parameterization>SIMPLE</Parameterization>");
            sb.AppendLine("    <AllowSnapshotIsolation>True</AllowSnapshotIsolation>");
            sb.AppendLine("    <ReadCommittedSnapshot>True</ReadCommittedSnapshot>");
            sb.AppendLine("    <VardecimalStorageFormatOn>True</VardecimalStorageFormatOn>");
            sb.AppendLine("    <SupplementalLoggingOn>False</SupplementalLoggingOn>");
            sb.AppendLine("    <CompatibilityMode>150</CompatibilityMode>");
            sb.AppendLine("  </PropertyGroup>");
            
            // Add build configurations
            sb.AppendLine("  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' \">");
            sb.AppendLine("    <OutputPath>bin\\Release\\</OutputPath>");
            sb.AppendLine("    <BuildScriptName>$(MSBuildProjectName).sql</BuildScriptName>");
            sb.AppendLine("    <TreatWarningsAsErrors>False</TreatWarningsAsErrors>");
            sb.AppendLine("    <DebugType>pdbonly</DebugType>");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine("    <DefineDebug>false</DefineDebug>");
            sb.AppendLine("    <DefineTrace>true</DefineTrace>");
            sb.AppendLine("    <ErrorReport>prompt</ErrorReport>");
            sb.AppendLine("    <WarningLevel>4</WarningLevel>");
            sb.AppendLine("  </PropertyGroup>");
            
            sb.AppendLine("  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' \">");
            sb.AppendLine("    <OutputPath>bin\\Debug\\</OutputPath>");
            sb.AppendLine("    <BuildScriptName>$(MSBuildProjectName).sql</BuildScriptName>");
            sb.AppendLine("    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("    <DebugType>full</DebugType>");
            sb.AppendLine("    <Optimize>false</Optimize>");
            sb.AppendLine("    <DefineDebug>true</DefineDebug>");
            sb.AppendLine("    <DefineTrace>true</DefineTrace>");
            sb.AppendLine("    <ErrorReport>prompt</ErrorReport>");
            sb.AppendLine("    <WarningLevel>4</WarningLevel>");
            sb.AppendLine("  </PropertyGroup>");

            // Add target framework import
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <VisualStudioVersion Condition=\"'$(VisualStudioVersion)' == ''\">11.0</VisualStudioVersion>");
            sb.AppendLine("    <SSDTExists Condition=\"Exists('$(MSBuildExtensionsPath)\\Microsoft\\VisualStudio\\v$(VisualStudioVersion)\\SSDT\\Microsoft.Data.Tools.Schema.SqlTasks.targets')\">True</SSDTExists>");
            sb.AppendLine("    <VisualStudioVersion Condition=\"'$(SSDTExists)' == ''\">11.0</VisualStudioVersion>");
            sb.AppendLine("  </PropertyGroup>");

            // Add item groups for scripts
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Folder Include=\"Tables\\\" />");
            sb.AppendLine("    <Folder Include=\"Indexes\\\" />");
            sb.AppendLine("    <Folder Include=\"StoredProcedures\\\" />");
            sb.AppendLine("    <Folder Include=\"Scripts\\\" />");
            sb.AppendLine("    <Folder Include=\"Scripts\\PreDeployment\\\" />");
            sb.AppendLine("    <Folder Include=\"Scripts\\PostDeployment\\\" />");
            sb.AppendLine("    <Folder Include=\"Scripts\\DataMigration\\\" />");
            sb.AppendLine("    <Folder Include=\"Security\\\" />");
            sb.AppendLine("    <Folder Include=\"Security\\Schemas\\\" />");
            sb.AppendLine("  </ItemGroup>");

            // Add build includes
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Build Include=\"Security\\Schemas\\Schemas.sql\" />");
            foreach (var dbMapping in project.Assessment.DatabaseMappings)
            {
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    sb.AppendLine($"    <Build Include=\"Tables\\{containerMapping.TargetTable}.sql\" />");
                }
            }
            
            foreach (var indexRec in project.Assessment.IndexRecommendations)
            {
                sb.AppendLine($"    <Build Include=\"Indexes\\{indexRec.IndexName}.sql\" />");
            }

            foreach (var transformationRule in project.Assessment.TransformationRules)
            {
                var procName = $"sp_Migrate_{transformationRule.RuleName.Replace(" ", "")}";
                sb.AppendLine($"    <Build Include=\"StoredProcedures\\{procName}.sql\" />");
            }
            sb.AppendLine("  </ItemGroup>");

            // Add pre/post deployment scripts
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PreDeploy Include=\"Scripts\\PreDeployment\\Script.PreDeployment.sql\" />");
            sb.AppendLine("    <PostDeploy Include=\"Scripts\\PostDeployment\\Script.PostDeployment.sql\" />");
            sb.AppendLine("  </ItemGroup>");

            // Add target imports
            sb.AppendLine("  <Import Condition=\"'$(SQLDBExtensionsRefPath)' != ''\" Project=\"$(SQLDBExtensionsRefPath)\\Microsoft.Data.Tools.Schema.SqlTasks.targets\" />");
            sb.AppendLine("  <Import Condition=\"'$(SQLDBExtensionsRefPath)' == ''\" Project=\"$(MSBuildExtensionsPath)\\Microsoft\\VisualStudio\\v$(VisualStudioVersion)\\SSDT\\Microsoft.Data.Tools.Schema.SqlTasks.targets\" />");
            
            sb.AppendLine("</Project>");

            return sb.ToString();
        }

        private string GenerateTableScript(ContainerMapping containerMapping)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine($"-- Table: {containerMapping.TargetSchema}.{containerMapping.TargetTable}");
            sb.AppendLine($"-- Generated from Cosmos DB container: {containerMapping.SourceContainer}");
            sb.AppendLine($"-- Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            
            sb.AppendLine($"CREATE TABLE [{containerMapping.TargetSchema}].[{containerMapping.TargetTable}]");
            sb.AppendLine("(");

            for (int i = 0; i < containerMapping.FieldMappings.Count; i++)
            {
                var field = containerMapping.FieldMappings[i];
                var comma = i < containerMapping.FieldMappings.Count - 1 ? "," : "";
                
                var nullability = field.IsNullable ? "NULL" : "NOT NULL";
                sb.AppendLine($"    [{field.TargetColumn}] {field.TargetType} {nullability}{comma}");
            }

            // Add primary key constraint
            var pkFields = containerMapping.FieldMappings.Where(f => f.IsPartitionKey).ToList();
            if (pkFields.Any())
            {
                var pkColumns = string.Join(", ", pkFields.Select(f => $"[{f.TargetColumn}]"));
                sb.AppendLine($"    CONSTRAINT [PK_{containerMapping.TargetTable}] PRIMARY KEY CLUSTERED ({pkColumns})");
            }

            sb.AppendLine(");");
            sb.AppendLine();
            
            // Add comments for transformation requirements
            if (containerMapping.RequiredTransformations.Any())
            {
                sb.AppendLine("-- Required Transformations:");
                foreach (var transformation in containerMapping.RequiredTransformations)
                {
                    sb.AppendLine($"-- * {transformation}");
                }
            }

            return sb.ToString();
        }

        private string GenerateIndexScript(IndexRecommendation indexRec)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine($"-- Index: {indexRec.IndexName}");
            sb.AppendLine($"-- Table: {indexRec.TableName}");
            sb.AppendLine($"-- Justification: {indexRec.Justification}");
            sb.AppendLine($"-- Priority: {indexRec.Priority}");
            sb.AppendLine($"-- Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();

            var indexType = indexRec.IndexType.ToUpper() switch
            {
                "CLUSTERED" => "CLUSTERED",
                "NONCLUSTERED" => "NONCLUSTERED",
                "UNIQUE" => "UNIQUE NONCLUSTERED",
                "COLUMNSTORE" => "NONCLUSTERED COLUMNSTORE",
                _ => "NONCLUSTERED"
            };

            var columns = string.Join(", ", indexRec.Columns.Select(c => $"[{c}]"));
            
            sb.AppendLine($"CREATE {indexType} INDEX [{indexRec.IndexName}]");
            sb.AppendLine($"ON [dbo].[{indexRec.TableName}] ({columns})");
            
            if (indexRec.IncludedColumns.Any())
            {
                var includedCols = string.Join(", ", indexRec.IncludedColumns.Select(c => $"[{c}]"));
                sb.AppendLine($"INCLUDE ({includedCols})");
            }
            
            sb.AppendLine("WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF);");

            return sb.ToString();
        }

        private string GenerateDataMigrationStoredProcedure(TransformationRule transformationRule)
        {
            var sb = new StringBuilder();
            var procName = $"sp_Migrate_{transformationRule.RuleName.Replace(" ", "")}";
            
            sb.AppendLine($"-- Stored Procedure: {procName}");
            sb.AppendLine($"-- Transformation Rule: {transformationRule.RuleName}");
            sb.AppendLine($"-- Type: {transformationRule.TransformationType}");
            sb.AppendLine($"-- Logic: {transformationRule.Logic}");
            sb.AppendLine($"-- Affected Tables: {string.Join(", ", transformationRule.AffectedTables)}");
            sb.AppendLine($"-- Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();

            sb.AppendLine($"CREATE PROCEDURE [dbo].[{procName}]");
            sb.AppendLine("    @BatchSize INT = 1000,");
            sb.AppendLine("    @LogProgress BIT = 1");
            sb.AppendLine("AS");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    SET NOCOUNT ON;");
            sb.AppendLine();
            sb.AppendLine("    DECLARE @RowsProcessed INT = 0;");
            sb.AppendLine("    DECLARE @TotalRows INT = 0;");
            sb.AppendLine("    DECLARE @StartTime DATETIME2 = GETUTCDATE();");
            sb.AppendLine();
            sb.AppendLine($"    -- Transformation logic for: {transformationRule.TransformationType}");
            sb.AppendLine($"    -- Pattern: {transformationRule.SourcePattern} => {transformationRule.TargetPattern}");
            sb.AppendLine();
            sb.AppendLine("    BEGIN TRY");
            sb.AppendLine("        BEGIN TRANSACTION;");
            sb.AppendLine();

            // Add transformation-specific logic based on type
            switch (transformationRule.TransformationType.ToLower())
            {
                case "flatten":
                    sb.AppendLine("        -- Flatten nested objects");
                    sb.AppendLine("        -- TODO: Implement specific flattening logic based on source schema");
                    break;
                case "split":
                    sb.AppendLine("        -- Split arrays into separate tables");
                    sb.AppendLine("        -- TODO: Implement array splitting logic");
                    break;
                case "combine":
                    sb.AppendLine("        -- Combine multiple fields");
                    sb.AppendLine("        -- TODO: Implement field combination logic");
                    break;
                case "typeconvert":
                    sb.AppendLine("        -- Convert data types");
                    sb.AppendLine("        -- TODO: Implement type conversion logic");
                    break;
                default:
                    sb.AppendLine("        -- Custom transformation");
                    sb.AppendLine("        -- TODO: Implement custom transformation logic");
                    break;
            }

            sb.AppendLine();
            sb.AppendLine("        IF @LogProgress = 1");
            sb.AppendLine("        BEGIN");
            sb.AppendLine("            PRINT 'Transformation completed successfully.';");
            sb.AppendLine("            PRINT 'Rows processed: ' + CAST(@RowsProcessed AS VARCHAR(20));");
            sb.AppendLine("            PRINT 'Execution time: ' + CAST(DATEDIFF(SECOND, @StartTime, GETUTCDATE()) AS VARCHAR(20)) + ' seconds';");
            sb.AppendLine("        END");
            sb.AppendLine();
            sb.AppendLine("        COMMIT TRANSACTION;");
            sb.AppendLine("    END TRY");
            sb.AppendLine("    BEGIN CATCH");
            sb.AppendLine("        IF @@TRANCOUNT > 0");
            sb.AppendLine("            ROLLBACK TRANSACTION;");
            sb.AppendLine();
            sb.AppendLine("        DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();");
            sb.AppendLine("        DECLARE @ErrorSeverity INT = ERROR_SEVERITY();");
            sb.AppendLine("        DECLARE @ErrorState INT = ERROR_STATE();");
            sb.AppendLine();
            sb.AppendLine("        RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
            sb.AppendLine("    END CATCH");
            sb.AppendLine("END");

            return sb.ToString();
        }

        private string GeneratePreDeploymentScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Pre-Deployment Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("-- This script is executed before the main deployment script");
            sb.AppendLine();
            sb.AppendLine("-- Disable foreign key checks during deployment");
            sb.AppendLine("-- EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
            sb.AppendLine();
            sb.AppendLine("-- Create backup of existing data if tables exist");
            sb.AppendLine("-- (Add backup logic here if needed)");
            sb.AppendLine();
            sb.AppendLine("PRINT 'Pre-deployment script executed successfully.'");

            return sb.ToString();
        }

        private string GeneratePostDeploymentScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Post-Deployment Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("-- This script is executed after the main deployment script");
            sb.AppendLine();
            sb.AppendLine("-- Re-enable foreign key checks");
            sb.AppendLine("-- EXEC sp_MSforeachtable 'ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL'");
            sb.AppendLine();
            sb.AppendLine("-- Update statistics for all tables");
            sb.AppendLine("EXEC sp_updatestats;");
            sb.AppendLine();
            sb.AppendLine("-- Rebuild indexes if needed");
            sb.AppendLine("-- EXEC sp_MSforeachtable 'ALTER INDEX ALL ON ? REBUILD'");
            sb.AppendLine();
            sb.AppendLine("PRINT 'Post-deployment script executed successfully.'");

            return sb.ToString();
        }

        private string GenerateDataValidationScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Data Validation Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("-- Validates data integrity after migration");
            sb.AppendLine();

            foreach (var dbMapping in project.Assessment.DatabaseMappings)
            {
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    sb.AppendLine($"-- Validate {containerMapping.TargetTable}");
                    sb.AppendLine($"SELECT COUNT(*) AS [{containerMapping.TargetTable}_Count] FROM [{containerMapping.TargetSchema}].[{containerMapping.TargetTable}];");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("PRINT 'Data validation completed.'");

            return sb.ToString();
        }

        private string GenerateDataCleanupScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Data Cleanup Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("-- Cleans up temporary data and optimizes database");
            sb.AppendLine();
            sb.AppendLine("-- Remove any temporary tables");
            sb.AppendLine("-- DROP TABLE IF EXISTS #TempTable");
            sb.AppendLine();
            sb.AppendLine("-- Clean up old backup data");
            sb.AppendLine("-- (Add cleanup logic here if needed)");
            sb.AppendLine();
            sb.AppendLine("-- Shrink log file if necessary");
            sb.AppendLine("-- DBCC SHRINKFILE (LogicalFileName, 1)");
            sb.AppendLine();
            sb.AppendLine("PRINT 'Data cleanup completed.'");

            return sb.ToString();
        }

        private string GenerateSecurityConfigurationScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Security Configuration Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            sb.AppendLine("-- Create application roles");
            sb.AppendLine("IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'app_reader')");
            sb.AppendLine("    CREATE ROLE [app_reader];");
            sb.AppendLine();
            sb.AppendLine("IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'app_writer')");
            sb.AppendLine("    CREATE ROLE [app_writer];");
            sb.AppendLine();
            sb.AppendLine("-- Grant permissions to roles");
            sb.AppendLine("GRANT SELECT ON SCHEMA::dbo TO [app_reader];");
            sb.AppendLine("GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [app_writer];");
            sb.AppendLine();
            sb.AppendLine("PRINT 'Security configuration completed.'");

            return sb.ToString();
        }

        private string GenerateSchemaCreationScript(SqlDatabaseProject project)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("-- Schema Creation Script");
            sb.AppendLine($"-- Project: {project.ProjectName}");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            
            // Create schemas used in the project
            var schemas = project.Assessment.DatabaseMappings
                .SelectMany(dm => dm.ContainerMappings)
                .Select(cm => cm.TargetSchema)
                .Distinct()
                .Where(s => !string.IsNullOrEmpty(s) && s != "dbo");

            foreach (var schema in schemas)
            {
                sb.AppendLine($"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}')");
                sb.AppendLine($"    EXEC('CREATE SCHEMA [{schema}]');");
                sb.AppendLine();
            }

            sb.AppendLine("PRINT 'Schema creation completed.'");

            return sb.ToString();
        }

        #endregion
    }
}
