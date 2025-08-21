using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for assessing Cosmos DB to SQL migration requirements and recommendations
    /// Implements Azure best practices for SQL platform selection
    /// </summary>
    public class SqlMigrationAssessmentService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SqlMigrationAssessmentService> _logger;

        public SqlMigrationAssessmentService(IConfiguration configuration, ILogger<SqlMigrationAssessmentService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Performs comprehensive SQL migration assessment based on Cosmos DB analysis
        /// Following Azure Well-Architected Framework principles
        /// </summary>
        public Task<SqlMigrationAssessment> AssessMigrationAsync(CosmosDbAnalysis cosmosAnalysis, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting SQL migration assessment for {ContainerCount} containers", cosmosAnalysis.Containers.Count);

            var assessment = new SqlMigrationAssessment();

            try
            {
                // Recommend Azure SQL platform based on best practices
                assessment.RecommendedPlatform = RecommendAzureSqlPlatform(cosmosAnalysis);
                assessment.RecommendedTier = RecommendServiceTier(cosmosAnalysis);

                // Create database and container mappings
                assessment.DatabaseMappings = CreateDatabaseMappings(cosmosAnalysis);

                // Generate index recommendations based on usage patterns
                assessment.IndexRecommendations = GenerateIndexRecommendations(cosmosAnalysis);

                // Assess migration complexity
                assessment.Complexity = AssessMigrationComplexity(cosmosAnalysis);

                // Define transformation rules
                assessment.TransformationRules = DefineTransformationRules(cosmosAnalysis);

                _logger.LogInformation("SQL migration assessment completed. Recommended platform: {Platform}, Tier: {Tier}", 
                    assessment.RecommendedPlatform, assessment.RecommendedTier);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during SQL migration assessment");
                throw;
            }

            return Task.FromResult(assessment);
        }

        /// <summary>
        /// Recommends Azure SQL platform based on Azure best practices and workload characteristics
        /// Reference: https://docs.microsoft.com/azure/azure-sql/azure-sql-iaas-vs-paas-what-is-overview
        /// </summary>
        private string RecommendAzureSqlPlatform(CosmosDbAnalysis analysis)
        {
            var totalDocuments = analysis.Containers.Sum(c => c.DocumentCount);
            var totalSizeGB = analysis.Containers.Sum(c => c.SizeBytes) / (1024.0 * 1024.0 * 1024.0);
            var complexSchemas = analysis.Containers.Count(c => c.DetectedSchemas.Any(s => s.Fields.Values.Any(f => f.IsNested)));
            var hasComplexIndexing = analysis.Containers.Any(c => c.IndexingPolicy.CompositeIndexes.Count > 5);

            _logger.LogInformation("Platform recommendation factors: {TotalDocs} docs, {TotalSizeGB:F2} GB, {ComplexSchemas} complex schemas", 
                totalDocuments, totalSizeGB, complexSchemas);

            // Azure SQL Database - Best for cloud-native applications, managed service
            if (totalSizeGB <= 4000 && !hasComplexIndexing && totalDocuments <= 100_000_000)
            {
                _logger.LogInformation("Recommending Azure SQL Database for cloud-native, managed experience");
                return "AzureSqlDatabase";
            }

            // Azure SQL Managed Instance - Best for lift-and-shift scenarios with full SQL Server compatibility
            if (complexSchemas > analysis.Containers.Count * 0.5 || hasComplexIndexing || totalSizeGB > 4000)
            {
                _logger.LogInformation("Recommending Azure SQL Managed Instance for complex workload requirements");
                return "AzureSqlManagedInstance";
            }

            // SQL Server on Azure VMs - Best for maximum control and hybrid scenarios
            if (totalSizeGB > 16000 || totalDocuments > 1_000_000_000)
            {
                _logger.LogInformation("Recommending SQL Server on Azure VMs for enterprise-scale requirements");
                return "SqlOnAzureVm";
            }

            // Default to Azure SQL Database for most scenarios
            _logger.LogInformation("Defaulting to Azure SQL Database recommendation");
            return "AzureSqlDatabase";
        }

        /// <summary>
        /// Recommends service tier based on performance metrics and best practices
        /// </summary>
        private string RecommendServiceTier(CosmosDbAnalysis analysis)
        {
            var peakRUs = analysis.PerformanceMetrics.PeakRUsPerSecond;
            var avgRUs = analysis.PerformanceMetrics.AverageRUsPerSecond;
            var totalSizeGB = analysis.Containers.Sum(c => c.SizeBytes) / (1024.0 * 1024.0 * 1024.0);

            // Map RU consumption to DTU/vCore recommendations
            // 1 RU ≈ 0.02 DTU (rough estimation)
            var estimatedDTUs = Math.Max(peakRUs * 0.02, avgRUs * 0.04);

            if (estimatedDTUs <= 100 && totalSizeGB <= 250)
            {
                return "Standard S2-S4";
            }
            else if (estimatedDTUs <= 500 && totalSizeGB <= 1000)
            {
                return "Standard S6-S12";
            }
            else if (estimatedDTUs <= 2000 && totalSizeGB <= 4000)
            {
                return "Premium P2-P6";
            }
            else
            {
                return "Premium P11-P15 or vCore General Purpose";
            }
        }

        private List<DatabaseMapping> CreateDatabaseMappings(CosmosDbAnalysis analysis)
        {
            var databaseName = _configuration["CosmosDb:DatabaseName"] ?? "UnknownDatabase";
            var mappings = new List<DatabaseMapping>();

            var databaseMapping = new DatabaseMapping
            {
                SourceDatabase = databaseName,
                TargetDatabase = $"{databaseName}_SQL",
                ContainerMappings = new List<ContainerMapping>()
            };

            foreach (var container in analysis.Containers)
            {
                var containerMapping = CreateContainerMapping(container);
                databaseMapping.ContainerMappings.Add(containerMapping);
            }

            mappings.Add(databaseMapping);
            return mappings;
        }

        private ContainerMapping CreateContainerMapping(ContainerAnalysis container)
        {
            var mapping = new ContainerMapping
            {
                SourceContainer = container.ContainerName,
                TargetSchema = "dbo",
                TargetTable = SanitizeTableName(container.ContainerName),
                FieldMappings = new List<FieldMapping>(),
                RequiredTransformations = new List<string>()
            };

            // Create field mappings for each detected schema
            var primarySchema = container.DetectedSchemas.OrderByDescending(s => s.Prevalence).FirstOrDefault();
            if (primarySchema != null)
            {
                foreach (var field in primarySchema.Fields.Values)
                {
                    var fieldMapping = CreateFieldMapping(field, container.PartitionKey);
                    mapping.FieldMappings.Add(fieldMapping);

                    if (fieldMapping.RequiresTransformation)
                    {
                        mapping.RequiredTransformations.Add($"Transform {field.FieldName}: {fieldMapping.TransformationLogic}");
                    }
                }
            }

            // Add transformation requirements for nested documents
            if (container.DetectedSchemas.Any(s => s.Fields.Values.Any(f => f.IsNested)))
            {
                mapping.RequiredTransformations.Add("Flatten nested JSON structures");
            }

            return mapping;
        }

        private FieldMapping CreateFieldMapping(FieldInfo field, string partitionKey)
        {
            var mapping = new FieldMapping
            {
                SourceField = field.FieldName,
                SourceType = string.Join("|", field.DetectedTypes),
                TargetColumn = SanitizeColumnName(field.FieldName),
                TargetType = field.RecommendedSqlType,
                IsPartitionKey = field.FieldName.Equals(partitionKey.TrimStart('/'), StringComparison.OrdinalIgnoreCase),
                IsNullable = !field.IsRequired,
                RequiresTransformation = field.IsNested || field.DetectedTypes.Count > 1
            };

            // Define transformation logic for complex cases
            if (field.IsNested)
            {
                mapping.TransformationLogic = "Extract nested fields into separate columns or JSON column";
            }
            else if (field.DetectedTypes.Count > 1)
            {
                mapping.TransformationLogic = $"Handle multiple data types: {string.Join(", ", field.DetectedTypes)}";
            }

            // Adjust SQL type based on field characteristics
            if (mapping.TargetType == "NVARCHAR" && field.MaxLength > 0)
            {
                mapping.TargetType = field.MaxLength <= 4000 ? $"NVARCHAR({Math.Max(field.MaxLength * 2, 50)})" : "NVARCHAR(MAX)";
            }

            return mapping;
        }

        private List<IndexRecommendation> GenerateIndexRecommendations(CosmosDbAnalysis analysis)
        {
            var recommendations = new List<IndexRecommendation>();

            foreach (var container in analysis.Containers)
            {
                var tableName = SanitizeTableName(container.ContainerName);

                // Primary key recommendation (clustered index)
                recommendations.Add(new IndexRecommendation
                {
                    TableName = tableName,
                    IndexName = $"PK_{tableName}",
                    IndexType = "Clustered",
                    Columns = new List<string> { "id" },
                    Justification = "Primary key for unique identification and optimal storage",
                    Priority = 1,
                    EstimatedImpactRUs = 0 // No additional RU cost in SQL
                });

                // Partition key index
                if (!string.IsNullOrEmpty(container.PartitionKey))
                {
                    var partitionColumn = SanitizeColumnName(container.PartitionKey.TrimStart('/'));
                    recommendations.Add(new IndexRecommendation
                    {
                        TableName = tableName,
                        IndexName = $"IX_{tableName}_{partitionColumn}",
                        IndexType = "NonClustered",
                        Columns = new List<string> { partitionColumn },
                        Justification = "Partition key from Cosmos DB - likely used for filtering and grouping",
                        Priority = 2,
                        EstimatedImpactRUs = (long)(container.Performance.AverageRUConsumption * 0.1)
                    });
                }

                // Recommendations based on query patterns
                foreach (var query in container.Performance.TopQueries.Take(5))
                {
                    var indexRecommendation = GenerateQueryBasedIndexRecommendation(tableName, query.Value, container);
                    if (indexRecommendation != null)
                    {
                        recommendations.Add(indexRecommendation);
                    }
                }

                // Composite index recommendations based on Cosmos DB composite indexes
                foreach (var compositeIndex in container.IndexingPolicy.CompositeIndexes.Take(3))
                {
                    var columns = compositeIndex.Paths.Select(p => SanitizeColumnName(p.Path.TrimStart('/'))).ToList();
                    if (columns.Any())
                    {
                        recommendations.Add(new IndexRecommendation
                        {
                            TableName = tableName,
                            IndexName = $"IX_{tableName}_Composite_{string.Join("_", columns)}",
                            IndexType = "NonClustered",
                            Columns = columns,
                            Justification = "Based on existing Cosmos DB composite index usage patterns",
                            Priority = 3,
                            EstimatedImpactRUs = (long)(container.Performance.AverageRUConsumption * 0.05)
                        });
                    }
                }
            }

            _logger.LogInformation("Generated {IndexCount} index recommendations", recommendations.Count);
            return recommendations;
        }

        private IndexRecommendation? GenerateQueryBasedIndexRecommendation(string tableName, QueryMetrics query, ContainerAnalysis container)
        {
            // Simple pattern matching to identify common query patterns
            var queryPattern = query.QueryPattern.ToLowerInvariant();

            // Look for WHERE clause patterns
            if (queryPattern.Contains("where") && queryPattern.Contains("="))
            {
                // Extract potential column names (simplified logic)
                var primarySchema = container.DetectedSchemas.OrderByDescending(s => s.Prevalence).FirstOrDefault();
                if (primarySchema != null)
                {
                    var commonFields = primarySchema.Fields.Values
                        .Where(f => !f.IsNested && f.DetectedTypes.All(t => t != "NVARCHAR(MAX)"))
                        .Take(3)
                        .Select(f => SanitizeColumnName(f.FieldName))
                        .ToList();

                    if (commonFields.Any())
                    {
                        return new IndexRecommendation
                        {
                            TableName = tableName,
                            IndexName = $"IX_{tableName}_Query_Optimized",
                            IndexType = "NonClustered",
                            Columns = commonFields,
                            Justification = $"Optimizes frequent query pattern with {query.ExecutionCount} executions",
                            Priority = 4,
                            EstimatedImpactRUs = (long)query.AverageRUs
                        };
                    }
                }
            }

            return null;
        }

        private MigrationComplexity AssessMigrationComplexity(CosmosDbAnalysis analysis)
        {
            var complexity = new MigrationComplexity();
            var factors = new List<ComplexityFactor>();
            var risks = new List<string>();
            var assumptions = new List<string>();

            // Analyze complexity factors
            var totalContainers = analysis.Containers.Count;
            var schemasPerContainer = analysis.Containers.Select(c => c.DetectedSchemas.Count).ToList();
            var nestedFieldCount = analysis.Containers.Sum(c => c.DetectedSchemas.Sum(s => s.Fields.Values.Count(f => f.IsNested)));
            var totalDocuments = analysis.Containers.Sum(c => c.DocumentCount);

            // Container count factor
            if (totalContainers <= 5)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Container Count",
                    Impact = "Low",
                    Description = $"{totalContainers} containers - manageable scope"
                });
            }
            else if (totalContainers <= 20)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Container Count",
                    Impact = "Medium",
                    Description = $"{totalContainers} containers - moderate complexity"
                });
            }
            else
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Container Count",
                    Impact = "High",
                    Description = $"{totalContainers} containers - high coordination required"
                });
            }

            // Schema complexity factor
            var avgSchemasPerContainer = schemasPerContainer.Any() ? schemasPerContainer.Average() : 0;
            if (avgSchemasPerContainer <= 2)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Schema Consistency",
                    Impact = "Low",
                    Description = "Consistent schemas across containers"
                });
            }
            else if (avgSchemasPerContainer <= 5)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Schema Consistency",
                    Impact = "Medium",
                    Description = "Some schema variations requiring normalization"
                });
            }
            else
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Schema Consistency",
                    Impact = "High",
                    Description = "High schema variation - significant normalization effort"
                });
                risks.Add("Schema inconsistency may lead to data loss or corruption during migration");
            }

            // Nested data complexity
            if (nestedFieldCount == 0)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Structure",
                    Impact = "Low",
                    Description = "Flat document structure - straightforward mapping"
                });
            }
            else if (nestedFieldCount <= totalContainers * 5)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Structure",
                    Impact = "Medium",
                    Description = "Some nested structures requiring flattening or JSON columns"
                });
            }
            else
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Structure",
                    Impact = "High",
                    Description = "Highly nested structures - complex transformation required"
                });
                risks.Add("Complex nested structures may require significant application changes");
            }

            // Data volume factor
            if (totalDocuments <= 1_000_000)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Volume",
                    Impact = "Low",
                    Description = "Small dataset - quick migration possible"
                });
            }
            else if (totalDocuments <= 100_000_000)
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Volume",
                    Impact = "Medium",
                    Description = "Moderate dataset - planned migration windows needed"
                });
            }
            else
            {
                factors.Add(new ComplexityFactor
                {
                    Factor = "Data Volume",
                    Impact = "High",
                    Description = "Large dataset - extensive migration planning required"
                });
                risks.Add("Large data volumes may require extended migration windows and impact availability");
            }

            // Calculate overall complexity
            var highImpactCount = factors.Count(f => f.Impact == "High");
            var mediumImpactCount = factors.Count(f => f.Impact == "Medium");

            if (highImpactCount >= 2)
            {
                complexity.OverallComplexity = "High";
                complexity.EstimatedMigrationDays = 30 + (totalContainers * 3);
            }
            else if (highImpactCount == 1 || mediumImpactCount >= 2)
            {
                complexity.OverallComplexity = "Medium";
                complexity.EstimatedMigrationDays = 15 + (totalContainers * 2);
            }
            else
            {
                complexity.OverallComplexity = "Low";
                complexity.EstimatedMigrationDays = 5 + totalContainers;
            }

            // Add standard assumptions
            assumptions.Add("Application code changes will be handled separately");
            assumptions.Add("Target SQL infrastructure is properly sized");
            assumptions.Add("Migration will be performed during maintenance windows");
            assumptions.Add("Thorough testing will be conducted before go-live");

            if (analysis.MonitoringLimitations.Any())
            {
                assumptions.Add("Performance metrics are estimated due to monitoring limitations");
                risks.Add("Limited performance data may affect sizing recommendations");
            }

            complexity.ComplexityFactors = factors;
            complexity.Risks = risks;
            complexity.Assumptions = assumptions;

            _logger.LogInformation("Migration complexity assessed as {Complexity} with {EstimatedDays} estimated days", 
                complexity.OverallComplexity, complexity.EstimatedMigrationDays);

            return complexity;
        }

        private List<TransformationRule> DefineTransformationRules(CosmosDbAnalysis analysis)
        {
            var rules = new List<TransformationRule>();

            // Rule for flattening nested objects
            if (analysis.Containers.Any(c => c.DetectedSchemas.Any(s => s.Fields.Values.Any(f => f.IsNested))))
            {
                var affectedTables = analysis.Containers
                    .Where(c => c.DetectedSchemas.Any(s => s.Fields.Values.Any(f => f.IsNested)))
                    .Select(c => SanitizeTableName(c.ContainerName))
                    .ToList();

                rules.Add(new TransformationRule
                {
                    RuleName = "Flatten Nested Objects",
                    SourcePattern = "document.nestedObject.property",
                    TargetPattern = "flattened_nestedObject_property",
                    TransformationType = "Flatten",
                    Logic = "Convert nested JSON objects into separate columns with underscore naming convention",
                    AffectedTables = affectedTables
                });
            }

            // Rule for handling arrays
            if (analysis.Containers.Any(c => c.DetectedSchemas.Any(s => s.Fields.Keys.Any(k => k.Contains("[]")))))
            {
                var affectedTables = analysis.Containers
                    .Where(c => c.DetectedSchemas.Any(s => s.Fields.Keys.Any(k => k.Contains("[]"))))
                    .Select(c => SanitizeTableName(c.ContainerName))
                    .ToList();

                rules.Add(new TransformationRule
                {
                    RuleName = "Handle Array Fields",
                    SourcePattern = "document.arrayField[]",
                    TargetPattern = "arrayField_json NVARCHAR(MAX)",
                    TransformationType = "TypeConvert",
                    Logic = "Store arrays as JSON strings in NVARCHAR(MAX) columns for query flexibility",
                    AffectedTables = affectedTables
                });
            }

            // Rule for type unification
            foreach (var container in analysis.Containers)
            {
                var fieldsWithMultipleTypes = container.DetectedSchemas
                    .SelectMany(s => s.Fields.Values)
                    .Where(f => f.DetectedTypes.Count > 1)
                    .GroupBy(f => f.FieldName)
                    .Where(g => g.Count() > 1);

                if (fieldsWithMultipleTypes.Any())
                {
                    rules.Add(new TransformationRule
                    {
                        RuleName = $"Unify Types in {container.ContainerName}",
                        SourcePattern = "mixed type fields",
                        TargetPattern = "NVARCHAR(MAX) with validation",
                        TransformationType = "TypeConvert",
                        Logic = "Convert fields with multiple types to NVARCHAR(MAX) and add data validation logic",
                        AffectedTables = new List<string> { SanitizeTableName(container.ContainerName) }
                    });
                }
            }

            _logger.LogInformation("Defined {RuleCount} transformation rules", rules.Count);
            return rules;
        }

        private string SanitizeTableName(string name)
        {
            // Remove invalid characters and ensure SQL naming conventions
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (char.IsDigit(sanitized.FirstOrDefault()))
            {
                sanitized = "Table_" + sanitized;
            }
            return string.IsNullOrEmpty(sanitized) ? "UnnamedTable" : sanitized;
        }

        private string SanitizeColumnName(string name)
        {
            // Remove invalid characters and ensure SQL naming conventions
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (char.IsDigit(sanitized.FirstOrDefault()))
            {
                sanitized = "Col_" + sanitized;
            }
            return string.IsNullOrEmpty(sanitized) ? "UnnamedColumn" : sanitized;
        }
    }
}
