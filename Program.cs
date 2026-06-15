using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.Orchestration;

namespace CosmosToSqlAssessment
{
    /// <summary>
    /// Cosmos DB to SQL Migration Assessment Tool entry point.
    /// Parses CLI args, builds configuration + DI, hands off to <see cref="AssessmentOrchestrator"/>.
    /// </summary>
    internal class Program
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
                var options = CliArgumentParser.Parse(args);
                if (options == null)
                {
                    return 1; // Help was displayed or invalid arguments
                }

                // Validate command line options
                if (!CliArgumentParser.Validate(options))
                {
                    return 1;
                }

                // Build configuration
                var configuration = BuildConfiguration(options);

                // Setup dependency injection
                var services = new ServiceCollection().AddCosmosAssessment(configuration);
                using var serviceProvider = services.BuildServiceProvider();

                // Resolve and run the orchestrator inside a fresh scope so scoped
                // services (including the orchestrator itself) get correct lifetimes.
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();
                return await orchestrator.RunAsync(options, _cancellationTokenSource.Token);
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
                    var services = new ServiceCollection().AddCosmosAssessment(configuration);
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

        internal static IConfiguration BuildConfiguration(CliOptions? options = null)
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

        // ---------------------------------------------------------------------
        // Pre-existing dead-code methods retained here pending cleanup in
        // sub-issue #189 (Program.cs reduction to entry point only).
        // ValidateConfiguration / GenerateReportsAsync / GenerateOverallRecommendations
        // are unreferenced; flagged during the #187 refactor; removal deferred so
        // that this slice is a pure structural extraction.
        // ---------------------------------------------------------------------

        private static bool ValidateConfiguration(IConfiguration configuration, ILogger logger)
        {
            var isValid = true;
            var validationErrors = new List<string>();

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

            var workspaceId = configuration["AzureMonitor:WorkspaceId"];
            if (string.IsNullOrEmpty(workspaceId))
            {
                logger.LogWarning("Azure Monitor workspace ID not configured. Performance metrics will be limited.");
                Console.WriteLine("⚠️  Warning: Azure Monitor not configured - performance analysis will be limited");
            }
            else
            {
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

        private static async Task GenerateReportsAsync(
            IServiceProvider serviceProvider,
            CosmosToSqlAssessment.Models.AssessmentResult assessmentResult,
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

        private static List<CosmosToSqlAssessment.Models.RecommendationItem> GenerateOverallRecommendations(CosmosToSqlAssessment.Models.AssessmentResult assessment)
        {
            var recommendations = new List<CosmosToSqlAssessment.Models.RecommendationItem>();

            var platformRec = new CosmosToSqlAssessment.Models.RecommendationItem
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

            if (assessment.SqlAssessment.Complexity.OverallComplexity == "High")
            {
                var complexityRec = new CosmosToSqlAssessment.Models.RecommendationItem
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

            if (assessment.DataFactoryEstimate.EstimatedDuration.TotalHours > 24)
            {
                var performanceRec = new CosmosToSqlAssessment.Models.RecommendationItem
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

            if (assessment.DataFactoryEstimate.EstimatedCostUSD > 500)
            {
                var costRec = new CosmosToSqlAssessment.Models.RecommendationItem
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

            var highPriorityIndexes = assessment.SqlAssessment.IndexRecommendations.Count(i => i.Priority <= 2);
            if (highPriorityIndexes > 0)
            {
                var indexRec = new CosmosToSqlAssessment.Models.RecommendationItem
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

            if (assessment.CosmosAnalysis.MonitoringLimitations.Any())
            {
                var monitoringRec = new CosmosToSqlAssessment.Models.RecommendationItem
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
    }
}
