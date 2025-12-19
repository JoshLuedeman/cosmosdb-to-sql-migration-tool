using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;
using System.Text.Json;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for analyzing Cosmos DB databases and collecting performance metrics
    /// Implements Azure best practices with proper authentication and error handling
    /// </summary>
    public class CosmosDbAnalysisService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CosmosDbAnalysisService> _logger;
        private readonly CosmosClient _cosmosClient;
        private readonly LogsQueryClient? _logsQueryClient;

        public CosmosDbAnalysisService(IConfiguration configuration, ILogger<CosmosDbAnalysisService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            // Initialize Cosmos Client with user credentials (Interactive Browser authentication)
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = false,
                ExcludeWorkloadIdentityCredential = false,
                ExcludeManagedIdentityCredential = false,
                ExcludeInteractiveBrowserCredential = false,
                ExcludeAzureCliCredential = false,
                ExcludeAzurePowerShellCredential = false,
                ExcludeAzureDeveloperCliCredential = false
            });

            var cosmosEndpoint = _configuration["CosmosDb:AccountEndpoint"];
            if (string.IsNullOrEmpty(cosmosEndpoint))
            {
                throw new ArgumentException("Cosmos DB account endpoint not configured");
            }

            _cosmosClient = new CosmosClient(cosmosEndpoint, credential);

            // Initialize Azure Monitor client if workspace ID is configured
            var workspaceId = _configuration["AzureMonitor:WorkspaceId"];
            if (!string.IsNullOrEmpty(workspaceId))
            {
                try
                {
                    _logsQueryClient = new LogsQueryClient(credential);
                    _logger.LogInformation("Azure Monitor client initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize Azure Monitor client. Performance metrics will be limited.");
                }
            }
            else
            {
                _logger.LogWarning("Azure Monitor workspace ID not configured. Performance metrics will be limited.");
            }
        }

        /// <summary>
        /// Performs comprehensive analysis of the Cosmos DB database
        /// </summary>
        public async Task<CosmosDbAnalysis> AnalyzeDatabaseAsync(CancellationToken cancellationToken = default)
        {
            var databaseName = _configuration["CosmosDb:DatabaseName"];
            if (string.IsNullOrEmpty(databaseName))
            {
                throw new ArgumentException("Database name not configured");
            }
            
            return await AnalyzeDatabaseAsync(databaseName, cancellationToken);
        }

        /// <summary>
        /// Performs comprehensive analysis of the specified Cosmos DB database
        /// </summary>
        public async Task<CosmosDbAnalysis> AnalyzeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            var analysis = new CosmosDbAnalysis();

            if (string.IsNullOrEmpty(databaseName))
            {
                throw new ArgumentException("Database name cannot be null or empty", nameof(databaseName));
            }

            _logger.LogInformation("Starting Cosmos DB analysis for database: {DatabaseName}", databaseName);

            try
            {
                // Get database reference
                var database = _cosmosClient.GetDatabase(databaseName);

                // Analyze database-level metrics
                analysis.DatabaseMetrics = await AnalyzeDatabaseMetricsAsync(database, cancellationToken);

                // Get containers to analyze
                var containersToAnalyze = await GetContainersToAnalyzeAsync(database, cancellationToken);

                // Analyze each container
                foreach (var containerName in containersToAnalyze)
                {
                    _logger.LogInformation("Analyzing container: {ContainerName}", containerName);
                    var containerAnalysis = await AnalyzeContainerAsync(database.GetContainer(containerName), cancellationToken);
                    analysis.Containers.Add(containerAnalysis);
                }

                // Collect performance metrics if Azure Monitor is available
                if (_logsQueryClient != null)
                {
                    analysis.PerformanceMetrics = await CollectPerformanceMetricsAsync(cancellationToken);
                }
                else
                {
                    analysis.MonitoringLimitations.Add("Azure Monitor not configured - performance metrics unavailable");
                }

                _logger.LogInformation("Cosmos DB analysis completed successfully. Analyzed {ContainerCount} containers.", analysis.Containers.Count);
            }
            catch (CosmosException ex)
            {
                _logger.LogError(ex, "Cosmos DB error during analysis: {StatusCode} - {Message}", ex.StatusCode, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during Cosmos DB analysis");
                throw;
            }

            return analysis;
        }

        private async Task<DatabaseMetrics> AnalyzeDatabaseMetricsAsync(Database database, CancellationToken cancellationToken)
        {
            var metrics = new DatabaseMetrics();

            try
            {
                // Get database properties
                var response = await database.ReadAsync(cancellationToken: cancellationToken);
                
                // Note: Some metrics require account-level access which might not be available
                // We'll collect what we can and note limitations

                _logger.LogInformation("Database throughput model: {ThroughputModel}", 
                    response.Resource.Id != null ? "Provisioned" : "Unknown");

                metrics.ConsistencyLevel = "Unknown"; // Requires account-level access
                
                // Count containers
                var containerIterator = database.GetContainerQueryIterator<dynamic>();
                var containerNames = new List<string>();
                
                while (containerIterator.HasMoreResults)
                {
                    var containerResponse = await containerIterator.ReadNextAsync(cancellationToken);
                    foreach (var container in containerResponse)
                    {
                        containerNames.Add(container.id.ToString());
                    }
                }

                metrics.ContainerCount = containerNames.Count;
                _logger.LogInformation("Found {ContainerCount} containers in database", metrics.ContainerCount);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Limited access to database-level metrics");
            }

            return metrics;
        }

        private async Task<List<string>> GetContainersToAnalyzeAsync(Database database, CancellationToken cancellationToken)
        {
            var analyzeAll = _configuration.GetValue<bool>("CosmosDb:AnalyzeAllContainers", true);
            var configuredContainers = _configuration.GetSection("CosmosDb:ContainerNames").Get<string[]>() ?? Array.Empty<string>();

            if (!analyzeAll && configuredContainers.Length > 0)
            {
                _logger.LogInformation("Analyzing configured containers: {Containers}", string.Join(", ", configuredContainers));
                return configuredContainers.ToList();
            }

            // Discover all containers
            var containers = new List<string>();
            var iterator = database.GetContainerQueryIterator<dynamic>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                foreach (var container in response)
                {
                    string containerName = container.id;
                    containers.Add(containerName);
                    _logger.LogInformation("Found container: {ContainerName}", containerName);
                }
            }

            _logger.LogInformation("Discovered {ContainerCount} containers for analysis: {ContainerNames}", 
                containers.Count, string.Join(", ", containers));
            return containers;
        }

        private async Task<ContainerAnalysis> AnalyzeContainerAsync(Container container, CancellationToken cancellationToken)
        {
            var analysis = new ContainerAnalysis
            {
                ContainerName = container.Id
            };

            try
            {
                // Read container properties
                var containerResponse = await container.ReadContainerAsync(cancellationToken: cancellationToken);
                var containerProperties = containerResponse.Resource;

                analysis.PartitionKey = containerProperties.PartitionKeyPath ?? "/id";
                
                // Get throughput information
                try
                {
                    var throughputResponse = await container.ReadThroughputAsync(cancellationToken: cancellationToken);
                    analysis.ProvisionedRUs = throughputResponse ?? 0;
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // Container might be using shared database throughput
                    _logger.LogInformation("Container {ContainerName} uses shared database throughput", container.Id);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // Insufficient permissions to read throughput settings
                    _logger.LogWarning("Insufficient permissions to read throughput settings for container {ContainerName}. Continuing without throughput analysis.", container.Id);
                    analysis.ProvisionedRUs = 0; // Set to 0 to indicate unknown
                }

                // Analyze indexing policy
                analysis.IndexingPolicy = AnalyzeIndexingPolicy(containerProperties.IndexingPolicy);

                // Sample documents to understand schema
                _logger.LogInformation("Starting schema analysis for container {ContainerName}", container.Id);
                var (schemas, childTables) = await AnalyzeDocumentSchemasAsync(container, cancellationToken);
                analysis.DetectedSchemas = schemas;
                analysis.ChildTables = childTables;
                _logger.LogInformation("Completed schema analysis for container {ContainerName} - Found {SchemaCount} schemas and {ChildTableCount} child tables", 
                    container.Id, analysis.DetectedSchemas.Count, analysis.ChildTables.Count);

                // Get document count (this is an approximation)
                var countQuery = new QueryDefinition("SELECT VALUE COUNT(1) FROM c");
                var countIterator = container.GetItemQueryIterator<int>(countQuery);
                
                if (countIterator.HasMoreResults)
                {
                    var countResponse = await countIterator.ReadNextAsync(cancellationToken);
                    analysis.DocumentCount = countResponse.FirstOrDefault();
                }

                _logger.LogInformation("Container {ContainerName}: {DocumentCount} documents, {SchemaCount} detected schemas", 
                    container.Id, analysis.DocumentCount, analysis.DetectedSchemas.Count);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing container {ContainerName}", container.Id);
                throw;
            }

            return analysis;
        }

        private ContainerIndexingPolicy AnalyzeIndexingPolicy(Microsoft.Azure.Cosmos.IndexingPolicy indexingPolicy)
        {
            var policy = new ContainerIndexingPolicy();

            if (indexingPolicy.IncludedPaths != null)
            {
                policy.IncludedPaths = indexingPolicy.IncludedPaths.Select(p => p.Path).ToList();
            }

            if (indexingPolicy.ExcludedPaths != null)
            {
                policy.ExcludedPaths = indexingPolicy.ExcludedPaths.Select(p => p.Path).ToList();
            }

            if (indexingPolicy.CompositeIndexes != null)
            {
                policy.CompositeIndexes = indexingPolicy.CompositeIndexes.Select(ci => new CompositeIndex
                {
                    Paths = ci.Select(path => new IndexPath
                    {
                        Path = path.Path,
                        Order = path.Order.ToString()
                    }).ToList()
                }).ToList();
            }

            return policy;
        }

        private async Task<(List<DocumentSchema> schemas, Dictionary<string, ChildTableSchema> childTables)> AnalyzeDocumentSchemasAsync(Container container, CancellationToken cancellationToken)
        {
            var schemas = new Dictionary<string, DocumentSchema>();
            var childTables = new Dictionary<string, ChildTableSchema>();
            
            // Track array values across all documents for many-to-many analysis
            var arrayValueFrequency = new Dictionary<string, Dictionary<string, int>>();
            
            const int sampleSize = 100; // Analyze first 100 documents

            _logger.LogInformation("Starting document schema analysis for container {ContainerName}", container.Id);

            try
            {
                var query = new QueryDefinition($"SELECT TOP {sampleSize} * FROM c");
                var iterator = container.GetItemQueryIterator<dynamic>(query);

                int totalDocumentsProcessed = 0;
                
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync(cancellationToken);
                    
                    _logger.LogInformation("Processing {DocumentCount} documents from container {ContainerName}", 
                        response.Count(), container.Id);
                    
                    foreach (var dynamicDocument in response)
                    {
                        totalDocumentsProcessed++;
                        Console.WriteLine($"DEBUG: Processing document #{totalDocumentsProcessed}");
                        
                        // Convert dynamic to JSON string and then to JsonElement
                        string docString = "";
                        JsonElement document = default;
                        
                        try
                        {
                            docString = dynamicDocument.ToString();
                            Console.WriteLine($"DEBUG: Dynamic document content length: {docString.Length}");
                            
                            if (!string.IsNullOrEmpty(docString))
                            {
                                var jsonDoc = JsonDocument.Parse(docString);
                                document = jsonDoc.RootElement;
                                Console.WriteLine($"DEBUG: Parsed JsonElement successfully. ValueKind: {document.ValueKind}");
                            }
                            else
                            {
                                Console.WriteLine($"DEBUG: Empty document string from dynamic object");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"DEBUG: Error converting dynamic to JsonElement: {ex.Message}");
                            continue;
                        }
                        
                        // Safely get document ID
                        string docId = "No ID";
                        try
                        {
                            if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("id", out var idProp))
                            {
                                docId = idProp.GetString() ?? "No ID";
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"DEBUG: Error getting document ID: {ex.Message}");
                        }
                        
                        _logger.LogInformation("Processing document #{DocNumber}: {DocId}", 
                            totalDocumentsProcessed, docId);
                            
                        // Log the raw document structure for debugging
                        Console.WriteLine($"DEBUG: Document content length: {docString.Length}");
                        
                        if (docString.Length > 200)
                        {
                            Console.WriteLine($"DEBUG: Document preview: {docString.Substring(0, 200)}...");
                            _logger.LogInformation("Document preview: {DocPreview}...", docString.Substring(0, 200));
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG: Full document: {docString}");
                            _logger.LogInformation("Full document: {Document}", docString);
                        }
                        
                        Console.WriteLine($"DEBUG: About to analyze document structure");
                        AnalyzeDocumentStructure(document, schemas, childTables, arrayValueFrequency);
                        
                        // DEBUGGING: Let's also try parsing the docString directly to see if we can extract fields
                        if (!string.IsNullOrEmpty(docString))
                        {
                            try
                            {
                                var parsedDoc = JsonDocument.Parse(docString);
                                Console.WriteLine($"DEBUG: Successfully parsed docString. Root element kind: {parsedDoc.RootElement.ValueKind}");
                                
                                // Try direct field extraction from the parsed document
                                var testFields = new Dictionary<string, FieldInfo>();
                                var testChildTables = new Dictionary<string, ChildTableInfo>();
                                
                                Console.WriteLine($"DEBUG: About to call ExtractFieldsWithNormalization on parsed document");
                                var testArrayFrequency = new Dictionary<string, Dictionary<string, int>>();
                                ExtractFieldsWithNormalization(parsedDoc.RootElement, "", testFields, testChildTables, testArrayFrequency);
                                
                                Console.WriteLine($"DEBUG: Direct parsing extracted {testFields.Count} fields: {string.Join(", ", testFields.Keys)}");
                                if (testFields.Count > 0)
                                {
                                    foreach (var field in testFields.Take(5))
                                    {
                                        Console.WriteLine($"DEBUG: Field details - {field.Key}: {field.Value.RecommendedSqlType} (Types: {string.Join(",", field.Value.DetectedTypes)})");
                                    }
                                }
                                
                                parsedDoc.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"DEBUG: Error parsing docString: {ex.Message}");
                            }
                        }
                        
                        Console.WriteLine($"DEBUG: Completed document structure analysis. Current schema count: {schemas.Count}");
                    }
                }

                _logger.LogInformation("Processed {TotalDocs} documents, found {SchemaCount} unique schemas", 
                    totalDocumentsProcessed, schemas.Count);

                // Convert to list and calculate prevalence
                var totalDocuments = schemas.Values.Sum(s => s.SampleCount);
                foreach (var schema in schemas.Values)
                {
                    schema.Prevalence = totalDocuments > 0 ? (double)schema.SampleCount / totalDocuments : 0;
                }

                _logger.LogInformation("Detected {SchemaCount} unique schemas in container {ContainerName}", 
                    schemas.Count, container.Id);

                // Analyze array value frequencies for many-to-many recommendations
                _logger.LogInformation("Analyzing array value frequencies for many-to-many relationship detection...");
                ApplyManyToManyAnalysis(childTables, arrayValueFrequency, (int)totalDocuments);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing document schemas for container {ContainerName}", container.Id);
            }

            return (schemas.Values.ToList(), childTables);
        }

        private void AnalyzeDocumentStructure(JsonElement document, Dictionary<string, DocumentSchema> schemas, Dictionary<string, ChildTableSchema> childTables, Dictionary<string, Dictionary<string, int>> arrayValueFrequency)
        {
            Console.WriteLine("DEBUG: Starting AnalyzeDocumentStructure");
            var mainTableFields = new Dictionary<string, FieldInfo>();
            var rawChildTables = new Dictionary<string, ChildTableInfo>();
            
            Console.WriteLine("DEBUG: About to call ExtractFieldsWithNormalization");
            ExtractFieldsWithNormalization(document, "", mainTableFields, rawChildTables, arrayValueFrequency);

            Console.WriteLine($"DEBUG: ExtractFieldsWithNormalization completed. Main fields: {mainTableFields.Count}, Child tables: {rawChildTables.Count}");
            
            _logger.LogInformation("Extracted {MainFieldCount} main fields and {ChildTableCount} child tables for schema analysis", 
                mainTableFields.Count, rawChildTables.Count);
            
            if (mainTableFields.Any())
            {
                Console.WriteLine($"DEBUG: Main fields found: {string.Join(", ", mainTableFields.Keys)}");
                _logger.LogInformation("Main fields detected: {Fields}", string.Join(", ", mainTableFields.Keys));
            }
            else
            {
                Console.WriteLine("DEBUG: No main fields extracted from document!");
                _logger.LogWarning("No main fields extracted from document!");
            }

            // Create a signature for this schema based on main table fields only (child tables are stored separately)
            var mainSignature = string.Join("|", mainTableFields.Select(f => $"{f.Key}:{string.Join(",", f.Value.DetectedTypes)}"));
            
            Console.WriteLine($"DEBUG: Schema signature: {mainSignature}");
            
            if (!schemas.ContainsKey(mainSignature))
            {
                var newSchema = new DocumentSchema
                {
                    SchemaName = $"Schema_{schemas.Count + 1}",
                    Fields = new Dictionary<string, FieldInfo>(mainTableFields), // Make a copy
                    SampleCount = 0
                };
                
                Console.WriteLine($"DEBUG: Creating new schema '{newSchema.SchemaName}' with {newSchema.Fields.Count} fields");
                
                schemas[mainSignature] = newSchema;
            }

            schemas[mainSignature].SampleCount++;

            // Process child tables separately for normalization
            foreach (var childTable in rawChildTables)
            {
                var tableName = childTable.Key;
                var childTableInfo = childTable.Value;
                var childFieldSamples = childTableInfo.FieldSamples;

                if (!childTables.ContainsKey(tableName))
                {
                    // Create child table schema by consolidating all field samples
                    var consolidatedFields = new Dictionary<string, FieldInfo>();
                    var sampleCount = 0;

                    foreach (var fieldSample in childFieldSamples)
                    {
                        sampleCount++;
                        foreach (var field in fieldSample)
                        {
                            if (!consolidatedFields.ContainsKey(field.Key))
                            {
                                consolidatedFields[field.Key] = new FieldInfo
                                {
                                    FieldName = field.Key,
                                    DetectedTypes = new List<string>(field.Value.DetectedTypes),
                                    RecommendedSqlType = field.Value.RecommendedSqlType,
                                    IsRequired = field.Value.IsRequired,
                                    IsNested = field.Value.IsNested,
                                    MaxLength = field.Value.MaxLength,
                                    Selectivity = field.Value.Selectivity
                                };
                            }
                            else
                            {
                                // Merge field information
                                foreach (var type in field.Value.DetectedTypes)
                                {
                                    if (!consolidatedFields[field.Key].DetectedTypes.Contains(type))
                                    {
                                        consolidatedFields[field.Key].DetectedTypes.Add(type);
                                    }
                                }
                                consolidatedFields[field.Key].MaxLength = Math.Max(consolidatedFields[field.Key].MaxLength, field.Value.MaxLength);
                                if (!field.Value.IsRequired)
                                {
                                    consolidatedFields[field.Key].IsRequired = false;
                                }
                            }
                        }
                    }

                    childTables[tableName] = new ChildTableSchema
                    {
                        TableName = tableName,
                        SourceFieldPath = tableName,
                        ChildTableType = childTableInfo.SourceType, // Now correctly detects "Array" or "NestedObject"
                        Fields = consolidatedFields,
                        SampleCount = sampleCount,
                        ParentKeyField = "ParentId"
                    };
                }
                else
                {
                    // Update existing child table schema
                    childTables[tableName].SampleCount += childFieldSamples.Count;
                }
            }
            
            Console.WriteLine($"DEBUG: Updated schema '{schemas[mainSignature].SchemaName}' sample count to {schemas[mainSignature].SampleCount}");
            Console.WriteLine($"DEBUG: Schema now has {schemas[mainSignature].Fields.Count} fields: {string.Join(", ", schemas[mainSignature].Fields.Keys)}");
            Console.WriteLine($"DEBUG: Found {childTables.Count} child tables: {string.Join(", ", childTables.Keys)}");
        }

        private void ExtractFieldsWithNormalization(JsonElement element, string prefix, Dictionary<string, FieldInfo> mainFields, Dictionary<string, ChildTableInfo> childTables, Dictionary<string, Dictionary<string, int>> arrayValueFrequency)
        {
            try
            {
                Console.WriteLine($"DEBUG: ExtractFieldsWithNormalization called with prefix='{prefix}', ValueKind={element.ValueKind}");
                
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        try
                        {
                            var properties = element.EnumerateObject().ToList();
                            Console.WriteLine($"DEBUG: Found {properties.Count} properties in object");
                            
                            foreach (var property in properties)
                            {
                                var fieldName = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}_{property.Name}";
                                Console.WriteLine($"DEBUG: Processing object property: {property.Name} -> {fieldName} (ValueKind: {property.Value.ValueKind})");
                            
                                // For nested objects, create child tables instead of flattening
                                if (property.Value.ValueKind == JsonValueKind.Object)
                                {
                                    // Only create child table if this is a top-level nested object (no prefix)
                                    // This prevents infinitely nested structures from creating too many tables
                                    if (string.IsNullOrEmpty(prefix))
                                    {
                                        var tableName = property.Name;
                                        Console.WriteLine($"DEBUG: Creating child table for nested object: {tableName}");
                                        
                                        if (!childTables.ContainsKey(tableName))
                                        {
                                            childTables[tableName] = new ChildTableInfo
                                            {
                                                SourceType = "NestedObject",
                                                FieldSamples = new List<Dictionary<string, FieldInfo>>()
                                            };
                                        }
                                        
                                        // Extract fields from the nested object
                                        var childFields = new Dictionary<string, FieldInfo>();
                                        ExtractFieldsFlat(property.Value, "", childFields);
                                        if (childFields.Any())
                                        {
                                            childTables[tableName].FieldSamples.Add(childFields);
                                        }
                                    }
                                    else
                                    {
                                        // For deeply nested objects, flatten them
                                        ExtractFieldsWithNormalization(property.Value, fieldName, mainFields, childTables, arrayValueFrequency);
                                    }
                                }
                                else if (property.Value.ValueKind == JsonValueKind.Array)
                                {
                                    ExtractFieldsWithNormalization(property.Value, fieldName, mainFields, childTables, arrayValueFrequency);
                                }
                                else
                                {
                                    // This is a scalar field - add it to main fields
                                    Console.WriteLine($"DEBUG: Adding scalar field: {fieldName}");
                                    
                                    if (!mainFields.ContainsKey(fieldName))
                                    {
                                        mainFields[fieldName] = new FieldInfo
                                        {
                                            FieldName = fieldName,
                                            DetectedTypes = new List<string>(),
                                            IsNested = fieldName.Contains('_'),
                                            IsRequired = true, // Default to required
                                            RecommendedSqlType = ""
                                        };
                                    }

                                    var sqlType = MapJsonTypeToSqlTypeEnhanced(property.Value);
                                    Console.WriteLine($"DEBUG: Mapped {fieldName} to SQL type: {sqlType}");
                                    
                                    if (!mainFields[fieldName].DetectedTypes.Contains(sqlType))
                                    {
                                        mainFields[fieldName].DetectedTypes.Add(sqlType);
                                    }
                                    
                                    // Update recommended SQL type
                                    mainFields[fieldName].RecommendedSqlType = GetRecommendedSqlType(mainFields[fieldName].DetectedTypes);
                                    
                                    Console.WriteLine($"DEBUG: Field {fieldName} final info - SQL Type: {mainFields[fieldName].RecommendedSqlType}, Detected Types: [{string.Join(", ", mainFields[fieldName].DetectedTypes)}], Required: {mainFields[fieldName].IsRequired}");
                                }
                            }
                        }
                        catch (Exception objEx)
                        {
                            Console.WriteLine($"DEBUG: Error enumerating object properties: {objEx.Message}");
                            _logger.LogError(objEx, "Error enumerating object properties in document");
                        }
                        break;

                    case JsonValueKind.Array:
                        if (element.GetArrayLength() > 0)
                        {
                            var tableName = string.IsNullOrEmpty(prefix) ? "child_table" : prefix;
                            Console.WriteLine($"DEBUG: Analyzing array: {tableName} with {element.GetArrayLength()} items");
                            
                            // Analyze array content to determine storage strategy
                            var arrayAnalysis = AnalyzeArrayStructure(element, tableName);
                            
                            // Track array values for many-to-many analysis
                            TrackArrayValues(element, tableName, arrayValueFrequency);
                            
                            if (arrayAnalysis.ShouldCreateTable)
                            {
                                // Create child table for complex arrays
                                Console.WriteLine($"DEBUG: Creating child table for complex array: {tableName}");
                                
                                if (!childTables.ContainsKey(tableName))
                                {
                                    childTables[tableName] = new ChildTableInfo
                                    {
                                        SourceType = "Array",
                                        FieldSamples = new List<Dictionary<string, FieldInfo>>()
                                    };
                                }

                                // Analyze each item in the array to understand the child table structure
                                foreach (var arrayItem in element.EnumerateArray().Take(10)) // Sample first 10 items
                                {
                                    var childFields = new Dictionary<string, FieldInfo>();
                                    ExtractFieldsFlat(arrayItem, "", childFields);
                                    if (childFields.Any())
                                    {
                                        childTables[tableName].FieldSamples.Add(childFields);
                                    }
                                }
                            }
                            else
                            {
                                // Store primitive array as a single field in main table
                                Console.WriteLine($"DEBUG: Storing primitive array {tableName} as {arrayAnalysis.RecommendedStorage}");
                                
                                if (!mainFields.ContainsKey(tableName))
                                {
                                    mainFields[tableName] = new FieldInfo
                                    {
                                        FieldName = tableName,
                                        DetectedTypes = new List<string>(),
                                        IsNested = tableName.Contains('_'),
                                        RecommendedSqlType = arrayAnalysis.RecommendedSqlType,
                                        IsRequired = false // Arrays can be empty
                                    };
                                }
                                
                                if (!mainFields[tableName].DetectedTypes.Contains(arrayAnalysis.RecommendedSqlType))
                                {
                                    mainFields[tableName].DetectedTypes.Add(arrayAnalysis.RecommendedSqlType);
                                }
                                
                                mainFields[tableName].RecommendedSqlType = arrayAnalysis.RecommendedSqlType;
                            }
                        }
                        break;

                    default:
                        // This shouldn't happen at the top level, but handle it just in case
                        if (!string.IsNullOrEmpty(prefix))
                        {
                            Console.WriteLine($"DEBUG: Adding default scalar field: {prefix}");
                            
                            if (!mainFields.ContainsKey(prefix))
                            {
                                mainFields[prefix] = new FieldInfo
                                {
                                    FieldName = prefix,
                                    DetectedTypes = new List<string>(),
                                    IsNested = prefix.Contains('_'),
                                    RecommendedSqlType = ""
                                };
                            }

                            var sqlType = MapJsonTypeToSqlTypeEnhanced(element);
                            if (!mainFields[prefix].DetectedTypes.Contains(sqlType))
                            {
                                mainFields[prefix].DetectedTypes.Add(sqlType);
                            }
                            
                            mainFields[prefix].RecommendedSqlType = GetRecommendedSqlType(mainFields[prefix].DetectedTypes);
                        }
                        break;
                }
                
                Console.WriteLine($"DEBUG: ExtractFieldsWithNormalization completed. Current main fields: {mainFields.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Error in ExtractFieldsWithNormalization: {ex.Message}");
                _logger.LogError(ex, "Error extracting fields from JSON element with prefix {Prefix}", prefix);
            }
        }

        private void ExtractFieldsFlat(JsonElement element, string prefix, Dictionary<string, FieldInfo> fields)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var fieldName = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}_{property.Name}";
                        ExtractFieldsFlat(property.Value, fieldName, fields);
                    }
                    break;

                case JsonValueKind.Array:
                    // For arrays within child tables, we'll store as JSON for simplicity
                    var arrayFieldName = string.IsNullOrEmpty(prefix) ? "ArrayValue" : prefix;
                    if (!fields.ContainsKey(arrayFieldName))
                    {
                        fields[arrayFieldName] = new FieldInfo
                        {
                            FieldName = arrayFieldName,
                            DetectedTypes = new List<string> { "NVARCHAR(MAX)" },
                            RecommendedSqlType = "NVARCHAR(MAX)",
                            IsNested = false
                        };
                    }
                    break;

                default:
                    // Handle primitive values - if prefix is empty, this is a primitive array item
                    var primitiveFieldName = string.IsNullOrEmpty(prefix) ? "Value" : prefix;
                    if (!fields.ContainsKey(primitiveFieldName))
                    {
                        fields[primitiveFieldName] = new FieldInfo
                        {
                            FieldName = primitiveFieldName,
                            DetectedTypes = new List<string>(),
                            IsNested = false,
                            RecommendedSqlType = ""
                        };
                    }

                    var sqlType = MapJsonTypeToSqlTypeEnhanced(element);
                    if (!fields[primitiveFieldName].DetectedTypes.Contains(sqlType))
                    {
                        fields[primitiveFieldName].DetectedTypes.Add(sqlType);
                    }
                    
                    fields[primitiveFieldName].RecommendedSqlType = GetRecommendedSqlType(fields[primitiveFieldName].DetectedTypes);
                    break;
            }
        }

        /// <summary>
        /// Analyzes array structure to determine optimal storage strategy
        /// </summary>
        private ArrayAnalysis AnalyzeArrayStructure(JsonElement arrayElement, string arrayName)
        {
            var analysis = new ArrayAnalysis
            {
                ArrayName = arrayName,
                ItemCount = arrayElement.GetArrayLength(),
                ShouldCreateTable = false,
                RecommendedStorage = "JSON",
                RecommendedSqlType = "NVARCHAR(MAX)"
            };

            if (analysis.ItemCount == 0)
            {
                return analysis;
            }

            // Sample array items to understand structure
            var sampleItems = arrayElement.EnumerateArray().Take(Math.Min(10, analysis.ItemCount)).ToList();
            var itemTypes = new HashSet<JsonValueKind>();
            var hasComplexStructure = false;
            var maxStringLength = 0;
            var totalComplexity = 0;

            foreach (var item in sampleItems)
            {
                itemTypes.Add(item.ValueKind);
                
                switch (item.ValueKind)
                {
                    case JsonValueKind.Object:
                        hasComplexStructure = true;
                        totalComplexity += CountObjectProperties(item);
                        break;
                        
                    case JsonValueKind.Array:
                        hasComplexStructure = true;
                        totalComplexity += item.GetArrayLength();
                        break;
                        
                    case JsonValueKind.String:
                        var stringValue = item.GetString() ?? "";
                        maxStringLength = Math.Max(maxStringLength, stringValue.Length);
                        break;
                }
            }

            // Decision logic for storage strategy
            if (hasComplexStructure)
            {
                // Complex structures (objects/nested arrays) should get their own tables
                analysis.ShouldCreateTable = true;
                analysis.RecommendedStorage = "RelationalTable";
                analysis.RecommendedSqlType = "FK_Reference";
            }
            else if (itemTypes.Count == 1 && itemTypes.First() == JsonValueKind.String)
            {
                // Arrays of strings - decision based on usage patterns
                if (IsLikelyTagsOrCategories(arrayName) && maxStringLength <= 100)
                {
                    // Small string arrays (tags, categories) - use delimited string for better query performance
                    analysis.RecommendedStorage = "DelimitedString";
                    analysis.RecommendedSqlType = "NVARCHAR(MAX)"; // Use MAX for flexibility
                    analysis.TransformationLogic = "Store as comma-delimited string for efficient tag queries";
                }
                else if (analysis.ItemCount > 20 || maxStringLength > 500)
                {
                    // Large or long string arrays - use separate table
                    analysis.ShouldCreateTable = true;
                    analysis.RecommendedStorage = "RelationalTable";
                    analysis.RecommendedSqlType = "FK_Reference";
                }
                else
                {
                    // Medium string arrays - store as JSON
                    analysis.RecommendedStorage = "JSON";
                    analysis.RecommendedSqlType = "NVARCHAR(MAX)";
                    analysis.TransformationLogic = "Store as JSON array for flexible querying";
                }
            }
            else if (itemTypes.Count == 1)
            {
                // Arrays of other single primitive types (numbers, booleans)
                var primitiveType = itemTypes.First();
                if (analysis.ItemCount <= 10)
                {
                    // Small primitive arrays - store as delimited string
                    analysis.RecommendedStorage = "DelimitedString";
                    analysis.RecommendedSqlType = primitiveType switch
                    {
                        JsonValueKind.Number => "NVARCHAR(500)", // "1,2,3,4,5"
                        JsonValueKind.True or JsonValueKind.False => "NVARCHAR(100)", // "true,false,true"
                        _ => "NVARCHAR(MAX)"
                    };
                }
                else
                {
                    // Large primitive arrays - separate table might be better
                    analysis.ShouldCreateTable = true;
                    analysis.RecommendedStorage = "RelationalTable";
                    analysis.RecommendedSqlType = "FK_Reference";
                }
            }
            else
            {
                // Mixed types - store as JSON
                analysis.RecommendedStorage = "JSON";
                analysis.RecommendedSqlType = "NVARCHAR(MAX)";
                analysis.TransformationLogic = "Mixed types array stored as JSON";
            }

            return analysis;
        }

        private int CountObjectProperties(JsonElement objectElement)
        {
            try
            {
                return objectElement.EnumerateObject().Count();
            }
            catch
            {
                return 0;
            }
        }

        private bool IsLikelyTagsOrCategories(string fieldName)
        {
            var lowerName = fieldName.ToLowerInvariant();
            return lowerName.Contains("tag") || 
                   lowerName.Contains("category") || 
                   lowerName.Contains("label") ||
                   lowerName.Contains("preference") ||
                   lowerName.Contains("channel") ||
                   lowerName.Contains("alert") ||
                   lowerName.Contains("scope") ||
                   lowerName.Contains("service") ||
                   lowerName.Contains("account");
        }

        /// <summary>
        /// Tracks array values across documents for many-to-many relationship analysis
        /// </summary>
        private void TrackArrayValues(JsonElement arrayElement, string arrayName, Dictionary<string, Dictionary<string, int>> arrayValueFrequency)
        {
            if (!arrayValueFrequency.ContainsKey(arrayName))
            {
                arrayValueFrequency[arrayName] = new Dictionary<string, int>();
            }

            var valueTracker = arrayValueFrequency[arrayName];

            foreach (var item in arrayElement.EnumerateArray())
            {
                string value = ExtractValueForTracking(item);
                if (!string.IsNullOrEmpty(value))
                {
                    valueTracker[value] = valueTracker.GetValueOrDefault(value, 0) + 1;
                }
            }
        }

        private string ExtractValueForTracking(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object => element.GetRawText(), // For small objects, track the full JSON
                _ => ""
            };
        }

        /// <summary>
        /// Analyzes array value frequencies and recommends many-to-many relationships
        /// </summary>
        private void ApplyManyToManyAnalysis(Dictionary<string, ChildTableSchema> childTables, 
            Dictionary<string, Dictionary<string, int>> arrayValueFrequency, int totalDocuments)
        {
            foreach (var arrayField in arrayValueFrequency)
            {
                var arrayName = arrayField.Key;
                var valueCounts = arrayField.Value;
                
                // Calculate statistics
                var totalValues = valueCounts.Values.Sum();
                var uniqueValues = valueCounts.Count;
                var avgValuesPerDocument = totalDocuments > 0 ? (double)totalValues / totalDocuments : 0;
                var maxFrequency = valueCounts.Values.Max();
                var sharedValuePercentage = totalDocuments > 0 ? (double)maxFrequency / totalDocuments : 0;

                Console.WriteLine($"DEBUG: Array {arrayName}: {uniqueValues} unique values, {avgValuesPerDocument:F2} avg per doc, {sharedValuePercentage:P0} max reuse");

                // Decision criteria for many-to-many relationships
                bool shouldBeManyToMany = ShouldCreateManyToManyRelationship(
                    uniqueValues, totalValues, totalDocuments, sharedValuePercentage, arrayName);

                if (shouldBeManyToMany)
                {
                    // Update the child table schema to indicate many-to-many
                    if (childTables.ContainsKey(arrayName))
                    {
                        var childTable = childTables[arrayName];
                        childTable.ChildTableType = "ManyToMany";
                        childTable.RecommendedIndexes.Add(new IndexRecommendation
                        {
                            IndexName = $"IX_{arrayName}_Value_Lookup",
                            Columns = new List<string> { "Value" },
                            IndexType = "NONCLUSTERED",
                            Justification = $"Support lookups by {arrayName} value (appears in {sharedValuePercentage:P0} of documents)"
                        });

                        // Add transformation guidance
                        childTable.TransformationNotes.Add($"MANY-TO-MANY: {uniqueValues} unique values shared across {totalDocuments} documents");
                        childTable.TransformationNotes.Add($"Consider creating reference table '{arrayName}Values' with junction table");
                        childTable.TransformationNotes.Add($"Most frequent value appears in {sharedValuePercentage:P0} of documents");
                    }

                    _logger.LogInformation("Recommended many-to-many relationship for {ArrayName}: {UniqueValues} unique values, {SharePercentage:P0} max sharing", 
                        arrayName, uniqueValues, sharedValuePercentage);
                }
            }
        }

        private bool ShouldCreateManyToManyRelationship(int uniqueValues, int totalValues, int totalDocuments, 
            double maxSharePercentage, string arrayName)
        {
            // Don't recommend many-to-many for very small datasets
            if (totalDocuments < 5 || uniqueValues < 3)
                return false;

            // Strong indicators for many-to-many:
            // 1. High value reuse (same value appears in multiple documents)
            if (maxSharePercentage >= 0.3) // 30% or more documents share the same value
                return true;

            // 2. Good ratio of unique values to total values (indicates reuse)
            var reuseRatio = totalValues > 0 ? (double)uniqueValues / totalValues : 0;
            if (reuseRatio <= 0.7 && uniqueValues >= 10) // 70% reuse or better with decent volume
                return true;

            // 3. Semantic indicators (field names suggest reference data)
            if (IsLikelyReferenceData(arrayName) && uniqueValues >= 5)
                return true;

            return false;
        }

        private bool IsLikelyReferenceData(string fieldName)
        {
            var lowerName = fieldName.ToLowerInvariant();
            return lowerName.Contains("id") ||
                   lowerName.Contains("code") ||
                   lowerName.Contains("type") ||
                   lowerName.Contains("status") ||
                   lowerName.Contains("category") ||
                   lowerName.Contains("classification") ||
                   lowerName.Contains("department") ||
                   lowerName.Contains("team") ||
                   lowerName.Contains("role");
        }

        private void ExtractFields(JsonElement element, string prefix, Dictionary<string, FieldInfo> fields)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var fieldName = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                        ExtractFields(property.Value, fieldName, fields);
                    }
                    break;

                case JsonValueKind.Array:
                    if (element.GetArrayLength() > 0)
                    {
                        var firstElement = element.EnumerateArray().First();
                        ExtractFields(firstElement, $"{prefix}[]", fields);
                    }
                    break;

                default:
                    if (!fields.ContainsKey(prefix))
                    {
                        fields[prefix] = new FieldInfo
                        {
                            FieldName = prefix,
                            DetectedTypes = new List<string>(),
                            IsNested = prefix.Contains('.'),
                            RecommendedSqlType = ""
                        };
                    }

                    var sqlType = MapJsonTypeToSqlType(element);
                    if (!fields[prefix].DetectedTypes.Contains(sqlType))
                    {
                        fields[prefix].DetectedTypes.Add(sqlType);
                    }

                    // Update recommended SQL type (prioritize more specific types)
                    fields[prefix].RecommendedSqlType = GetRecommendedSqlType(fields[prefix].DetectedTypes);

                    // Update max length for strings
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var length = element.GetString()?.Length ?? 0;
                        fields[prefix].MaxLength = Math.Max(fields[prefix].MaxLength, length);
                    }
                    break;
            }
        }

        private string MapJsonTypeToSqlType(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => "NVARCHAR",
                JsonValueKind.Number => element.TryGetInt64(out _) ? "BIGINT" : "DECIMAL",
                JsonValueKind.True or JsonValueKind.False => "BIT",
                JsonValueKind.Null => "NULL",
                JsonValueKind.Object => "NVARCHAR(MAX)", // JSON
                JsonValueKind.Array => "NVARCHAR(MAX)", // JSON array
                _ => "NVARCHAR(MAX)"
            };
        }

        private string MapJsonTypeToSqlTypeEnhanced(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => AnalyzeStringType(element),
                JsonValueKind.Number => AnalyzeNumberType(element),
                JsonValueKind.True or JsonValueKind.False => "BIT",
                JsonValueKind.Null => "NULL",
                JsonValueKind.Object => "NVARCHAR(MAX)", // JSON
                JsonValueKind.Array => "NVARCHAR(MAX)", // JSON array
                _ => "NVARCHAR(MAX)"
            };
        }

        private string AnalyzeStringType(JsonElement element)
        {
            var value = element.GetString();
            if (string.IsNullOrEmpty(value))
                return "NVARCHAR(50)";

            // Check for common patterns
            if (DateTime.TryParse(value, out _))
                return "DATETIME2";
            
            if (Guid.TryParse(value, out _))
                return "UNIQUEIDENTIFIER";

            // Analyze length for appropriate VARCHAR sizing
            var length = value.Length;
            return length switch
            {
                <= 50 => "NVARCHAR(50)",
                <= 100 => "NVARCHAR(100)",
                <= 255 => "NVARCHAR(255)",
                <= 1000 => "NVARCHAR(1000)",
                _ => "NVARCHAR(MAX)"
            };
        }

        private string AnalyzeNumberType(JsonElement element)
        {
            if (element.TryGetInt32(out var intValue))
            {
                return intValue switch
                {
                    >= -128 and <= 127 => "TINYINT",
                    >= -32768 and <= 32767 => "SMALLINT",
                    _ => "INT"
                };
            }
            
            if (element.TryGetInt64(out var longValue))
                return "BIGINT";
            
            if (element.TryGetDecimal(out var decimalValue))
            {
                // Analyze precision and scale
                var decimalString = decimalValue.ToString();
                var decimalPlaces = decimalString.Contains('.') ? 
                    decimalString.Split('.')[1].Length : 0;
                
                return decimalPlaces switch
                {
                    0 => "BIGINT",
                    <= 2 => "DECIMAL(18,2)", // Common for currency
                    <= 4 => "DECIMAL(18,4)",
                    _ => "DECIMAL(18,6)"
                };
            }
            
            return "DECIMAL(18,2)";
        }

        private string GetRecommendedSqlType(List<string> detectedTypes)
        {
            if (!detectedTypes.Any())
                return "NVARCHAR(MAX)";

            // Prioritize more specific types over generic ones
            if (detectedTypes.Contains("UNIQUEIDENTIFIER"))
                return "UNIQUEIDENTIFIER";
            if (detectedTypes.Contains("DATETIME2"))
                return "DATETIME2";
            if (detectedTypes.Contains("DATE"))
                return "DATE";
            if (detectedTypes.Contains("DECIMAL"))
                return detectedTypes.FirstOrDefault(t => t.StartsWith("DECIMAL(")) ?? "DECIMAL(18,2)";
            if (detectedTypes.Contains("BIGINT"))
                return "BIGINT";
            if (detectedTypes.Contains("INT"))
                return "INT";
            if (detectedTypes.Contains("SMALLINT"))
                return "SMALLINT";
            if (detectedTypes.Contains("TINYINT"))
                return "TINYINT";
            if (detectedTypes.Contains("BIT"))
                return "BIT";
            if (detectedTypes.Any(t => t.StartsWith("VARCHAR(")))
                return detectedTypes.FirstOrDefault(t => t.StartsWith("VARCHAR(")) ?? "VARCHAR(255)";
            if (detectedTypes.Contains("NVARCHAR"))
                return detectedTypes.FirstOrDefault(t => t.StartsWith("NVARCHAR(")) ?? "NVARCHAR(255)";
            
            return "NVARCHAR(MAX)";
        }

        private async Task<PerformanceMetrics> CollectPerformanceMetricsAsync(CancellationToken cancellationToken)
        {
            var metrics = new PerformanceMetrics();
            
            if (_logsQueryClient == null)
            {
                _logger.LogWarning("Azure Monitor client not available for performance metrics collection");
                return metrics;
            }

            var workspaceId = _configuration["AzureMonitor:WorkspaceId"];
            var accountName = _configuration["AzureMonitor:CosmosAccountName"];

            if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(accountName))
            {
                _logger.LogWarning("Azure Monitor configuration incomplete");
                return metrics;
            }

            try
            {
                // Set analysis period to last 6 months
                var endTime = DateTime.UtcNow;
                var startTime = endTime.AddMonths(-6);
                
                metrics.AnalysisPeriod = new TimeRange 
                { 
                    StartTime = startTime, 
                    EndTime = endTime 
                };

                // Query for RU consumption metrics
                var ruQuery = $@"
                    AzureMetrics
                    | where TimeGenerated >= datetime({startTime:yyyy-MM-dd})
                    | where TimeGenerated <= datetime({endTime:yyyy-MM-dd})
                    | where ResourceProvider == 'MICROSOFT.DOCUMENTDB'
                    | where Resource =~ '{accountName}'
                    | where MetricName == 'TotalRequestUnits'
                    | summarize 
                        AvgRUs = avg(Average),
                        MaxRUs = max(Maximum),
                        TotalRUs = sum(Total)
                    by bin(TimeGenerated, 1h)
                    | order by TimeGenerated asc";

                var response = await _logsQueryClient.QueryWorkspaceAsync(
                    workspaceId, 
                    ruQuery, 
                    new QueryTimeRange(startTime, endTime),
                    cancellationToken: cancellationToken);

                if (response.Value != null)
                {
                    var ruData = response.Value.Table.Rows;
                    if (ruData.Any())
                    {
                        metrics.AverageRUsPerSecond = ruData.Average(row => Convert.ToDouble(row[1]));
                        metrics.PeakRUsPerSecond = ruData.Max(row => Convert.ToDouble(row[2]));
                        metrics.TotalRUConsumption = ruData.Sum(row => Convert.ToDouble(row[3]));
                    }

                    _logger.LogInformation("Successfully collected performance metrics from Azure Monitor");
                }
                else
                {
                    _logger.LogWarning("Azure Monitor query returned null response");
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting performance metrics from Azure Monitor");
            }

            return metrics;
        }

        public void Dispose()
        {
            _cosmosClient?.Dispose();
        }
    }

    /// <summary>
    /// Helper class to track child table information including type and field samples
    /// Used during document analysis to determine whether child tables originated from arrays or nested objects
    /// </summary>
    internal class ChildTableInfo
    {
        /// <summary>
        /// Gets or sets the source type of the child table.
        /// Valid values: "Array", "NestedObject", "ManyToMany"
        /// </summary>
        public string SourceType { get; set; } = "Array";
        
        /// <summary>
        /// Gets or sets the field samples collected from the source structure.
        /// Each dictionary represents a sample document's fields for schema consolidation.
        /// </summary>
        public List<Dictionary<string, FieldInfo>> FieldSamples { get; set; } = new();
    }
}
