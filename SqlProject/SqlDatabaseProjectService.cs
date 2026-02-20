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
                Path.Combine(project.OutputPath, "Scripts", "PreDeployment"),
                Path.Combine(project.OutputPath, "Scripts", "PostDeployment"),
                Path.Combine(project.OutputPath, "Scripts", "DataMigration"),
                Path.Combine(project.OutputPath, "Security"),
                Path.Combine(project.OutputPath, "Security", "Schemas")
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
                    AppendFlattenTransformationLogic(sb, transformationRule);
                    break;
                case "split":
                    AppendSplitTransformationLogic(sb, transformationRule);
                    break;
                case "combine":
                    AppendCombineTransformationLogic(sb, transformationRule);
                    break;
                case "typeconvert":
                    AppendTypeConvertTransformationLogic(sb, transformationRule);
                    break;
                default:
                    AppendCustomTransformationLogic(sb, transformationRule);
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

        #region Transformation Logic Methods

        /// <summary>
        /// Generates T-SQL logic for flattening nested objects into flat table columns
        /// </summary>
        private void AppendFlattenTransformationLogic(StringBuilder sb, TransformationRule transformationRule)
        {
            sb.AppendLine("        -- Flatten nested objects into flat table columns");
            sb.AppendLine("        -- This transformation extracts nested JSON properties into individual columns");
            sb.AppendLine();
            
            foreach (var tableName in transformationRule.AffectedTables)
            {
                sb.AppendLine($"        -- Process table: {tableName}");
                sb.AppendLine($"        DECLARE @CurrentBatch_{tableName} INT = 0;");
                sb.AppendLine($"        DECLARE @TotalBatches_{tableName} INT;");
                sb.AppendLine();
                sb.AppendLine($"        -- Get total number of rows to process");
                sb.AppendLine($"        SELECT @TotalBatches_{tableName} = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)");
                sb.AppendLine($"        FROM [{tableName}]");
                sb.AppendLine($"        WHERE SourceJson IS NOT NULL; -- Assuming SourceJson column exists");
                sb.AppendLine();
                sb.AppendLine($"        -- Process batches to avoid locking issues");
                sb.AppendLine($"        WHILE @CurrentBatch_{tableName} < @TotalBatches_{tableName}");
                sb.AppendLine("        BEGIN");
                sb.AppendLine("            -- Extract and flatten nested JSON properties");
                sb.AppendLine("            -- Example: Extract 'address.street', 'address.city', 'address.zipCode' from nested object");
                sb.AppendLine("            UPDATE TOP (@BatchSize) t");
                sb.AppendLine("            SET ");
                sb.AppendLine("                -- Flatten nested properties using JSON_VALUE");
                sb.AppendLine("                -- Example transformations based on source pattern:");
                sb.AppendLine($"                -- t.address_street = JSON_VALUE(t.SourceJson, '$.address.street'),");
                sb.AppendLine($"                -- t.address_city = JSON_VALUE(t.SourceJson, '$.address.city'),");
                sb.AppendLine($"                -- t.address_zipCode = JSON_VALUE(t.SourceJson, '$.address.zipCode'),");
                sb.AppendLine("                -- Handle null values gracefully");
                sb.AppendLine($"                t.ProcessedFlag = 1, -- Mark as processed");
                sb.AppendLine($"                @RowsProcessed = @RowsProcessed + @@ROWCOUNT");
                sb.AppendLine($"            FROM [{tableName}] t");
                sb.AppendLine("            WHERE t.SourceJson IS NOT NULL");
                sb.AppendLine("                AND (t.ProcessedFlag IS NULL OR t.ProcessedFlag = 0);");
                sb.AppendLine();
                sb.AppendLine($"            SET @CurrentBatch_{tableName} = @CurrentBatch_{tableName} + 1;");
                sb.AppendLine();
                sb.AppendLine("            IF @LogProgress = 1");
                sb.AppendLine("            BEGIN");
                sb.AppendLine($"                PRINT 'Flattening {tableName}: Batch ' + CAST(@CurrentBatch_{tableName} AS VARCHAR) + ' of ' + CAST(@TotalBatches_{tableName} AS VARCHAR);");
                sb.AppendLine("            END");
                sb.AppendLine("        END");
                sb.AppendLine();
            }
            
            sb.AppendLine("        -- Additional flattening logic");
            sb.AppendLine("        -- For complex nested structures, you can use CROSS APPLY with OPENJSON");
            sb.AppendLine("        -- Example:");
            sb.AppendLine("        -- UPDATE t");
            sb.AppendLine("        -- SET t.flattened_column = j.value");
            sb.AppendLine("        -- FROM [TableName] t");
            sb.AppendLine("        -- CROSS APPLY OPENJSON(t.SourceJson, '$.path.to.nested') j");
        }

        /// <summary>
        /// Generates T-SQL logic for splitting arrays into child table rows
        /// </summary>
        private void AppendSplitTransformationLogic(StringBuilder sb, TransformationRule transformationRule)
        {
            sb.AppendLine("        -- Split arrays into separate child table rows");
            sb.AppendLine("        -- This transformation normalizes array data into relational child tables");
            sb.AppendLine();
            
            foreach (var tableName in transformationRule.AffectedTables)
            {
                var childTableName = $"{tableName}_ArrayItems";
                
                sb.AppendLine($"        -- Process array splitting for table: {tableName}");
                sb.AppendLine($"        DECLARE @CurrentBatch_{tableName}_Array INT = 0;");
                sb.AppendLine($"        DECLARE @TotalBatches_{tableName}_Array INT;");
                sb.AppendLine();
                sb.AppendLine($"        -- Get total number of parent rows with arrays to process");
                sb.AppendLine($"        SELECT @TotalBatches_{tableName}_Array = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)");
                sb.AppendLine($"        FROM [{tableName}]");
                sb.AppendLine($"        WHERE ArrayJson IS NOT NULL AND ISJSON(ArrayJson) = 1;");
                sb.AppendLine();
                sb.AppendLine($"        -- Process batches");
                sb.AppendLine($"        WHILE @CurrentBatch_{tableName}_Array < @TotalBatches_{tableName}_Array");
                sb.AppendLine("        BEGIN");
                sb.AppendLine("            -- Split array elements into child table rows");
                sb.AppendLine("            -- Using OPENJSON to parse array and insert into child table");
                sb.AppendLine($"            ;WITH ArrayData AS (");
                sb.AppendLine("                SELECT TOP (@BatchSize)");
                sb.AppendLine("                    t.Id AS ParentId,");
                sb.AppendLine("                    t.ArrayJson,");
                sb.AppendLine("                    ROW_NUMBER() OVER (ORDER BY t.Id) AS RowNum");
                sb.AppendLine($"                FROM [{tableName}] t");
                sb.AppendLine("                WHERE t.ArrayJson IS NOT NULL ");
                sb.AppendLine("                    AND ISJSON(t.ArrayJson) = 1");
                sb.AppendLine("                    AND (t.ArrayProcessedFlag IS NULL OR t.ArrayProcessedFlag = 0)");
                sb.AppendLine("            )");
                sb.AppendLine($"            INSERT INTO [{childTableName}] (ParentId, ArrayIndex, ArrayValue, ArrayItemJson)");
                sb.AppendLine("            SELECT ");
                sb.AppendLine("                ad.ParentId,");
                sb.AppendLine("                CAST(j.[key] AS INT) AS ArrayIndex,");
                sb.AppendLine("                j.[value] AS ArrayValue,");
                sb.AppendLine("                -- Parse complex objects within array");
                sb.AppendLine("                CASE ");
                sb.AppendLine("                    WHEN ISJSON(j.[value]) = 1 THEN j.[value]");
                sb.AppendLine("                    ELSE NULL");
                sb.AppendLine("                END AS ArrayItemJson");
                sb.AppendLine("            FROM ArrayData ad");
                sb.AppendLine("            CROSS APPLY OPENJSON(ad.ArrayJson) j;");
                sb.AppendLine();
                sb.AppendLine("            -- Mark parent rows as processed");
                sb.AppendLine("            UPDATE t");
                sb.AppendLine("            SET t.ArrayProcessedFlag = 1,");
                sb.AppendLine("                @RowsProcessed = @RowsProcessed + @@ROWCOUNT");
                sb.AppendLine($"            FROM [{tableName}] t");
                sb.AppendLine("            WHERE t.Id IN (");
                sb.AppendLine("                SELECT TOP (@BatchSize) t2.Id");
                sb.AppendLine($"                FROM [{tableName}] t2");
                sb.AppendLine("                WHERE t2.ArrayJson IS NOT NULL");
                sb.AppendLine("                    AND ISJSON(t2.ArrayJson) = 1");
                sb.AppendLine("                    AND (t2.ArrayProcessedFlag IS NULL OR t2.ArrayProcessedFlag = 0)");
                sb.AppendLine("            );");
                sb.AppendLine();
                sb.AppendLine($"            SET @CurrentBatch_{tableName}_Array = @CurrentBatch_{tableName}_Array + 1;");
                sb.AppendLine();
                sb.AppendLine("            IF @LogProgress = 1");
                sb.AppendLine("            BEGIN");
                sb.AppendLine($"                PRINT 'Splitting arrays in {tableName}: Batch ' + CAST(@CurrentBatch_{tableName}_Array AS VARCHAR) + ' of ' + CAST(@TotalBatches_{tableName}_Array AS VARCHAR);");
                sb.AppendLine("            END");
                sb.AppendLine("        END");
                sb.AppendLine();
            }
            
            sb.AppendLine("        -- Note: Ensure child tables exist with proper foreign key relationships");
            sb.AppendLine("        -- Child table structure should include: ParentId (FK), ArrayIndex, ArrayValue, ArrayItemJson");
        }

        /// <summary>
        /// Generates T-SQL logic for combining multiple fields into a single column
        /// </summary>
        private void AppendCombineTransformationLogic(StringBuilder sb, TransformationRule transformationRule)
        {
            sb.AppendLine("        -- Combine multiple fields into single columns");
            sb.AppendLine("        -- This transformation concatenates or computes derived values from multiple source fields");
            sb.AppendLine();
            
            foreach (var tableName in transformationRule.AffectedTables)
            {
                sb.AppendLine($"        -- Process field combination for table: {tableName}");
                sb.AppendLine($"        DECLARE @CurrentBatch_{tableName}_Combine INT = 0;");
                sb.AppendLine($"        DECLARE @TotalBatches_{tableName}_Combine INT;");
                sb.AppendLine();
                sb.AppendLine($"        -- Get total number of rows to process");
                sb.AppendLine($"        SELECT @TotalBatches_{tableName}_Combine = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)");
                sb.AppendLine($"        FROM [{tableName}]");
                sb.AppendLine($"        WHERE CombineProcessedFlag IS NULL OR CombineProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine($"        -- Process batches");
                sb.AppendLine($"        WHILE @CurrentBatch_{tableName}_Combine < @TotalBatches_{tableName}_Combine");
                sb.AppendLine("        BEGIN");
                sb.AppendLine("            -- Combine fields using various strategies");
                sb.AppendLine("            UPDATE TOP (@BatchSize) t");
                sb.AppendLine("            SET ");
                sb.AppendLine("                -- Example 1: Concatenate string fields (e.g., FirstName + LastName = FullName)");
                sb.AppendLine("                -- t.FullName = TRIM(CONCAT(t.FirstName, ' ', t.MiddleName, ' ', t.LastName)),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 2: Combine address components");
                sb.AppendLine("                -- t.FullAddress = TRIM(CONCAT_WS(', ', t.Street, t.City, t.State, t.ZipCode)),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 3: Create computed values (e.g., area from width and height)");
                sb.AppendLine("                -- t.Area = CASE WHEN t.Width IS NOT NULL AND t.Height IS NOT NULL ");
                sb.AppendLine("                --              THEN t.Width * t.Height ");
                sb.AppendLine("                --              ELSE NULL END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 4: Combine date and time into datetime");
                sb.AppendLine("                -- t.FullDateTime = CASE WHEN t.DateValue IS NOT NULL AND t.TimeValue IS NOT NULL");
                sb.AppendLine("                --                       THEN CAST(CAST(t.DateValue AS DATE) + CAST(t.TimeValue AS TIME) AS DATETIME2)");
                sb.AppendLine("                --                       ELSE NULL END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 5: Create JSON object from multiple fields");
                sb.AppendLine("                -- t.CombinedJson = (");
                sb.AppendLine("                --     SELECT t.Field1 AS field1, t.Field2 AS field2, t.Field3 AS field3");
                sb.AppendLine("                --     FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
                sb.AppendLine("                -- ),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Mark as processed");
                sb.AppendLine($"                t.CombineProcessedFlag = 1,");
                sb.AppendLine($"                @RowsProcessed = @RowsProcessed + @@ROWCOUNT");
                sb.AppendLine($"            FROM [{tableName}] t");
                sb.AppendLine("            WHERE t.CombineProcessedFlag IS NULL OR t.CombineProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine($"            SET @CurrentBatch_{tableName}_Combine = @CurrentBatch_{tableName}_Combine + 1;");
                sb.AppendLine();
                sb.AppendLine("            IF @LogProgress = 1");
                sb.AppendLine("            BEGIN");
                sb.AppendLine($"                PRINT 'Combining fields in {tableName}: Batch ' + CAST(@CurrentBatch_{tableName}_Combine AS VARCHAR) + ' of ' + CAST(@TotalBatches_{tableName}_Combine AS VARCHAR);");
                sb.AppendLine("            END");
                sb.AppendLine("        END");
                sb.AppendLine();
            }
            
            sb.AppendLine("        -- Note: Ensure combined columns exist in target tables with appropriate data types");
            sb.AppendLine("        -- Use NULL-safe functions like CONCAT, COALESCE, ISNULL to handle missing values");
        }

        /// <summary>
        /// Generates T-SQL logic for type conversion between Cosmos DB and SQL types
        /// </summary>
        private void AppendTypeConvertTransformationLogic(StringBuilder sb, TransformationRule transformationRule)
        {
            sb.AppendLine("        -- Convert data types from Cosmos DB to SQL Server types");
            sb.AppendLine("        -- This transformation handles type conversions with validation and error handling");
            sb.AppendLine();
            
            foreach (var tableName in transformationRule.AffectedTables)
            {
                sb.AppendLine($"        -- Process type conversion for table: {tableName}");
                sb.AppendLine($"        DECLARE @CurrentBatch_{tableName}_Convert INT = 0;");
                sb.AppendLine($"        DECLARE @TotalBatches_{tableName}_Convert INT;");
                sb.AppendLine($"        DECLARE @ConversionErrors_{tableName} INT = 0;");
                sb.AppendLine();
                sb.AppendLine($"        -- Get total number of rows to process");
                sb.AppendLine($"        SELECT @TotalBatches_{tableName}_Convert = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)");
                sb.AppendLine($"        FROM [{tableName}]");
                sb.AppendLine($"        WHERE ConvertProcessedFlag IS NULL OR ConvertProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine($"        -- Process batches");
                sb.AppendLine($"        WHILE @CurrentBatch_{tableName}_Convert < @TotalBatches_{tableName}_Convert");
                sb.AppendLine("        BEGIN");
                sb.AppendLine("            -- Perform type conversions with validation");
                sb.AppendLine("            UPDATE TOP (@BatchSize) t");
                sb.AppendLine("            SET ");
                sb.AppendLine("                -- Example 1: Convert string to INT with validation");
                sb.AppendLine("                -- t.IntValue = TRY_CAST(t.SourceStringValue AS INT),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 2: Convert string to DATETIME with validation");
                sb.AppendLine("                -- t.DateTimeValue = TRY_CONVERT(DATETIME2, t.SourceDateString, 127), -- ISO 8601 format");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 3: Convert string to DECIMAL with validation");
                sb.AppendLine("                -- t.DecimalValue = TRY_CAST(t.SourceDecimalString AS DECIMAL(18,2)),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 4: Convert boolean strings to BIT");
                sb.AppendLine("                -- t.BoolValue = CASE ");
                sb.AppendLine("                --     WHEN LOWER(t.SourceBoolString) IN ('true', '1', 'yes', 't', 'y') THEN 1");
                sb.AppendLine("                --     WHEN LOWER(t.SourceBoolString) IN ('false', '0', 'no', 'f', 'n') THEN 0");
                sb.AppendLine("                --     ELSE NULL -- Invalid value");
                sb.AppendLine("                -- END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 5: Convert GUID strings to UNIQUEIDENTIFIER");
                sb.AppendLine("                -- t.GuidValue = TRY_CAST(t.SourceGuidString AS UNIQUEIDENTIFIER),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 6: Handle Cosmos DB numeric strings that might be stored as strings");
                sb.AppendLine("                -- t.NumericValue = CASE");
                sb.AppendLine("                --     WHEN ISNUMERIC(t.SourceValue) = 1 THEN CAST(t.SourceValue AS FLOAT)");
                sb.AppendLine("                --     ELSE NULL");
                sb.AppendLine("                -- END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 7: Convert arrays stored as JSON strings to SQL JSON type");
                sb.AppendLine("                -- t.JsonArrayValue = CASE ");
                sb.AppendLine("                --     WHEN ISJSON(t.SourceArrayString) = 1 THEN t.SourceArrayString");
                sb.AppendLine("                --     ELSE NULL");
                sb.AppendLine("                -- END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example 8: Handle Unix epoch timestamps");
                sb.AppendLine("                -- t.DateTimeFromEpoch = CASE");
                sb.AppendLine("                --     WHEN TRY_CAST(t.SourceEpochValue AS BIGINT) IS NOT NULL");
                sb.AppendLine("                --     THEN DATEADD(SECOND, t.SourceEpochValue, '1970-01-01')");
                sb.AppendLine("                --     ELSE NULL");
                sb.AppendLine("                -- END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Track conversion errors");
                sb.AppendLine("                -- t.ConversionErrors = CONCAT_WS('; ',");
                sb.AppendLine("                --     CASE WHEN TRY_CAST(t.Field1 AS INT) IS NULL AND t.Field1 IS NOT NULL THEN 'Field1: Invalid INT' END,");
                sb.AppendLine("                --     CASE WHEN TRY_CAST(t.Field2 AS DATETIME2) IS NULL AND t.Field2 IS NOT NULL THEN 'Field2: Invalid DATE' END");
                sb.AppendLine("                -- ),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Mark as processed");
                sb.AppendLine($"                t.ConvertProcessedFlag = 1,");
                sb.AppendLine($"                @RowsProcessed = @RowsProcessed + @@ROWCOUNT");
                sb.AppendLine($"            FROM [{tableName}] t");
                sb.AppendLine("            WHERE t.ConvertProcessedFlag IS NULL OR t.ConvertProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine("            -- Count rows with conversion errors");
                sb.AppendLine($"            SELECT @ConversionErrors_{tableName} = COUNT(*)");
                sb.AppendLine($"            FROM [{tableName}]");
                sb.AppendLine("            WHERE ConversionErrors IS NOT NULL AND ConversionErrors <> '';");
                sb.AppendLine();
                sb.AppendLine($"            SET @CurrentBatch_{tableName}_Convert = @CurrentBatch_{tableName}_Convert + 1;");
                sb.AppendLine();
                sb.AppendLine("            IF @LogProgress = 1");
                sb.AppendLine("            BEGIN");
                sb.AppendLine($"                PRINT 'Converting types in {tableName}: Batch ' + CAST(@CurrentBatch_{tableName}_Convert AS VARCHAR) + ' of ' + CAST(@TotalBatches_{tableName}_Convert AS VARCHAR);");
                sb.AppendLine($"                IF @ConversionErrors_{tableName} > 0");
                sb.AppendLine($"                    PRINT 'Warning: ' + CAST(@ConversionErrors_{tableName} AS VARCHAR) + ' rows have conversion errors';");
                sb.AppendLine("            END");
                sb.AppendLine("        END");
                sb.AppendLine();
            }
            
            sb.AppendLine("        -- Note: Use TRY_CAST and TRY_CONVERT for safe type conversions");
            sb.AppendLine("        -- Log conversion errors for data quality analysis");
            sb.AppendLine("        -- Consider creating a separate errors table to track failed conversions");
        }

        /// <summary>
        /// Generates T-SQL logic for custom transformation extensibility
        /// </summary>
        private void AppendCustomTransformationLogic(StringBuilder sb, TransformationRule transformationRule)
        {
            sb.AppendLine("        -- Custom transformation logic");
            sb.AppendLine("        -- This section provides extensibility for custom business logic transformations");
            sb.AppendLine();
            sb.AppendLine("        -- EXTENSIBILITY POINT: Add your custom transformation logic here");
            sb.AppendLine("        -- This stored procedure can be modified to implement specific business rules");
            sb.AppendLine();
            
            foreach (var tableName in transformationRule.AffectedTables)
            {
                sb.AppendLine($"        -- Custom transformation for table: {tableName}");
                sb.AppendLine($"        DECLARE @CurrentBatch_{tableName}_Custom INT = 0;");
                sb.AppendLine($"        DECLARE @TotalBatches_{tableName}_Custom INT;");
                sb.AppendLine();
                sb.AppendLine($"        -- Get total number of rows to process");
                sb.AppendLine($"        SELECT @TotalBatches_{tableName}_Custom = CEILING(CAST(COUNT(*) AS FLOAT) / @BatchSize)");
                sb.AppendLine($"        FROM [{tableName}]");
                sb.AppendLine($"        WHERE CustomProcessedFlag IS NULL OR CustomProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine($"        -- Process batches");
                sb.AppendLine($"        WHILE @CurrentBatch_{tableName}_Custom < @TotalBatches_{tableName}_Custom");
                sb.AppendLine("        BEGIN");
                sb.AppendLine("            -- Apply custom business rules and transformations");
                sb.AppendLine("            -- Examples of custom transformations:");
                sb.AppendLine("            ");
                sb.AppendLine("            -- 1. Data enrichment from lookup tables");
                sb.AppendLine("            -- 2. Complex calculated fields");
                sb.AppendLine("            -- 3. Data normalization or standardization");
                sb.AppendLine("            -- 4. Cross-table data validation");
                sb.AppendLine("            -- 5. Business rule enforcement");
                sb.AppendLine("            ");
                sb.AppendLine("            UPDATE TOP (@BatchSize) t");
                sb.AppendLine("            SET ");
                sb.AppendLine("                -- Example: Enrich with lookup data");
                sb.AppendLine("                -- t.CategoryName = c.Name,");
                sb.AppendLine("                -- t.CategoryPath = c.FullPath,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example: Apply business rules");
                sb.AppendLine("                -- t.DiscountedPrice = CASE ");
                sb.AppendLine("                --     WHEN t.Quantity >= 100 THEN t.Price * 0.9  -- 10% bulk discount");
                sb.AppendLine("                --     WHEN t.Quantity >= 50 THEN t.Price * 0.95   -- 5% discount");
                sb.AppendLine("                --     ELSE t.Price");
                sb.AppendLine("                -- END,");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Example: Data standardization");
                sb.AppendLine("                -- t.PhoneNumber = dbo.fn_FormatPhoneNumber(t.RawPhoneNumber),");
                sb.AppendLine("                -- t.EmailAddress = LOWER(TRIM(t.EmailAddress)),");
                sb.AppendLine("                ");
                sb.AppendLine("                -- Mark as processed");
                sb.AppendLine($"                t.CustomProcessedFlag = 1,");
                sb.AppendLine($"                @RowsProcessed = @RowsProcessed + @@ROWCOUNT");
                sb.AppendLine($"            FROM [{tableName}] t");
                sb.AppendLine("            -- LEFT JOIN LookupTable c ON t.CategoryId = c.Id -- Example join for enrichment");
                sb.AppendLine("            WHERE t.CustomProcessedFlag IS NULL OR t.CustomProcessedFlag = 0;");
                sb.AppendLine();
                sb.AppendLine($"            SET @CurrentBatch_{tableName}_Custom = @CurrentBatch_{tableName}_Custom + 1;");
                sb.AppendLine();
                sb.AppendLine("            IF @LogProgress = 1");
                sb.AppendLine("            BEGIN");
                sb.AppendLine($"                PRINT 'Custom transformation for {tableName}: Batch ' + CAST(@CurrentBatch_{tableName}_Custom AS VARCHAR) + ' of ' + CAST(@TotalBatches_{tableName}_Custom AS VARCHAR);");
                sb.AppendLine("            END");
                sb.AppendLine("        END");
                sb.AppendLine();
            }
            
            sb.AppendLine("        -- CONFIGURATION GUIDANCE:");
            sb.AppendLine("        -- 1. Review the transformation logic and source/target patterns");
            sb.AppendLine("        -- 2. Adjust field names and table names based on your actual schema");
            sb.AppendLine("        -- 3. Add necessary joins to lookup tables for data enrichment");
            sb.AppendLine("        -- 4. Implement custom validation rules specific to your business domain");
            sb.AppendLine("        -- 5. Consider creating user-defined functions for complex transformations");
            sb.AppendLine("        -- 6. Test transformations on a sample dataset before full migration");
            sb.AppendLine("        ");
            sb.AppendLine("        -- For complex scenarios, consider:");
            sb.AppendLine("        -- - Creating separate stored procedures for each custom rule");
            sb.AppendLine("        -- - Using a configuration table to drive transformation logic");
            sb.AppendLine("        -- - Implementing a transformation framework with metadata");
        }

        #endregion
    }
}
