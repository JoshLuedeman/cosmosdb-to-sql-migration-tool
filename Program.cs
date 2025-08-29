using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.Models;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment
{
    /// <summary>
    /// Cosmos DB to SQL Migration Assessment Tool
    /// Provides comprehensive analysis and migration recommendations following Azure best practices
    /// </summary>
    class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new();

        static async Task<int> Main(string[] args)
        {
            // Handle Ctrl+C gracefully
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _cancellationTokenSource.Cancel();
                Console.WriteLine("\nOperation cancelled by user.");
            };

            try
            {
                // Parse command line arguments
                var options = ParseCommandLineArguments(args);
                if (options == null)
                {
                    return 1; // Help was displayed or invalid arguments
                }

                // Validate command line options
                if (!ValidateCommandLineOptions(options))
                {
                    return 1;
                }

                // Build configuration
                var configuration = BuildConfiguration(options);

                // Setup dependency injection
                var services = ConfigureServices(configuration);
                using var serviceProvider = services.BuildServiceProvider();

                // Setup logging
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Starting Cosmos DB to SQL Migration Assessment Tool");

                // Display welcome message
                DisplayWelcomeMessage();

                // Get user inputs
                var userInputs = await GetUserInputsAsync(configuration, options, logger);
                if (userInputs == null)
                {
                    return 1;
                }

                // Validate effective configuration (post command-line processing)
                if (!ValidateEffectiveConfiguration(userInputs, logger))
                {
                    return 1;
                }

                // Run the assessment
                var assessmentResult = await RunAssessmentAsync(serviceProvider, configuration, userInputs, logger, _cancellationTokenSource.Token);

                // Generate outputs based on command line options
                await GenerateOutputsAsync(serviceProvider, assessmentResult, userInputs.OutputDirectory, options, logger, _cancellationTokenSource.Token);

                // Display completion message
                DisplayCompletionMessage(assessmentResult, options);

                logger.LogInformation("Assessment completed successfully");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Assessment cancelled.");
                return 130; // Standard exit code for SIGINT
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                
                // Log full exception details if logger is available
                try
                {
                    var configuration = BuildConfiguration();
                    var services = ConfigureServices(configuration);
                    using var serviceProvider = services.BuildServiceProvider();
                    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "Unhandled exception occurred during assessment");
                }
                catch
                {
                    // If logging setup fails, just write to console
                    Console.WriteLine($"Full error details: {ex}");
                }

                return 1;
            }
        }

        private static IConfiguration BuildConfiguration(CommandLineOptions? options = null)
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables();

            // Add command line overrides if provided
            if (options != null)
            {
                var commandLineConfig = new Dictionary<string, string?>();
                
                if (!string.IsNullOrEmpty(options.WorkspaceId))
                {
                    commandLineConfig["AzureMonitor:WorkspaceId"] = options.WorkspaceId;
                }
                
                configBuilder.AddInMemoryCollection(commandLineConfig);
            }

            return configBuilder.Build();
        }

        private static IServiceCollection ConfigureServices(IConfiguration configuration)
        {
            var services = new ServiceCollection();

            // Configuration
            services.AddSingleton(configuration);

            // Logging
            services.AddLogging(builder =>
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddConsole();
                builder.AddDebug();
            });

            // Application services
            services.AddScoped<CosmosDbAnalysisService>();
            services.AddScoped<SqlMigrationAssessmentService>();
            services.AddScoped<DataFactoryEstimateService>();
            services.AddScoped<ReportGenerationService>();
            services.AddScoped<SqlProjectGenerationService>();

            return services;
        }

        private static void DisplayWelcomeMessage()
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("    Cosmos DB to SQL Migration Assessment Tool");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("This tool will analyze your Cosmos DB database and provide:");
            Console.WriteLine("• Comprehensive performance and schema analysis");
            Console.WriteLine("• SQL migration recommendations and mapping");
            Console.WriteLine("• Azure Data Factory migration estimates");
            Console.WriteLine("• Detailed Excel and Word reports");
            Console.WriteLine("• Ready-to-deploy SQL Database projects (.sqlproj)");
            Console.WriteLine();
            Console.WriteLine("Features:");
            Console.WriteLine("• 6-month performance metrics analysis");
            Console.WriteLine("• Index recommendations based on usage patterns");
            Console.WriteLine("• Azure SQL platform recommendations");
            Console.WriteLine("• Migration effort and cost estimates");
            Console.WriteLine("• SSDT-compatible SQL projects for deployment");
            Console.WriteLine();
        }

        private static bool ValidateConfiguration(IConfiguration configuration, ILogger logger)
        {
            var isValid = true;
            var validationErrors = new List<string>();

            // Validate Cosmos DB configuration
            var cosmosEndpoint = configuration["CosmosDb:AccountEndpoint"];
            if (string.IsNullOrEmpty(cosmosEndpoint))
            {
                validationErrors.Add("CosmosDb:AccountEndpoint is required");
                isValid = false;
            }

            var databaseName = configuration["CosmosDb:DatabaseName"];
            if (string.IsNullOrEmpty(databaseName))
            {
                validationErrors.Add("CosmosDb:DatabaseName is required");
                isValid = false;
            }

            // Check Azure Monitor configuration (optional but recommended)
            var workspaceId = configuration["AzureMonitor:WorkspaceId"];
            if (string.IsNullOrEmpty(workspaceId))
            {
                logger.LogWarning("Azure Monitor workspace ID not configured. Performance metrics will be limited.");
                Console.WriteLine("⚠️  Warning: Azure Monitor not configured - performance analysis will be limited");
            }
            else
            {
                // Validate workspace ID format (should be a GUID)
                if (!Guid.TryParse(workspaceId, out _))
                {
                    logger.LogWarning("Azure Monitor workspace ID format is invalid. Expected GUID format.");
                    Console.WriteLine("⚠️  Warning: Invalid workspace ID format - should be a GUID (e.g., 12345678-1234-1234-1234-123456789012)");
                }
                else
                {
                    Console.WriteLine("✅ Azure Monitor workspace configured");
                }
            }

            // Display validation results
            if (!isValid)
            {
                Console.WriteLine("❌ Configuration validation failed:");
                foreach (var error in validationErrors)
                {
                    Console.WriteLine($"   • {error}");
                }
                Console.WriteLine();
                Console.WriteLine("Please update appsettings.json with the required configuration values.");
                return false;
            }

            Console.WriteLine("✅ Configuration validation passed");
            Console.WriteLine($"   • Cosmos DB Endpoint: {cosmosEndpoint}");
            Console.WriteLine($"   • Database: {databaseName}");
            
            if (!string.IsNullOrEmpty(workspaceId))
            {
                Console.WriteLine($"   • Azure Monitor: Configured");
            }
            
            Console.WriteLine();
            return true;
        }

        private static bool ValidateEffectiveConfiguration(UserInputs userInputs, ILogger logger)
        {
            var isValid = true;
            var validationErrors = new List<string>();

            // Validate Cosmos DB endpoint
            if (string.IsNullOrEmpty(userInputs.AccountEndpoint))
            {
                validationErrors.Add("Cosmos DB account endpoint is required");
                isValid = false;
            }

            // Validate database names
            if (!userInputs.DatabaseNames.Any())
            {
                validationErrors.Add("At least one database name is required");
                isValid = false;
            }

            // Validate output directory
            if (string.IsNullOrEmpty(userInputs.OutputDirectory))
            {
                validationErrors.Add("Output directory is required");
                isValid = false;
            }

            // Display validation results
            if (!isValid)
            {
                Console.WriteLine("❌ Configuration validation failed:");
                foreach (var error in validationErrors)
                {
                    Console.WriteLine($"   • {error}");
                }
                Console.WriteLine();
                return false;
            }

            Console.WriteLine("✅ Configuration validation passed");
            Console.WriteLine($"   • Cosmos DB Endpoint: {userInputs.AccountEndpoint}");
            Console.WriteLine($"   • Database(s): {string.Join(", ", userInputs.DatabaseNames)}");
            Console.WriteLine($"   • Output Directory: {userInputs.OutputDirectory}");
            
            if (userInputs.MonitoringConfig?.WorkspaceId != null)
            {
                Console.WriteLine($"   • Azure Monitor: Configured");
            }
            else
            {
                Console.WriteLine("⚠️  Warning: Azure Monitor not configured - performance analysis will be limited");
            }
            
            Console.WriteLine();
            return true;
        }

        private static bool ValidateCommandLineOptions(CommandLineOptions options)
        {
            // Check for conflicting flags
            if (options.AssessmentOnly && options.ProjectOnly)
            {
                Console.WriteLine("❌ Error: Cannot specify both --assessment-only and --project-only flags.");
                Console.WriteLine("   Use one or the other, or omit both for default behavior (generate both).");
                return false;
            }

            return true;
        }

        private static async Task<AssessmentResult> RunAssessmentAsync(
            IServiceProvider serviceProvider, 
            IConfiguration configuration,
            UserInputs userInputs,
            ILogger logger, 
            CancellationToken cancellationToken)
        {
            var assessmentResults = new List<AssessmentResult>();

            Console.WriteLine("🔍 Starting assessment...");
            Console.WriteLine();

            // Process each database
            foreach (var databaseName in userInputs.DatabaseNames)
            {
                Console.WriteLine($"📊 Analyzing database: {databaseName}");
                
                // Use appropriate service provider based on endpoint override
                IServiceProvider activeServiceProvider = serviceProvider;
                ServiceProvider? overrideServiceProvider = null;
                
                if (!string.IsNullOrEmpty(userInputs.AccountEndpoint) && 
                    userInputs.AccountEndpoint != configuration["CosmosDb:AccountEndpoint"])
                {
                    // Create a new configuration that overrides the endpoint
                    var configDict = new Dictionary<string, string>
                    {
                        ["CosmosDb:AccountEndpoint"] = userInputs.AccountEndpoint
                    };
                    var overrideConfig = new ConfigurationBuilder()
                        .AddInMemoryCollection(configDict!)
                        .AddConfiguration(configuration)
                        .Build();
                    
                    // Create override service provider
                    var overrideServices = ConfigureServices(overrideConfig);
                    overrideServiceProvider = overrideServices.BuildServiceProvider();
                    activeServiceProvider = overrideServiceProvider;
                }
                
                var assessmentResult = new AssessmentResult
                {
                    CosmosAccountName = ExtractAccountNameFromEndpoint(userInputs.AccountEndpoint),
                    DatabaseName = databaseName
                };

                // Step 1: Cosmos DB Analysis
                Console.WriteLine("📊 Phase 1: Analyzing Cosmos DB database...");
                var cosmosService = activeServiceProvider.GetRequiredService<CosmosDbAnalysisService>();
                
                try
                {
                    assessmentResult.CosmosAnalysis = await cosmosService.AnalyzeDatabaseAsync(databaseName, cancellationToken);
                    Console.WriteLine($"   ✅ Analyzed {assessmentResult.CosmosAnalysis.Containers.Count} containers");
                    
                    if (assessmentResult.CosmosAnalysis.MonitoringLimitations.Any())
                    {
                        Console.WriteLine($"   ⚠️  {assessmentResult.CosmosAnalysis.MonitoringLimitations.Count} monitoring limitations detected");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to analyze Cosmos DB");
                    throw new InvalidOperationException($"Cosmos DB analysis failed for database {databaseName}: {ex.Message}", ex);
                }

                // Step 2: SQL Migration Assessment
                Console.WriteLine();
                Console.WriteLine("🎯 Phase 2: Generating SQL migration assessment...");
                var sqlService = activeServiceProvider.GetRequiredService<SqlMigrationAssessmentService>();
                
                try
                {
                    assessmentResult.SqlAssessment = await sqlService.AssessMigrationAsync(assessmentResult.CosmosAnalysis, databaseName, cancellationToken);
                    Console.WriteLine($"   ✅ Recommended platform: {assessmentResult.SqlAssessment.RecommendedPlatform}");
                    Console.WriteLine($"   ✅ Generated {assessmentResult.SqlAssessment.IndexRecommendations.Count} index recommendations");
                    Console.WriteLine($"   ✅ Migration complexity: {assessmentResult.SqlAssessment.Complexity.OverallComplexity}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to assess SQL migration");
                    throw new InvalidOperationException($"SQL migration assessment failed for database {databaseName}: {ex.Message}", ex);
                }

                // Step 3: Data Factory Estimates
                Console.WriteLine();
                Console.WriteLine("⏱️  Phase 3: Calculating migration estimates...");
                var dataFactoryService = activeServiceProvider.GetRequiredService<DataFactoryEstimateService>();
                
                try
                {
                    assessmentResult.DataFactoryEstimate = await dataFactoryService.EstimateMigrationAsync(
                        assessmentResult.CosmosAnalysis, 
                        assessmentResult.SqlAssessment, 
                        cancellationToken);
                    Console.WriteLine($"   ✅ Estimated migration time: {assessmentResult.DataFactoryEstimate.EstimatedDuration:hh\\:mm\\:ss}");
                    Console.WriteLine($"   ✅ Estimated cost: ${assessmentResult.DataFactoryEstimate.EstimatedCostUSD:F2}");
                    Console.WriteLine($"   ✅ Recommended DIUs: {assessmentResult.DataFactoryEstimate.RecommendedDIUs}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to calculate Data Factory estimates");
                    throw new InvalidOperationException($"Data Factory estimation failed for database {databaseName}: {ex.Message}", ex);
                }

                assessmentResults.Add(assessmentResult);
                Console.WriteLine($"✅ Completed assessment for database: {databaseName}");
                Console.WriteLine();
                
                // Clean up override service provider if created
                overrideServiceProvider?.Dispose();
            }

            // Generate reports for each database separately (Excel) and combined (Word)
            if (assessmentResults.Count == 1)
            {
                return assessmentResults[0];
            }
            else
            {
                // Store individual results for separate Excel generation
                foreach (var result in assessmentResults)
                {
                    result.GenerateSeparateExcel = true;
                }

                // Create a combined assessment result for Word report
                var combinedResult = new AssessmentResult
                {
                    CosmosAccountName = assessmentResults[0].CosmosAccountName,
                    DatabaseName = $"Multiple Databases ({assessmentResults.Count})",
                    CosmosAnalysis = CombineCosmosAnalyses(assessmentResults.Select(r => r.CosmosAnalysis).ToList()),
                    SqlAssessment = CombineSqlAssessments(assessmentResults.Select(r => r.SqlAssessment).ToList()),
                    DataFactoryEstimate = CombineDataFactoryEstimates(assessmentResults.Select(r => r.DataFactoryEstimate).ToList()),
                    IndividualDatabaseResults = assessmentResults.ToList()
                };

                return combinedResult;
            }
        }

        private static async Task GenerateOutputsAsync(
            IServiceProvider serviceProvider, 
            AssessmentResult assessmentResult,
            string outputDirectory,
            CommandLineOptions options,
            ILogger logger, 
            CancellationToken cancellationToken)
        {
            Console.WriteLine();

            // Generate assessment reports (unless --project-only is specified)
            if (!options.ProjectOnly)
            {
                Console.WriteLine("📄 Phase 5a: Generating assessment reports...");
                
                var reportingService = serviceProvider.GetRequiredService<ReportGenerationService>();
                
                try
                {
                    var (excelPaths, wordPath, analysisFolderPath) = await reportingService.GenerateAssessmentReportAsync(assessmentResult, outputDirectory, cancellationToken);
                    
                    foreach (var excelPath in excelPaths)
                    {
                        Console.WriteLine($"   ✅ Excel report: {excelPath}");
                    }
                    Console.WriteLine($"   ✅ Word summary: {wordPath}");

                    // Store analysis folder path for SQL project generation
                    assessmentResult.AnalysisFolderPath = analysisFolderPath;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to generate assessment reports");
                    Console.WriteLine($"   ❌ Assessment report generation failed: {ex.Message}");
                    throw;
                }
            }

            // Generate SQL projects (unless --assessment-only is specified)
            if (!options.AssessmentOnly)
            {
                Console.WriteLine("🏗️  Phase 5b: Generating SQL database projects...");
                
                var sqlProjectService = serviceProvider.GetRequiredService<SqlProjectGenerationService>();
                
                try
                {
                    // Use the analysis folder path from report generation
                    var analysisFolderPath = !string.IsNullOrEmpty(assessmentResult.AnalysisFolderPath) 
                        ? assessmentResult.AnalysisFolderPath 
                        : outputDirectory;
                        
                    await sqlProjectService.GenerateSqlProjectsAsync(assessmentResult, analysisFolderPath, cancellationToken);
                    
                    foreach (var databaseMapping in assessmentResult.SqlAssessment.DatabaseMappings)
                    {
                        var projectName = $"{SanitizeName(databaseMapping.TargetDatabase)}.Database";
                        var projectPath = Path.Combine(analysisFolderPath, "sql-projects", projectName);
                        Console.WriteLine($"   ✅ SQL project: {projectPath}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to generate SQL projects");
                    Console.WriteLine($"   ❌ SQL project generation failed: {ex.Message}");
                    throw;
                }
            }
        }

        private static string SanitizeName(string name)
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

        private static async Task GenerateReportsAsync(
            IServiceProvider serviceProvider, 
            AssessmentResult assessmentResult,
            string outputDirectory,
            ILogger logger, 
            CancellationToken cancellationToken)
        {
            Console.WriteLine();
            Console.WriteLine("📄 Phase 5: Generating reports...");

            var reportingService = serviceProvider.GetRequiredService<ReportGenerationService>();
            
            try
            {
                var (excelPaths, wordPath, analysisFolderPath) = await reportingService.GenerateAssessmentReportAsync(assessmentResult, outputDirectory, cancellationToken);
                
                foreach (var excelPath in excelPaths)
                {
                    Console.WriteLine($"   ✅ Excel report: {excelPath}");
                }
                Console.WriteLine($"   ✅ Word summary: {wordPath}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate reports");
                Console.WriteLine($"   ❌ Report generation failed: {ex.Message}");
                throw;
            }
        }

        private static CosmosDbAnalysis CombineCosmosAnalyses(List<CosmosDbAnalysis> analyses)
        {
            var combined = new CosmosDbAnalysis
            {
                Containers = new List<ContainerAnalysis>(),
                MonitoringLimitations = new List<string>(),
                DatabaseMetrics = new DatabaseMetrics() // Initialize with default or combine appropriately
            };

            foreach (var analysis in analyses)
            {
                combined.Containers.AddRange(analysis.Containers);
                combined.MonitoringLimitations.AddRange(analysis.MonitoringLimitations);
            }

            return combined;
        }

        private static SqlMigrationAssessment CombineSqlAssessments(List<SqlMigrationAssessment> assessments)
        {
            var combined = new SqlMigrationAssessment
            {
                RecommendedPlatform = assessments.FirstOrDefault()?.RecommendedPlatform ?? "Azure SQL Database",
                IndexRecommendations = new List<IndexRecommendation>(),
                Complexity = new MigrationComplexity(),
                DatabaseMappings = new List<DatabaseMapping>()
            };

            foreach (var assessment in assessments)
            {
                combined.IndexRecommendations.AddRange(assessment.IndexRecommendations);
                combined.DatabaseMappings.AddRange(assessment.DatabaseMappings);
            }

            // Combine complexity - take the highest complexity level
            var complexities = assessments.Select(a => a.Complexity.OverallComplexity).ToList();
            if (complexities.Contains("High"))
                combined.Complexity.OverallComplexity = "High";
            else if (complexities.Contains("Medium"))
                combined.Complexity.OverallComplexity = "Medium";
            else
                combined.Complexity.OverallComplexity = "Low";

            return combined;
        }

        private static DataFactoryEstimate CombineDataFactoryEstimates(List<DataFactoryEstimate> estimates)
        {
            return new DataFactoryEstimate
            {
                EstimatedDuration = TimeSpan.FromMilliseconds(estimates.Sum(e => e.EstimatedDuration.TotalMilliseconds)),
                EstimatedCostUSD = estimates.Sum(e => e.EstimatedCostUSD),
                RecommendedDIUs = estimates.Max(e => e.RecommendedDIUs),
                RecommendedParallelCopies = estimates.Max(e => e.RecommendedParallelCopies),
                TotalDataSizeGB = estimates.Sum(e => e.TotalDataSizeGB),
                PipelineEstimates = estimates.SelectMany(e => e.PipelineEstimates).ToList(),
                Prerequisites = estimates.SelectMany(e => e.Prerequisites).Distinct().ToList(),
                Recommendations = estimates.SelectMany(e => e.Recommendations).Distinct().ToList()
            };
        }

        private static void DisplayCompletionMessage(AssessmentResult assessment, CommandLineOptions options)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("                Assessment Complete!");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("📋 Assessment Summary:");
            Console.WriteLine($"   • Database: {assessment.DatabaseName}");
            Console.WriteLine($"   • Containers: {assessment.CosmosAnalysis.Containers.Count}");
            Console.WriteLine($"   • Total Documents: {assessment.CosmosAnalysis.Containers.Sum(c => c.DocumentCount):N0}");
            Console.WriteLine($"   • Total Size: {assessment.CosmosAnalysis.Containers.Sum(c => c.SizeBytes) / (1024.0 * 1024.0 * 1024.0):F2} GB");
            Console.WriteLine();
            Console.WriteLine("🎯 Migration Recommendations:");
            Console.WriteLine($"   • Recommended Platform: {assessment.SqlAssessment.RecommendedPlatform}");
            Console.WriteLine($"   • Recommended Tier: {assessment.SqlAssessment.RecommendedTier}");
            Console.WriteLine($"   • Complexity: {assessment.SqlAssessment.Complexity.OverallComplexity}");
            Console.WriteLine($"   • Estimated Migration Days: {assessment.SqlAssessment.Complexity.EstimatedMigrationDays}");
            Console.WriteLine();
            Console.WriteLine("🏭 Data Factory Estimates:");
            Console.WriteLine($"   • Migration Duration: {assessment.DataFactoryEstimate.EstimatedDuration.TotalHours:F1} hours");
            Console.WriteLine($"   • Estimated Cost: ${assessment.DataFactoryEstimate.EstimatedCostUSD:F2}");
            Console.WriteLine($"   • Recommended DIUs: {assessment.DataFactoryEstimate.RecommendedDIUs}");
            Console.WriteLine();
            
            // Display generated outputs based on options
            Console.WriteLine("📊 Generated Outputs:");
            if (!options.ProjectOnly)
            {
                Console.WriteLine("   • Excel reports: Detailed analysis and recommendations");
                Console.WriteLine("   • Word summary: Executive overview for stakeholders");
            }
            if (!options.AssessmentOnly)
            {
                Console.WriteLine("   • SQL Database projects: Ready for SSDT/SqlPackage deployment");
                Console.WriteLine($"   • {assessment.SqlAssessment.DatabaseMappings.Count} project(s) in sql-projects/ directory");
            }
            Console.WriteLine();
            
            Console.WriteLine("📊 Next Steps:");
            if (!options.ProjectOnly)
            {
                Console.WriteLine("   1. Review the generated Excel report for detailed analysis");
                Console.WriteLine("   2. Share the Word document with stakeholders");
            }
            if (!options.AssessmentOnly)
            {
                Console.WriteLine("   3. Deploy SQL projects using Visual Studio or SqlPackage.exe");
                Console.WriteLine("   4. Test schema with sample data");
            }
            Console.WriteLine("   5. Plan migration based on complexity assessment");
            Console.WriteLine("   6. Provision target Azure SQL infrastructure");
            Console.WriteLine("   7. Execute proof-of-concept migration if complexity is high");
            Console.WriteLine();
        }

        private static List<RecommendationItem> GenerateOverallRecommendations(AssessmentResult assessment)
        {
            var recommendations = new List<RecommendationItem>();

            // Platform-specific recommendations
            var platformRec = new RecommendationItem
            {
                Category = "Platform",
                Priority = "High",
                Title = "Azure SQL Platform Selection",
                Description = $"Based on analysis, {assessment.SqlAssessment.RecommendedPlatform} is recommended for optimal cost-performance balance.",
                Impact = "Significant impact on migration complexity, cost, and ongoing operations",
                ActionItems = new List<string>
                {
                    $"Provision {assessment.SqlAssessment.RecommendedPlatform} with {assessment.SqlAssessment.RecommendedTier} tier",
                    "Configure networking and security settings",
                    "Set up monitoring and alerting"
                }
            };
            recommendations.Add(platformRec);

            // Complexity-based recommendations
            if (assessment.SqlAssessment.Complexity.OverallComplexity == "High")
            {
                var complexityRec = new RecommendationItem
                {
                    Category = "Migration Strategy",
                    Priority = "High",
                    Title = "High Complexity Migration Approach",
                    Description = "The migration has been assessed as high complexity, requiring careful planning and phased approach.",
                    Impact = "Risk mitigation for data integrity and minimal downtime",
                    ActionItems = new List<string>
                    {
                        "Conduct proof-of-concept with sample data",
                        "Plan phased migration approach",
                        "Prepare comprehensive rollback strategy",
                        "Schedule extended testing phase"
                    }
                };
                recommendations.Add(complexityRec);
            }

            // Performance recommendations
            if (assessment.DataFactoryEstimate.EstimatedDuration.TotalHours > 24)
            {
                var performanceRec = new RecommendationItem
                {
                    Category = "Performance",
                    Priority = "Medium",
                    Title = "Long Migration Duration Optimization",
                    Description = "Migration estimated to take over 24 hours. Consider optimization strategies.",
                    Impact = "Reduced migration window and business impact",
                    ActionItems = new List<string>
                    {
                        "Consider parallel migration of containers",
                        "Optimize Data Factory pipeline settings",
                        "Schedule migration during maintenance windows",
                        "Implement incremental migration strategy"
                    }
                };
                recommendations.Add(performanceRec);
            }

            // Cost optimization recommendations
            if (assessment.DataFactoryEstimate.EstimatedCostUSD > 500)
            {
                var costRec = new RecommendationItem
                {
                    Category = "Cost Optimization",
                    Priority = "Medium",
                    Title = "Migration Cost Optimization",
                    Description = "Migration costs are estimated to be significant. Consider optimization opportunities.",
                    Impact = "Reduced migration costs without compromising quality",
                    ActionItems = new List<string>
                    {
                        "Evaluate self-hosted integration runtime for large data volumes",
                        "Optimize pipeline parallel copy settings",
                        "Consider regional data placement strategy",
                        "Monitor and adjust DIU settings based on actual performance"
                    }
                };
                recommendations.Add(costRec);
            }

            // Index optimization recommendations
            var highPriorityIndexes = assessment.SqlAssessment.IndexRecommendations.Count(i => i.Priority <= 2);
            if (highPriorityIndexes > 0)
            {
                var indexRec = new RecommendationItem
                {
                    Category = "Performance",
                    Priority = "High",
                    Title = "Index Implementation Strategy",
                    Description = $"Implement {highPriorityIndexes} high-priority indexes for optimal query performance.",
                    Impact = "Significant improvement in query performance and user experience",
                    ActionItems = new List<string>
                    {
                        "Create clustered indexes on primary keys first",
                        "Implement partition key indexes",
                        "Add composite indexes based on query patterns",
                        "Monitor index usage and performance impact"
                    }
                };
                recommendations.Add(indexRec);
            }

            // Monitoring recommendations
            if (assessment.CosmosAnalysis.MonitoringLimitations.Any())
            {
                var monitoringRec = new RecommendationItem
                {
                    Category = "Monitoring",
                    Priority = "Medium",
                    Title = "Enhanced Monitoring Setup",
                    Description = "Limited monitoring data was available during assessment. Enhance monitoring for future optimization.",
                    Impact = "Better insights for ongoing optimization and troubleshooting",
                    ActionItems = new List<string>
                    {
                        "Configure Application Insights for Cosmos DB",
                        "Set up Log Analytics workspace",
                        "Implement custom monitoring dashboards",
                        "Create performance baselines for comparison"
                    }
                };
                recommendations.Add(monitoringRec);
            }

            return recommendations;
        }

        private static string ExtractAccountNameFromEndpoint(string? endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                return "Unknown";

            try
            {
                var uri = new Uri(endpoint);
                return uri.Host.Split('.')[0];
            }
            catch
            {
                return "Unknown";
            }
        }

        private static CommandLineOptions? ParseCommandLineArguments(string[] args)
        {
            var options = new CommandLineOptions();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--help":
                    case "-h":
                        DisplayHelp();
                        return null;
                    case "--all-databases":
                    case "-a":
                        options.AnalyzeAllDatabases = true;
                        break;
                    case "--database":
                    case "-d":
                        if (i + 1 < args.Length)
                        {
                            options.DatabaseName = args[++i];
                        }
                        break;
                    case "--output":
                    case "-o":
                        if (i + 1 < args.Length)
                        {
                            options.OutputDirectory = args[++i];
                        }
                        break;
                    case "--auto-discover":
                        options.AutoDiscoverMonitoring = true;
                        break;
                    case "--endpoint":
                    case "-e":
                        if (i + 1 < args.Length)
                        {
                            options.AccountEndpoint = args[++i];
                        }
                        break;
                    case "--workspace-id":
                    case "-w":
                        if (i + 1 < args.Length)
                        {
                            options.WorkspaceId = args[++i];
                        }
                        break;
                    case "--assessment-only":
                        options.AssessmentOnly = true;
                        break;
                    case "--project-only":
                        options.ProjectOnly = true;
                        break;
                    default:
                        Console.WriteLine($"Unknown argument: {args[i]}");
                        DisplayHelp();
                        return null;
                }
            }

            return options;
        }

        private static void DisplayHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Cosmos DB to SQL Migration Assessment Tool");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  CosmosToSqlAssessment [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -h, --help                Show this help message");
            Console.WriteLine("  -a, --all-databases       Analyze all databases in the Cosmos DB account");
            Console.WriteLine("  -d, --database <name>     Analyze specific database (overrides config)");
            Console.WriteLine("  -e, --endpoint <url>      Cosmos DB account endpoint (overrides config)");
            Console.WriteLine("  -w, --workspace-id <id>   Log Analytics workspace ID for performance metrics");
            Console.WriteLine("  -o, --output <path>       Output directory for reports (will prompt if not specified)");
            Console.WriteLine("  --auto-discover           Automatically discover Azure Monitor settings");
            Console.WriteLine("  --assessment-only         Generate assessment reports only (skip SQL project generation)");
            Console.WriteLine("  --project-only            Generate SQL projects only (skip assessment reports)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  CosmosToSqlAssessment --all-databases");
            Console.WriteLine("  CosmosToSqlAssessment --database MyDatabase --output C:\\Reports");
            Console.WriteLine("  CosmosToSqlAssessment --endpoint https://myaccount.documents.azure.com:443/");
            Console.WriteLine("  CosmosToSqlAssessment --endpoint https://myaccount.documents.azure.com:443/ --all-databases");
            Console.WriteLine("  CosmosToSqlAssessment --workspace-id 12345678-1234-1234-1234-123456789012 --all-databases");
            Console.WriteLine("  CosmosToSqlAssessment --auto-discover");
            Console.WriteLine("  CosmosToSqlAssessment --assessment-only --database MyDatabase");
            Console.WriteLine("  CosmosToSqlAssessment --project-only --all-databases");
            Console.WriteLine();
        }

        private static async Task<UserInputs?> GetUserInputsAsync(IConfiguration configuration, CommandLineOptions options, ILogger logger)
        {
            var inputs = new UserInputs();

            try
            {
                // Determine account endpoint (command line overrides configuration)
                inputs.AccountEndpoint = !string.IsNullOrEmpty(options.AccountEndpoint) 
                    ? options.AccountEndpoint 
                    : configuration["CosmosDb:AccountEndpoint"] ?? string.Empty;

                if (string.IsNullOrEmpty(inputs.AccountEndpoint))
                {
                    Console.WriteLine("❌ Cosmos DB account endpoint not specified.");
                    Console.WriteLine("   Use --endpoint <url> or configure in appsettings.json");
                    Console.WriteLine();
                    DisplayHelp();
                    return null;
                }

                Console.WriteLine($"🔗 Using Cosmos DB endpoint: {inputs.AccountEndpoint}");

                // Determine databases to analyze
                if (options.AnalyzeAllDatabases)
                {
                    Console.WriteLine("🔍 Discovering all databases in Cosmos DB account...");
                    inputs.DatabaseNames = await DiscoverAllDatabasesAsync(inputs.AccountEndpoint, logger);
                    
                    if (!inputs.DatabaseNames.Any())
                    {
                        Console.WriteLine("❌ No databases found in the Cosmos DB account.");
                        return null;
                    }

                    Console.WriteLine($"✅ Found {inputs.DatabaseNames.Count} databases:");
                    foreach (var db in inputs.DatabaseNames)
                    {
                        Console.WriteLine($"   • {db}");
                    }
                    Console.WriteLine();
                }
                else if (!string.IsNullOrEmpty(options.DatabaseName))
                {
                    inputs.DatabaseNames = new List<string> { options.DatabaseName };
                }
                else
                {
                    var configuredDatabase = configuration["CosmosDb:DatabaseName"];
                    if (!string.IsNullOrEmpty(configuredDatabase))
                    {
                        inputs.DatabaseNames = new List<string> { configuredDatabase };
                    }
                    else
                    {
                        Console.WriteLine("❌ No database specified. Use --database <name> or configure in appsettings.json");
                        return null;
                    }
                }

                // Determine output directory
                var outputDir = GetOutputDirectoryAsync(configuration, options);
                if (string.IsNullOrEmpty(outputDir))
                {
                    Console.WriteLine("❌ No output directory specified.");
                    return null;
                }
                inputs.OutputDirectory = outputDir;

                // Auto-discover Azure Monitor if requested
                if (options.AutoDiscoverMonitoring || configuration.GetValue<bool>("AzureMonitor:AutoDiscover"))
                {
                    Console.WriteLine("🔍 Auto-discovering Azure Monitor settings...");
                    inputs.MonitoringConfig = await AutoDiscoverMonitoringAsync(configuration, logger);
                }

                return inputs;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get user inputs");
                Console.WriteLine($"❌ Error getting user inputs: {ex.Message}");
                return null;
            }
        }

        private static async Task<List<string>> DiscoverAllDatabasesAsync(string cosmosEndpoint, ILogger logger)
        {
            var databases = new List<string>();

            try
            {
                var credential = new DefaultAzureCredential();
                
                if (string.IsNullOrEmpty(cosmosEndpoint))
                {
                    throw new ArgumentException("Cosmos DB account endpoint not configured");
                }

                using var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

                var iterator = cosmosClient.GetDatabaseQueryIterator<Microsoft.Azure.Cosmos.DatabaseProperties>();
                
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    foreach (var db in response)
                    {
                        databases.Add(db.Id);
                    }
                }

                logger.LogInformation("Discovered {DatabaseCount} databases", databases.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to discover databases");
                throw;
            }

            return databases;
        }

        private static string? GetOutputDirectoryAsync(IConfiguration configuration, CommandLineOptions options)
        {
            // Use command line option if provided
            if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                return Path.GetFullPath(options.OutputDirectory);
            }

            // Use configuration if available and not prompting
            var configDirectory = configuration["Reports:OutputDirectory"];
            var promptForDirectory = configuration.GetValue<bool>("Reports:PromptForOutputDirectory");

            if (!string.IsNullOrEmpty(configDirectory) && !promptForDirectory)
            {
                return Path.GetFullPath(configDirectory);
            }

            // Prompt user for directory
            Console.WriteLine("📁 Please specify the output directory for reports:");
            Console.WriteLine("   (Press Enter to use default: ./CosmosAssessment_{timestamp})");
            Console.Write("   Output Directory: ");

            var userInput = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(userInput))
            {
                // Use default pattern
                var pattern = configuration["Reports:DefaultDirectoryPattern"] ?? "CosmosAssessment_{DateTime:yyyyMMdd_HHmmss}";
                var defaultDir = pattern.Replace("{DateTime:yyyyMMdd_HHmmss}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                userInput = Path.Combine(Environment.CurrentDirectory, defaultDir);
            }

            var fullPath = Path.GetFullPath(userInput);

            // Create directory if it doesn't exist
            try
            {
                Directory.CreateDirectory(fullPath);
                Console.WriteLine($"✅ Output directory: {fullPath}");
                return fullPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to create directory '{fullPath}': {ex.Message}");
                return null;
            }
        }

        private static Task<MonitoringConfiguration?> AutoDiscoverMonitoringAsync(IConfiguration configuration, ILogger logger)
        {
            try
            {
                // This is a simplified version - in a real implementation, you would use Azure Resource Manager APIs
                // to discover Log Analytics workspaces associated with the Cosmos DB account
                
                logger.LogInformation("Auto-discovery of monitoring settings is not fully implemented yet");
                Console.WriteLine("⚠️  Auto-discovery of Azure Monitor settings is not yet implemented.");
                Console.WriteLine("   Using configuration file or default settings.");
                
                return Task.FromResult<MonitoringConfiguration?>(null);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to auto-discover monitoring settings");
                return Task.FromResult<MonitoringConfiguration?>(null);
            }
        }
    }

    public class CommandLineOptions
    {
        public bool AnalyzeAllDatabases { get; set; }
        public string? DatabaseName { get; set; }
        public string? OutputDirectory { get; set; }
        public bool AutoDiscoverMonitoring { get; set; }
        public string? AccountEndpoint { get; set; }
        public string? WorkspaceId { get; set; }
        public bool AssessmentOnly { get; set; }
        public bool ProjectOnly { get; set; }
    }

    public class UserInputs
    {
        public List<string> DatabaseNames { get; set; } = new();
        public string OutputDirectory { get; set; } = string.Empty;
        public string AccountEndpoint { get; set; } = string.Empty;
        public MonitoringConfiguration? MonitoringConfig { get; set; }
    }

    public class MonitoringConfiguration
    {
        public string? WorkspaceId { get; set; }
        public string? SubscriptionId { get; set; }
        public string? ResourceGroupName { get; set; }
    }
}
