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

                // Generate foreign key constraints for referential integrity
                assessment.ForeignKeyConstraints = GenerateForeignKeyConstraints(cosmosAnalysis);

                // Generate unique constraints for business keys
                assessment.UniqueConstraints = GenerateUniqueConstraints(cosmosAnalysis);

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
                ChildTableMappings = new List<ChildTableMapping>(),
                RequiredTransformations = new List<string>()
            };

            // Create field mappings for each detected schema - main table only, excluding nested fields.
            var primarySchema = container.DetectedSchemas.OrderByDescending(s => s.Prevalence).FirstOrDefault();
            if (primarySchema != null)
            {
                // Detect and validate business keys for proper relational modeling
                var businessKeys = DetectBusinessKeys(primarySchema.Fields.Values);
                if (businessKeys.Any())
                {
                    mapping.RequiredTransformations.Add($"Consider creating unique constraints on business keys: {string.Join(", ", businessKeys)}");
                }

                // Detect potential normalization opportunities (2NF/3NF violations)
                var normalizationIssues = DetectNormalizationIssues(primarySchema.Fields.Values);
                if (normalizationIssues.Any())
                {
                    mapping.RequiredTransformations.AddRange(normalizationIssues);
                }

                foreach (var field in primarySchema.Fields.Values.Where(f => !f.IsNested))
                {
                    var fieldMapping = CreateFieldMapping(field, container.PartitionKey);
                    mapping.FieldMappings.Add(fieldMapping);

                    if (fieldMapping.RequiresTransformation)
                    {
                        mapping.RequiredTransformations.Add($"Transform {field.FieldName}: {fieldMapping.TransformationLogic}");
                    }
                }
            }

            // Create child table mappings for normalized schema
            foreach (var childTable in container.ChildTables.Values)
            {
                var childMapping = CreateChildTableMapping(childTable, container.ContainerName);
                
                // Validate child table relationships for proper modeling
                ValidateChildTableRelationship(childMapping, container, mapping.RequiredTransformations);
                
                mapping.ChildTableMappings.Add(childMapping);
                
                mapping.RequiredTransformations.Add($"Extract {childTable.SourceFieldPath} into separate table {childMapping.TargetTable}");
            }

            // Update transformation requirements for better normalization guidance
            if (container.ChildTables.Any())
            {
                mapping.RequiredTransformations.Add($"Normalize {container.ChildTables.Count} nested structures into separate related tables");
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
                mapping.TransformationLogic = "Nested field - normalized into separate related table with foreign key relationship";
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

        private ChildTableMapping CreateChildTableMapping(ChildTableSchema childTable, string parentContainerName)
        {
            var parentTableName = SanitizeTableName(parentContainerName);
            var parentKeyColumnName = $"{parentTableName}Id";
            
            var childMapping = new ChildTableMapping
            {
                SourceFieldPath = childTable.SourceFieldPath,
                ChildTableType = childTable.ChildTableType,
                TargetSchema = "dbo",
                TargetTable = SanitizeTableName($"{parentContainerName}_{childTable.TableName}"),
                ParentKeyColumn = parentKeyColumnName,
                FieldMappings = new List<FieldMapping>(),
                RequiredTransformations = new List<string>()
            };

            // Add foreign key field to child table
            var parentKeyMapping = new FieldMapping
            {
                SourceField = "id", // References parent document's id
                SourceType = "string",
                TargetColumn = childMapping.ParentKeyColumn,
                TargetType = "NVARCHAR(255)",
                IsPartitionKey = false,
                IsNullable = false,
                RequiresTransformation = false,
                TransformationLogic = $"Foreign key reference to {parentTableName} table"
            };
            childMapping.FieldMappings.Add(parentKeyMapping);

            // Add primary key field for child table
            var childPrimaryKeyMapping = new FieldMapping
            {
                SourceField = "$generated",
                SourceType = "generated",
                TargetColumn = "Id",
                TargetType = "BIGINT IDENTITY(1,1)",
                IsPartitionKey = false,
                IsNullable = false,
                RequiresTransformation = false,
                TransformationLogic = "Auto-generated primary key for child table"
            };
            childMapping.FieldMappings.Add(childPrimaryKeyMapping);

            // Create field mappings for child table fields
            foreach (var field in childTable.Fields.Values)
            {
                var fieldMapping = new FieldMapping
                {
                    SourceField = field.FieldName,
                    SourceType = string.Join("|", field.DetectedTypes),
                    TargetColumn = SanitizeColumnName(field.FieldName),
                    TargetType = field.RecommendedSqlType,
                    IsPartitionKey = false,
                    IsNullable = !field.IsRequired,
                    RequiresTransformation = field.DetectedTypes.Count > 1
                };

                if (field.DetectedTypes.Count > 1)
                {
                    fieldMapping.TransformationLogic = $"Handle multiple data types: {string.Join(", ", field.DetectedTypes)}";
                }

                // Adjust SQL type based on field characteristics
                if (fieldMapping.TargetType == "NVARCHAR" && field.MaxLength > 0)
                {
                    fieldMapping.TargetType = field.MaxLength <= 4000 ? $"NVARCHAR({Math.Max(field.MaxLength * 2, 50)})" : "NVARCHAR(MAX)";
                }

                childMapping.FieldMappings.Add(fieldMapping);

                if (fieldMapping.RequiresTransformation)
                {
                    childMapping.RequiredTransformations.Add($"Transform {field.FieldName}: {fieldMapping.TransformationLogic}");
                }
            }

            // Add transformation guidance
            if (childTable.ChildTableType == "Array")
            {
                childMapping.RequiredTransformations.Add("Extract array items into separate rows with foreign key references");
            }
            else if (childTable.ChildTableType == "NestedObject")
            {
                childMapping.RequiredTransformations.Add("Extract nested object properties into separate table with foreign key reference");
            }

            return childMapping;
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

                // Generate index recommendations for child tables
                foreach (var childTable in container.ChildTables.Values)
                {
                    var childTableName = SanitizeTableName($"{container.ContainerName}_{childTable.TableName}");
                    var parentTableName = SanitizeTableName(container.ContainerName);
                    var parentKeyColumnName = $"{parentTableName}Id";

                    // Primary key for child table
                    recommendations.Add(new IndexRecommendation
                    {
                        TableName = childTableName,
                        IndexName = $"PK_{childTableName}",
                        IndexType = "Clustered",
                        Columns = new List<string> { "Id" },
                        Justification = "Primary key for child table - auto-generated identity column",
                        Priority = 1,
                        EstimatedImpactRUs = 0
                    });

                    // Foreign key index for child table
                    recommendations.Add(new IndexRecommendation
                    {
                        TableName = childTableName,
                        IndexName = $"IX_{childTableName}_{parentKeyColumnName}",
                        IndexType = "NonClustered",
                        Columns = new List<string> { parentKeyColumnName },
                        Justification = $"Foreign key index for efficient joins with {parentTableName} table",
                        Priority = 2,
                        EstimatedImpactRUs = (long)(container.Performance.AverageRUConsumption * 0.05)
                    });

                    // Composite index for parent + frequently used child fields
                    var frequentFields = childTable.Fields.Values
                        .Where(f => f.Selectivity > 0.1 && !f.IsNested)
                        .OrderByDescending(f => f.Selectivity)
                        .Take(2)
                        .Select(f => SanitizeColumnName(f.FieldName))
                        .ToList();

                    if (frequentFields.Any())
                    {
                        var compositeColumns = new List<string> { parentKeyColumnName };
                        compositeColumns.AddRange(frequentFields);

                        recommendations.Add(new IndexRecommendation
                        {
                            TableName = childTableName,
                            IndexName = $"IX_{childTableName}_Composite",
                            IndexType = "NonClustered",
                            Columns = compositeColumns,
                            Justification = $"Composite index for efficient querying on {parentTableName} relationship and frequent fields",
                            Priority = 3,
                            EstimatedImpactRUs = (long)(container.Performance.AverageRUConsumption * 0.03)
                        });
                    }
                }
            }

            _logger.LogInformation("Generated {IndexCount} index recommendations", recommendations.Count);
            return recommendations;
        }

        /// <summary>
        /// Generates foreign key constraints for referential integrity
        /// </summary>
        private List<ForeignKeyConstraint> GenerateForeignKeyConstraints(CosmosDbAnalysis analysis)
        {
            var constraints = new List<ForeignKeyConstraint>();

            foreach (var container in analysis.Containers)
            {
                var parentTableName = SanitizeTableName(container.ContainerName);

                // Generate foreign key constraints for child tables
                foreach (var childTable in container.ChildTables.Values)
                {
                    var childTableName = SanitizeTableName($"{container.ContainerName}_{childTable.TableName}");
                    var parentKeyColumnName = $"{parentTableName}Id";

                    constraints.Add(new ForeignKeyConstraint
                    {
                        ConstraintName = $"FK_{childTableName}_{parentKeyColumnName}",
                        ChildTable = childTableName,
                        ChildColumn = parentKeyColumnName,
                        ParentTable = parentTableName,
                        ParentColumn = "Id",
                        OnDeleteAction = DetermineDeleteAction(childTable),
                        OnUpdateAction = "CASCADE",
                        Justification = $"Ensures referential integrity between {childTableName} and {parentTableName}",
                        IsDeferrable = false
                    });
                }
            }

            _logger.LogInformation("Generated {ConstraintCount} foreign key constraints", constraints.Count);
            return constraints;
        }

        /// <summary>
        /// Determines appropriate delete action based on child table characteristics
        /// </summary>
        private string DetermineDeleteAction(ChildTableSchema childTable)
        {
            // If it's an array of simple values or metadata, cascade delete is appropriate
            if (childTable.ChildTableType == "Array" && childTable.Fields.Count <= 3)
            {
                return "CASCADE";
            }
            
            // If it contains business-critical data, restrict deletion
            var hasCriticalFields = childTable.Fields.Values.Any(f => 
                f.FieldName.ToLowerInvariant().Contains("amount") ||
                f.FieldName.ToLowerInvariant().Contains("price") ||
                f.FieldName.ToLowerInvariant().Contains("total") ||
                f.FieldName.ToLowerInvariant().Contains("transaction"));
            
            return hasCriticalFields ? "RESTRICT" : "CASCADE";
        }

        /// <summary>
        /// Generates unique constraints for business keys
        /// </summary>
        private List<UniqueConstraint> GenerateUniqueConstraints(CosmosDbAnalysis analysis)
        {
            var constraints = new List<UniqueConstraint>();

            foreach (var container in analysis.Containers)
            {
                var tableName = SanitizeTableName(container.ContainerName);
                var primarySchema = container.DetectedSchemas.OrderByDescending(s => s.Prevalence).FirstOrDefault();
                
                if (primarySchema != null)
                {
                    var businessKeys = DetectBusinessKeys(primarySchema.Fields.Values);
                    
                    foreach (var businessKey in businessKeys)
                    {
                        if (businessKey.StartsWith("COMPOSITE_"))
                        {
                            // Parse composite key
                            var keyInfo = ParseCompositeKey(businessKey);
                            if (keyInfo.Columns.Any())
                            {
                                constraints.Add(new UniqueConstraint
                                {
                                    ConstraintName = $"UK_{tableName}_{keyInfo.KeyType}",
                                    TableName = tableName,
                                    Columns = keyInfo.Columns.Select(SanitizeColumnName).ToList(),
                                    ConstraintType = "UNIQUE",
                                    Justification = $"Composite business key constraint for {keyInfo.KeyType} uniqueness",
                                    IsComposite = true
                                });
                            }
                        }
                        else
                        {
                            // Single field business key
                            constraints.Add(new UniqueConstraint
                            {
                                ConstraintName = $"UK_{tableName}_{SanitizeColumnName(businessKey)}",
                                TableName = tableName,
                                Columns = new List<string> { SanitizeColumnName(businessKey) },
                                ConstraintType = "UNIQUE",
                                Justification = $"Business key constraint for {businessKey} uniqueness",
                                IsComposite = false
                            });
                        }
                    }
                }
            }

            _logger.LogInformation("Generated {ConstraintCount} unique constraints", constraints.Count);
            return constraints;
        }

        /// <summary>
        /// Parses composite key information from the business key string
        /// </summary>
        private (string KeyType, List<string> Columns) ParseCompositeKey(string compositeKey)
        {
            // Format: "COMPOSITE_KEYTYPE_KEY(column1, column2)"
            var keyType = "UNKNOWN";
            var columns = new List<string>();
            
            try
            {
                var startIndex = compositeKey.IndexOf("COMPOSITE_") + 10;
                var endIndex = compositeKey.IndexOf("_KEY(");
                if (startIndex > 10 && endIndex > startIndex)
                {
                    keyType = compositeKey.Substring(startIndex, endIndex - startIndex);
                }
                
                var columnsStart = compositeKey.IndexOf("(") + 1;
                var columnsEnd = compositeKey.IndexOf(")");
                if (columnsStart > 0 && columnsEnd > columnsStart)
                {
                    var columnsString = compositeKey.Substring(columnsStart, columnsEnd - columnsStart);
                    columns = columnsString.Split(',').Select(c => c.Trim()).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing composite key: {CompositeKey}", compositeKey);
            }
            
            return (keyType, columns);
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

        /// <summary>
        /// Detects potential business keys that should have unique constraints
        /// </summary>
        private List<string> DetectBusinessKeys(IEnumerable<FieldInfo> fields)
        {
            var businessKeys = new List<string>();
            var fieldList = fields.ToList();
            
            // Detect individual business keys
            foreach (var field in fieldList)
            {
                var fieldName = field.FieldName.ToLowerInvariant();
                
                // Common business key patterns
                if (fieldName.Contains("email") || fieldName.Contains("username") ||
                    fieldName.Contains("code") || fieldName.Contains("number") ||
                    fieldName.Contains("sku") || fieldName.Contains("identifier") ||
                    (fieldName.Contains("name") && field.DetectedTypes.Contains("UNIQUEIDENTIFIER")))
                {
                    // High selectivity indicates potential uniqueness
                    if (field.Selectivity > 0.8)
                    {
                        businessKeys.Add(field.FieldName);
                    }
                }
            }
            
            // Detect composite business keys
            var compositeKeys = DetectCompositeBusinessKeys(fieldList);
            businessKeys.AddRange(compositeKeys);
            
            return businessKeys;
        }

        /// <summary>
        /// Detects composite business keys (combinations of fields that together form unique constraints)
        /// </summary>
        private List<string> DetectCompositeBusinessKeys(List<FieldInfo> fields)
        {
            var compositeKeys = new List<string>();
            
            // Common composite key patterns
            var compositePatterns = new[]
            {
                new { Pattern = new[] { "first", "last" }, KeyType = "name" },
                new { Pattern = new[] { "year", "month" }, KeyType = "period" },
                new { Pattern = new[] { "area", "code" }, KeyType = "location" },
                new { Pattern = new[] { "type", "number" }, KeyType = "classification" },
                new { Pattern = new[] { "category", "subcategory" }, KeyType = "hierarchy" },
                new { Pattern = new[] { "start", "end" }, KeyType = "range" },
                new { Pattern = new[] { "from", "to" }, KeyType = "range" },
                new { Pattern = new[] { "source", "target" }, KeyType = "relationship" }
            };
            
            foreach (var pattern in compositePatterns)
            {
                var matchingFields = new List<string>();
                
                foreach (var keyword in pattern.Pattern)
                {
                    var field = fields.FirstOrDefault(f => 
                        f.FieldName.ToLowerInvariant().Contains(keyword) &&
                        f.Selectivity > 0.3); // Moderate selectivity for composite keys
                    
                    if (field != null)
                    {
                        matchingFields.Add(field.FieldName);
                    }
                }
                
                // If we found all parts of the composite key pattern
                if (matchingFields.Count == pattern.Pattern.Length)
                {
                    var compositeKeyName = $"COMPOSITE_{pattern.KeyType.ToUpper()}_KEY({string.Join(", ", matchingFields)})";
                    compositeKeys.Add(compositeKeyName);
                }
            }
            
            // Detect geographic composite keys
            var geoFields = fields.Where(f => 
                f.FieldName.ToLowerInvariant().Contains("lat") ||
                f.FieldName.ToLowerInvariant().Contains("lng") ||
                f.FieldName.ToLowerInvariant().Contains("longitude") ||
                f.FieldName.ToLowerInvariant().Contains("latitude")).ToList();
            
            if (geoFields.Count >= 2)
            {
                var latField = geoFields.FirstOrDefault(f => f.FieldName.ToLowerInvariant().Contains("lat"));
                var lngField = geoFields.FirstOrDefault(f => f.FieldName.ToLowerInvariant().Contains("lng") || 
                                                            f.FieldName.ToLowerInvariant().Contains("lon"));
                
                if (latField != null && lngField != null)
                {
                    compositeKeys.Add($"COMPOSITE_GEOGRAPHIC_KEY({latField.FieldName}, {lngField.FieldName})");
                }
            }
            
            // Detect temporal composite keys
            var dateFields = fields.Where(f => 
                f.RecommendedSqlType.Contains("DATE") ||
                f.DetectedTypes.Any(t => t.Contains("DATE"))).ToList();
            
            if (dateFields.Count >= 2)
            {
                var startDate = dateFields.FirstOrDefault(f => 
                    f.FieldName.ToLowerInvariant().Contains("start") ||
                    f.FieldName.ToLowerInvariant().Contains("from") ||
                    f.FieldName.ToLowerInvariant().Contains("begin"));
                    
                var endDate = dateFields.FirstOrDefault(f => 
                    f.FieldName.ToLowerInvariant().Contains("end") ||
                    f.FieldName.ToLowerInvariant().Contains("to") ||
                    f.FieldName.ToLowerInvariant().Contains("until"));
                
                if (startDate != null && endDate != null)
                {
                    compositeKeys.Add($"COMPOSITE_TEMPORAL_KEY({startDate.FieldName}, {endDate.FieldName})");
                }
            }
            
            return compositeKeys;
        }

        /// <summary>
        /// Detects potential 2NF/3NF violations that require further normalization
        /// </summary>
        private List<string> DetectNormalizationIssues(IEnumerable<FieldInfo> fields)
        {
            var issues = new List<string>();
            var fieldList = fields.ToList();
            
            // Detect repeating field patterns that suggest separate entities
            var fieldGroups = fieldList
                .GroupBy(f => f.FieldName.Split('_')[0]) // Group by prefix
                .Where(g => g.Count() > 3) // Groups with multiple related fields
                .ToList();
            
            foreach (var group in fieldGroups)
            {
                var prefix = group.Key;
                if (!string.IsNullOrEmpty(prefix) && prefix.Length > 2 && 
                    !prefix.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Consider normalizing '{prefix}' related fields into separate table for better 2NF compliance");
                }
            }
            
            // Detect potential transitive dependencies
            var addressFields = fieldList.Where(f => 
                f.FieldName.ToLowerInvariant().Contains("address") ||
                f.FieldName.ToLowerInvariant().Contains("city") ||
                f.FieldName.ToLowerInvariant().Contains("state") ||
                f.FieldName.ToLowerInvariant().Contains("zip") ||
                f.FieldName.ToLowerInvariant().Contains("country")).ToList();
                
            if (addressFields.Count >= 3)
            {
                issues.Add("Consider normalizing address fields into separate Address table for 3NF compliance");
            }
            
            return issues;
        }

        /// <summary>
        /// Validates child table relationships for proper relational modeling
        /// </summary>
        private void ValidateChildTableRelationship(ChildTableMapping childMapping, ContainerAnalysis container, List<string> transformations)
        {
            // Check for potential one-to-one relationships
            if (childMapping.ChildTableType == "NestedObject")
            {
                var childFieldCount = childMapping.FieldMappings.Count(fm => !fm.SourceField.StartsWith("$"));
                if (childFieldCount <= 3) // Small number of fields suggests one-to-one
                {
                    transformations.Add($"Consider merging {childMapping.TargetTable} back into main table if it represents a one-to-one relationship");
                }
            }
            
            // Enhanced many-to-many relationship detection and junction table creation
            if (childMapping.ChildTableType == "Array")
            {
                var junctionTableInfo = DetectJunctionTableNeed(childMapping, container);
                if (junctionTableInfo.IsJunctionTable)
                {
                    transformations.Add($"JUNCTION TABLE NEEDED: {childMapping.TargetTable} appears to be a many-to-many relationship");
                    transformations.Add($"Create junction table: {junctionTableInfo.SuggestedJunctionTableName}");
                    transformations.Add($"Junction table should have: {container.ContainerName}Id, {junctionTableInfo.ReferencedEntityName}Id, and relationship metadata");
                    
                    // Add the junction table as a separate child mapping
                    CreateJunctionTableMapping(childMapping, junctionTableInfo, transformations);
                }
                else
                {
                    var hasReferenceFields = childMapping.FieldMappings.Any(fm => 
                        fm.SourceField.ToLowerInvariant().Contains("id") && 
                        fm.TargetType.Contains("UNIQUEIDENTIFIER"));
                        
                    if (hasReferenceFields)
                    {
                        transformations.Add($"Verify if {childMapping.TargetTable} represents a many-to-many relationship requiring junction table");
                    }
                }
            }
            
            // Validate foreign key data types match
            var parentIdField = container.DetectedSchemas
                .SelectMany(s => s.Fields.Values)
                .FirstOrDefault(f => f.FieldName.Equals("id", StringComparison.OrdinalIgnoreCase));
                
            if (parentIdField != null)
            {
                var parentKeyMapping = childMapping.FieldMappings.FirstOrDefault(fm => 
                    fm.TransformationLogic?.Contains("Foreign key") == true);
                    
                if (parentKeyMapping != null && parentKeyMapping.TargetType != parentIdField.RecommendedSqlType)
                {
                    transformations.Add($"Ensure foreign key data type in {childMapping.TargetTable} matches parent table primary key");
                }
            }
        }

        /// <summary>
        /// Junction table information for many-to-many relationships
        /// </summary>
        private class JunctionTableInfo
        {
            public bool IsJunctionTable { get; set; }
            public string SuggestedJunctionTableName { get; set; } = string.Empty;
            public string ReferencedEntityName { get; set; } = string.Empty;
            public List<string> RelationshipFields { get; set; } = new();
        }

        /// <summary>
        /// Detects if a child table represents a many-to-many relationship requiring a junction table
        /// </summary>
        private JunctionTableInfo DetectJunctionTableNeed(ChildTableMapping childMapping, ContainerAnalysis container)
        {
            var info = new JunctionTableInfo();
            
            // Look for patterns that suggest many-to-many relationships
            var idFields = childMapping.FieldMappings
                .Where(fm => fm.SourceField.ToLowerInvariant().Contains("id") && 
                           !fm.SourceField.Equals("id", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            // Check for reference IDs (e.g., ProductId, CategoryId, UserId)
            var referenceIds = idFields.Where(fm => 
                fm.TargetType.Contains("UNIQUEIDENTIFIER") || 
                fm.TargetType.Contains("INT") || 
                fm.TargetType.Contains("BIGINT")).ToList();
            
            if (referenceIds.Count >= 1)
            {
                // This looks like a junction table if:
                // 1. Has reference IDs
                // 2. Has few additional fields (mostly relationship metadata)
                // 3. Array name suggests a relationship (e.g., "assignments", "memberships", "tags")
                
                var nonIdFields = childMapping.FieldMappings
                    .Where(fm => !fm.SourceField.ToLowerInvariant().Contains("id") && 
                               !fm.SourceField.StartsWith("$"))
                    .ToList();
                
                var arrayName = childMapping.SourceFieldPath.ToLowerInvariant();
                var relationshipKeywords = new[] { "assignment", "membership", "tag", "category", "role", "permission", "link", "association", "relation" };
                
                if (referenceIds.Count >= 1 && nonIdFields.Count <= 3 && 
                    relationshipKeywords.Any(keyword => arrayName.Contains(keyword)))
                {
                    info.IsJunctionTable = true;
                    info.ReferencedEntityName = ExtractEntityNameFromId(referenceIds.First().SourceField);
                    info.SuggestedJunctionTableName = $"{container.ContainerName}_{info.ReferencedEntityName}_Junction";
                    info.RelationshipFields = nonIdFields.Select(f => f.SourceField).ToList();
                }
            }
            
            return info;
        }

        /// <summary>
        /// Extracts entity name from ID field (e.g., "ProductId" -> "Product")
        /// </summary>
        private string ExtractEntityNameFromId(string idFieldName)
        {
            if (idFieldName.ToLowerInvariant().EndsWith("id"))
            {
                return idFieldName.Substring(0, idFieldName.Length - 2);
            }
            return idFieldName;
        }

        /// <summary>
        /// Creates junction table mapping for many-to-many relationships
        /// </summary>
        private void CreateJunctionTableMapping(ChildTableMapping originalMapping, JunctionTableInfo junctionInfo, List<string> transformations)
        {
            transformations.Add($"IMPLEMENTATION GUIDANCE for junction table {junctionInfo.SuggestedJunctionTableName}:");
            transformations.Add($"  - Primary Key: Composite key of both foreign keys OR auto-generated identity");
            transformations.Add($"  - Foreign Key 1: {originalMapping.ParentKeyColumn} -> {originalMapping.TargetTable.Replace("_" + originalMapping.SourceFieldPath, "")} table");
            transformations.Add($"  - Foreign Key 2: {junctionInfo.ReferencedEntityName}Id -> {junctionInfo.ReferencedEntityName} table");
            
            if (junctionInfo.RelationshipFields.Any())
            {
                transformations.Add($"  - Relationship metadata: {string.Join(", ", junctionInfo.RelationshipFields)}");
            }
            
            transformations.Add($"  - Unique constraint on (FK1, FK2) to prevent duplicate relationships");
            transformations.Add($"  - Indexes on both foreign keys for efficient querying");
        }
    }
}
