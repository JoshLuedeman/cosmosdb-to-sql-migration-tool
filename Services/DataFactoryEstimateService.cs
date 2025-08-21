using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for estimating Azure Data Factory migration time and costs
    /// Implements Azure best practices for data movement and transformation
    /// </summary>
    public class DataFactoryEstimateService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DataFactoryEstimateService> _logger;

        // Azure Data Factory pricing constants (as of 2025)
        private const decimal PipelineActivityCostUSD = 0.001m; // Per 1,000 activities
        private const decimal DataIntegrationUnitHourCostUSD = 0.274m; // Per DIU-hour
        private const decimal ExternalPipelineActivityCostUSD = 0.00025m; // Per activity

        public DataFactoryEstimateService(IConfiguration configuration, ILogger<DataFactoryEstimateService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Estimates Azure Data Factory migration time and costs based on Cosmos DB analysis
        /// Reference: https://docs.microsoft.com/azure/data-factory/copy-activity-performance
        /// </summary>
        public async Task<DataFactoryEstimate> EstimateMigrationAsync(
            CosmosDbAnalysis cosmosAnalysis, 
            SqlMigrationAssessment sqlAssessment, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting Data Factory migration estimation for {ContainerCount} containers", 
                cosmosAnalysis.Containers.Count);

            var estimate = new DataFactoryEstimate();

            try
            {
                // Calculate total data size
                estimate.TotalDataSizeGB = CalculateTotalDataSize(cosmosAnalysis);

                // Determine optimal DIU and parallel copy settings
                var (recommendedDIUs, recommendedParallelCopies) = CalculateOptimalSettings(cosmosAnalysis);
                estimate.RecommendedDIUs = recommendedDIUs;
                estimate.RecommendedParallelCopies = recommendedParallelCopies;

                // Estimate migration time for each container
                estimate.PipelineEstimates = await EstimatePipelinePerformanceAsync(cosmosAnalysis, sqlAssessment, recommendedDIUs, recommendedParallelCopies);

                // Calculate total duration and cost
                estimate.EstimatedDuration = TimeSpan.FromMinutes(estimate.PipelineEstimates.Sum(p => p.EstimatedDuration.TotalMinutes));
                estimate.EstimatedCostUSD = CalculateTotalCost(estimate);

                // Generate recommendations and prerequisites
                estimate.Recommendations = GenerateRecommendations(cosmosAnalysis, estimate);
                estimate.Prerequisites = GeneratePrerequisites(cosmosAnalysis, sqlAssessment);

                _logger.LogInformation("Data Factory estimation completed: {TotalSizeGB:F2} GB, {EstimatedHours:F1} hours, ${EstimatedCost:F2}", 
                    estimate.TotalDataSizeGB, estimate.EstimatedDuration.TotalHours, estimate.EstimatedCostUSD);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Data Factory estimation");
                throw;
            }

            return estimate;
        }

        private long CalculateTotalDataSize(CosmosDbAnalysis analysis)
        {
            var totalBytes = analysis.Containers.Sum(c => c.SizeBytes);
            var totalGB = totalBytes / (1024.0 * 1024.0 * 1024.0);
            
            _logger.LogInformation("Total data size calculated: {TotalGB:F2} GB from {ContainerCount} containers", 
                totalGB, analysis.Containers.Count);
            
            return (long)Math.Ceiling(totalGB);
        }

        /// <summary>
        /// Calculates optimal DIU and parallel copy settings based on data characteristics
        /// Reference: https://docs.microsoft.com/azure/data-factory/copy-activity-performance-features
        /// </summary>
        private (int DIUs, int ParallelCopies) CalculateOptimalSettings(CosmosDbAnalysis analysis)
        {
            var totalSizeGB = CalculateTotalDataSize(analysis);
            var maxContainerSizeGB = analysis.Containers.Max(c => c.SizeBytes / (1024.0 * 1024.0 * 1024.0));
            var averageDocumentSizeKB = analysis.Containers.Any() 
                ? analysis.Containers.Average(c => c.DocumentCount > 0 ? c.SizeBytes / (1024.0 * c.DocumentCount) : 0)
                : 0;

            // Configure DIUs based on data size and complexity
            // Small datasets (< 1GB): 2-4 DIUs
            // Medium datasets (1-100GB): 4-16 DIUs  
            // Large datasets (> 100GB): 16-256 DIUs
            int recommendedDIUs;
            if (totalSizeGB <= 1)
            {
                recommendedDIUs = 2;
            }
            else if (totalSizeGB <= 10)
            {
                recommendedDIUs = 4;
            }
            else if (totalSizeGB <= 100)
            {
                recommendedDIUs = 8;
            }
            else if (totalSizeGB <= 1000)
            {
                recommendedDIUs = 16;
            }
            else
            {
                recommendedDIUs = 32;
            }

            // Configure parallel copies based on source characteristics
            // Consider Cosmos DB RU limits and SQL target capacity
            var configuredParallelCopies = _configuration.GetValue<int>("DataFactory:EstimateParallelCopies", 4);
            int recommendedParallelCopies;

            if (analysis.Containers.Count <= 2)
            {
                recommendedParallelCopies = Math.Min(configuredParallelCopies, 2);
            }
            else if (analysis.Containers.Count <= 10)
            {
                recommendedParallelCopies = Math.Min(configuredParallelCopies, 4);
            }
            else
            {
                recommendedParallelCopies = Math.Min(configuredParallelCopies, 8);
            }

            // Adjust based on document size - smaller documents benefit from more parallelism
            if (averageDocumentSizeKB < 1) // Very small documents
            {
                recommendedParallelCopies = Math.Min(recommendedParallelCopies * 2, 16);
            }
            else if (averageDocumentSizeKB > 100) // Large documents
            {
                recommendedParallelCopies = Math.Max(recommendedParallelCopies / 2, 1);
            }

            _logger.LogInformation("Recommended settings: {DIUs} DIUs, {ParallelCopies} parallel copies " +
                                 "(Data size: {TotalSizeGB} GB, Avg doc size: {AvgDocSizeKB:F1} KB)", 
                recommendedDIUs, recommendedParallelCopies, totalSizeGB, averageDocumentSizeKB);

            return (recommendedDIUs, recommendedParallelCopies);
        }

        private Task<List<PipelineEstimate>> EstimatePipelinePerformanceAsync(
            CosmosDbAnalysis cosmosAnalysis, 
            SqlMigrationAssessment sqlAssessment,
            int recommendedDIUs, 
            int recommendedParallelCopies)
        {
            var estimates = new List<PipelineEstimate>();
            var networkBandwidthMbps = _configuration.GetValue<double>("DataFactory:NetworkBandwidthMbps", 1000);
            var sourceRegion = _configuration.GetValue<string>("DataFactory:SourceRegion", "East US");
            var targetRegion = _configuration.GetValue<string>("DataFactory:TargetRegion", "East US");

            // Apply regional latency factor
            var regionalLatencyFactor = string.Equals(sourceRegion, targetRegion, StringComparison.OrdinalIgnoreCase) ? 1.0 : 1.2;

            foreach (var container in cosmosAnalysis.Containers)
            {
                var containerMapping = sqlAssessment.DatabaseMappings
                    .SelectMany(dm => dm.ContainerMappings)
                    .FirstOrDefault(cm => cm.SourceContainer == container.ContainerName);

                var estimate = new PipelineEstimate
                {
                    SourceContainer = container.ContainerName,
                    TargetTable = containerMapping?.TargetTable ?? SanitizeTableName(container.ContainerName),
                    DataSizeGB = (long)(container.SizeBytes / (1024.0 * 1024.0 * 1024.0))
                };

                // Determine if transformation is required
                estimate.RequiresTransformation = containerMapping?.RequiredTransformations.Any() ?? false;
                if (estimate.RequiresTransformation)
                {
                    var transformationCount = containerMapping?.RequiredTransformations.Count ?? 0;
                    estimate.TransformationComplexity = transformationCount switch
                    {
                        <= 2 => "Low",
                        <= 5 => "Medium",
                        _ => "High"
                    };
                }
                else
                {
                    estimate.TransformationComplexity = "None";
                }

                // Calculate transfer time based on multiple factors
                var transferTimeMinutes = CalculateTransferTime(
                    container, 
                    networkBandwidthMbps, 
                    recommendedDIUs, 
                    recommendedParallelCopies, 
                    regionalLatencyFactor,
                    estimate.RequiresTransformation);

                estimate.EstimatedDuration = TimeSpan.FromMinutes(transferTimeMinutes);

                estimates.Add(estimate);

                _logger.LogInformation("Container {ContainerName}: {DataSizeGB} GB, {DurationMinutes:F1} minutes, " +
                                     "Transformation: {TransformationComplexity}", 
                    container.ContainerName, estimate.DataSizeGB, transferTimeMinutes, estimate.TransformationComplexity);
            }

            return Task.FromResult(estimates);
        }

        private double CalculateTransferTime(
            ContainerAnalysis container, 
            double networkBandwidthMbps, 
            int dius, 
            int parallelCopies, 
            double regionalLatencyFactor,
            bool requiresTransformation)
        {
            var dataSizeGB = container.SizeBytes / (1024.0 * 1024.0 * 1024.0);
            var documentCount = container.DocumentCount;

            // Base transfer rate calculation
            // DIU provides approximately 20-80 MBps depending on source/sink types
            var baseTransferRateMBps = dius * 40.0; // Conservative estimate for Cosmos to SQL
            
            // Apply parallel copy factor (diminishing returns)
            var parallelEfficiency = Math.Min(1.0, 0.5 + (parallelCopies * 0.1));
            var effectiveTransferRateMBps = baseTransferRateMBps * parallelEfficiency;

            // Consider network bandwidth limitation
            var networkLimitMBps = networkBandwidthMbps / 8.0; // Convert Mbps to MBps
            effectiveTransferRateMBps = Math.Min(effectiveTransferRateMBps, networkLimitMBps);

            // Document size factor - many small documents have overhead
            var avgDocumentSizeKB = documentCount > 0 ? (container.SizeBytes / 1024.0) / documentCount : 0;
            var documentSizeFactor = 1.0;
            
            if (avgDocumentSizeKB < 1) // Very small documents
            {
                documentSizeFactor = 1.5; // More overhead per MB
            }
            else if (avgDocumentSizeKB < 10) // Small documents
            {
                documentSizeFactor = 1.2;
            }
            else if (avgDocumentSizeKB > 1000) // Large documents
            {
                documentSizeFactor = 0.9; // More efficient per MB
            }

            // Transformation overhead
            var transformationFactor = requiresTransformation ? 1.8 : 1.0;

            // Regional latency factor
            var totalLatencyFactor = regionalLatencyFactor * documentSizeFactor * transformationFactor;

            // Calculate time in minutes
            var dataSizeMB = dataSizeGB * 1024.0;
            var transferTimeMinutes = (dataSizeMB / effectiveTransferRateMBps) * totalLatencyFactor / 60.0;

            // Add setup overhead (1-5 minutes depending on complexity)
            var setupOverheadMinutes = requiresTransformation ? 5.0 : 2.0;
            transferTimeMinutes += setupOverheadMinutes;

            // Minimum time for small datasets
            transferTimeMinutes = Math.Max(transferTimeMinutes, 1.0);

            return transferTimeMinutes;
        }

        private decimal CalculateTotalCost(DataFactoryEstimate estimate)
        {
            decimal totalCost = 0;

            // Calculate pipeline execution costs
            var totalActivities = estimate.PipelineEstimates.Count * 2; // Copy + monitoring activities
            var pipelineCost = (totalActivities / 1000m) * PipelineActivityCostUSD;

            // Calculate DIU costs
            var totalDIUHours = (decimal)estimate.EstimatedDuration.TotalHours * estimate.RecommendedDIUs;
            var diuCost = totalDIUHours * DataIntegrationUnitHourCostUSD;

            // Calculate external activity costs (if any transformations)
            var transformationActivities = estimate.PipelineEstimates.Count(p => p.RequiresTransformation) * 2;
            var externalActivityCost = transformationActivities * ExternalPipelineActivityCostUSD;

            totalCost = pipelineCost + diuCost + externalActivityCost;

            _logger.LogInformation("Cost breakdown: Pipeline: ${PipelineCost:F2}, DIU: ${DIUCost:F2}, " +
                                 "External: ${ExternalCost:F2}, Total: ${TotalCost:F2}", 
                pipelineCost, diuCost, externalActivityCost, totalCost);

            return Math.Round(totalCost, 2);
        }

        private List<string> GenerateRecommendations(CosmosDbAnalysis analysis, DataFactoryEstimate estimate)
        {
            var recommendations = new List<string>();

            // Performance recommendations
            if (estimate.EstimatedDuration.TotalHours > 24)
            {
                recommendations.Add("Consider breaking migration into smaller batches to reduce individual pipeline runtime");
                recommendations.Add("Schedule migration during off-peak hours to minimize impact on source system");
            }

            if (estimate.TotalDataSizeGB > 1000)
            {
                recommendations.Add("Use incremental data loading strategy to minimize migration window");
                recommendations.Add("Consider using Azure Data Factory mapping data flows for complex transformations");
            }

            // Cost optimization recommendations
            if (estimate.EstimatedCostUSD > 1000)
            {
                recommendations.Add("Consider using self-hosted integration runtime for cost optimization on large datasets");
                recommendations.Add("Optimize DIU usage by monitoring actual performance and adjusting accordingly");
            }

            // Source-specific recommendations
            var highThroughputContainers = analysis.Containers.Where(c => c.Performance.PeakRUConsumption > 10000).ToList();
            if (highThroughputContainers.Any())
            {
                recommendations.Add("Monitor Cosmos DB RU consumption during migration to avoid throttling");
                recommendations.Add("Consider temporarily scaling up Cosmos DB throughput during migration");
            }

            // Network recommendations
            recommendations.Add("Ensure network connectivity between source Cosmos DB and target SQL database");
            recommendations.Add("Use Azure ExpressRoute or VPN for secure and reliable data transfer");

            // Monitoring recommendations
            recommendations.Add("Enable Azure Data Factory monitoring and alerting for migration pipeline");
            recommendations.Add("Set up data validation checks to ensure data integrity during migration");

            return recommendations;
        }

        private List<string> GeneratePrerequisites(CosmosDbAnalysis analysis, SqlMigrationAssessment assessment)
        {
            var prerequisites = new List<string>();

            // Infrastructure prerequisites
            prerequisites.Add("Provision target Azure SQL Database with appropriate service tier");
            prerequisites.Add("Create Azure Data Factory instance in the same region as source data");
            prerequisites.Add("Configure network connectivity between all components");

            // Security prerequisites
            prerequisites.Add("Configure managed identity for Azure Data Factory");
            prerequisites.Add("Grant Data Factory appropriate permissions to Cosmos DB (Cosmos DB Data Reader)");
            prerequisites.Add("Grant Data Factory appropriate permissions to SQL Database (db_datawriter)");

            // Database prerequisites
            foreach (var dbMapping in assessment.DatabaseMappings)
            {
                prerequisites.Add($"Create target database: {dbMapping.TargetDatabase}");
                
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    prerequisites.Add($"Create target table: {containerMapping.TargetSchema}.{containerMapping.TargetTable}");
                }
            }

            // Performance prerequisites
            if (analysis.PerformanceMetrics.PeakRUsPerSecond > 50000)
            {
                prerequisites.Add("Consider temporarily increasing Cosmos DB throughput during migration");
            }

            if (assessment.Complexity.OverallComplexity == "High")
            {
                prerequisites.Add("Conduct proof-of-concept migration with sample data");
                prerequisites.Add("Prepare rollback strategy and procedures");
            }

            // Monitoring prerequisites
            prerequisites.Add("Set up monitoring and alerting for migration pipeline");
            prerequisites.Add("Prepare data validation queries for target database");
            prerequisites.Add("Create migration status dashboard for stakeholder communication");

            return prerequisites;
        }

        private string SanitizeTableName(string name)
        {
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (char.IsDigit(sanitized.FirstOrDefault()))
            {
                sanitized = "Table_" + sanitized;
            }
            return string.IsNullOrEmpty(sanitized) ? "UnnamedTable" : sanitized;
        }
    }
}
