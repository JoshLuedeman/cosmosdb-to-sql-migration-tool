using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Service for performing comprehensive data quality analysis before migration
    /// Identifies potential issues that could impact migration success
    /// </summary>
    public class DataQualityAnalysisService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DataQualityAnalysisService> _logger;
        private readonly CosmosClient _cosmosClient;
        private readonly DataQualityAnalysisOptions _options;

        public DataQualityAnalysisService(
            IConfiguration configuration, 
            ILogger<DataQualityAnalysisService> logger)
        {
            _configuration = configuration;
            _logger = logger;

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
            _options = new DataQualityAnalysisOptions();
        }

        /// <summary>
        /// Performs comprehensive data quality analysis on Cosmos DB database
        /// </summary>
        public async Task<DataQualityAnalysis> AnalyzeDataQualityAsync(
            CosmosDbAnalysis cosmosAnalysis,
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting data quality analysis for database: {DatabaseName}", databaseName);

            var analysis = new DataQualityAnalysis
            {
                AnalysisDate = DateTime.UtcNow
            };

            var database = _cosmosClient.GetDatabase(databaseName);

            // Analyze each container
            foreach (var containerInfo in cosmosAnalysis.Containers)
            {
                _logger.LogInformation("Analyzing data quality for container: {ContainerName}", containerInfo.ContainerName);
                
                var containerAnalysis = await AnalyzeContainerQualityAsync(
                    database.GetContainer(containerInfo.ContainerName),
                    containerInfo,
                    cancellationToken);

                analysis.ContainerAnalyses.Add(containerAnalysis);
                analysis.TotalDocumentsAnalyzed += containerAnalysis.SampleSize;
            }

            // Aggregate results
            AggregateResults(analysis);

            // Generate top issues and summary
            GenerateTopIssues(analysis);
            GenerateSummary(analysis);

            _logger.LogInformation(
                "Data quality analysis completed. Found {CriticalCount} critical, {WarningCount} warning, {InfoCount} info issues",
                analysis.CriticalIssuesCount,
                analysis.WarningIssuesCount,
                analysis.InfoIssuesCount);

            return analysis;
        }

        private async Task<ContainerQualityAnalysis> AnalyzeContainerQualityAsync(
            Container container,
            ContainerAnalysis containerInfo,
            CancellationToken cancellationToken)
        {
            var analysis = new ContainerQualityAnalysis
            {
                ContainerName = containerInfo.ContainerName,
                DocumentCount = containerInfo.DocumentCount
            };

            var documents = await SampleDocumentsAsync(container, _options.SampleSize, cancellationToken);
            analysis.SampleSize = documents.Count;

            if (documents.Count == 0)
            {
                _logger.LogWarning("No documents found in container: {ContainerName}", containerInfo.ContainerName);
                return analysis;
            }

            analysis.NullAnalysis = await AnalyzeNullValuesAsync(documents, containerInfo);
            analysis.DuplicateAnalysis = await AnalyzeDuplicatesAsync(documents, containerInfo);
            analysis.TypeConsistency = await AnalyzeTypeConsistencyAsync(documents, containerInfo);
            
            if (_options.IncludeOutlierDetection)
            {
                analysis.OutlierAnalysis = await AnalyzeOutliersAsync(documents, containerInfo);
            }
            
            analysis.StringLengthAnalysis = await AnalyzeStringLengthsAsync(documents, containerInfo);
            
            if (_options.IncludeEncodingChecks)
            {
                analysis.EncodingIssues = await AnalyzeEncodingIssuesAsync(documents, containerInfo);
            }
            
            analysis.DateValidation = await AnalyzeDateValidityAsync(documents, containerInfo);
            GenerateIssuesFromAnalyses(analysis);

            return analysis;
        }

        private async Task<List<JsonDocument>> SampleDocumentsAsync(
            Container container,
            int sampleSize,
            CancellationToken cancellationToken)
        {
            var documents = new List<JsonDocument>();

            try
            {
                var query = new QueryDefinition($"SELECT TOP {sampleSize} * FROM c");
                var iterator = container.GetItemQueryIterator<JsonDocument>(query);

                while (iterator.HasMoreResults && documents.Count < sampleSize)
                {
                    var response = await iterator.ReadNextAsync(cancellationToken);
                    documents.AddRange(response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sampling documents from container");
                throw;
            }

            return documents;
        }

        private Task<List<NullAnalysisResult>> AnalyzeNullValuesAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<NullAnalysisResult>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var nullCount = 0;
                var missingCount = 0;
                var sampleNullDocs = new List<string>();

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    
                    if (value == null)
                    {
                        missingCount++;
                        if (sampleNullDocs.Count < _options.MaxSampleRecords)
                        {
                            sampleNullDocs.Add(GetDocumentId(doc));
                        }
                    }
                    else if (IsNullValue(value))
                    {
                        nullCount++;
                        if (sampleNullDocs.Count < _options.MaxSampleRecords)
                        {
                            sampleNullDocs.Add(GetDocumentId(doc));
                        }
                    }
                }

                var totalDocs = documents.Count;
                var nullPercentage = (double)nullCount / totalDocs;
                var missingPercentage = (double)missingCount / totalDocs;
                var totalNullPercentage = nullPercentage + missingPercentage;

                if (totalNullPercentage > 0.01)
                {
                    results.Add(new NullAnalysisResult
                    {
                        FieldName = GetFieldName(fieldPath),
                        FieldPath = fieldPath,
                        TotalDocuments = totalDocs,
                        NullCount = nullCount,
                        MissingCount = missingCount,
                        NullPercentage = nullPercentage * 100,
                        MissingPercentage = missingPercentage * 100,
                        IsRecommendedRequired = totalNullPercentage < 0.05,
                        WillImpactNotNullConstraint = totalNullPercentage > 0,
                        SampleNullDocumentIds = sampleNullDocs,
                        RecommendedAction = GetNullRecommendation(totalNullPercentage)
                    });
                }
            }

            return Task.FromResult(results);
        }

        private Task<List<DuplicateAnalysisResult>> AnalyzeDuplicatesAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<DuplicateAnalysisResult>();

            if (!_options.IncludeDuplicateDetection || documents.Count == 0)
            {
                return Task.FromResult(results);
            }

            var idDuplicates = FindDuplicatesByField(documents, "id");
            if (idDuplicates.DuplicateGroupCount > 0)
            {
                results.Add(idDuplicates);
            }

            if (!string.IsNullOrEmpty(containerInfo.PartitionKey))
            {
                var partitionKeyField = containerInfo.PartitionKey.TrimStart('/');
                var pkDuplicates = FindDuplicatesByField(documents, partitionKeyField);
                if (pkDuplicates.DuplicateGroupCount > 0)
                {
                    results.Add(pkDuplicates);
                }
            }

            var businessKeyFields = new[] { "email", "Email", "username", "Username", "code", "Code" };
            foreach (var field in businessKeyFields)
            {
                if (documents.Any(d => GetFieldValue(d, field) != null))
                {
                    var duplicates = FindDuplicatesByField(documents, field);
                    if (duplicates.DuplicateGroupCount > 0)
                    {
                        results.Add(duplicates);
                    }
                }
            }

            return Task.FromResult(results);
        }

        private DuplicateAnalysisResult FindDuplicatesByField(List<JsonDocument> documents, string fieldPath)
        {
            var result = new DuplicateAnalysisResult
            {
                KeyType = fieldPath == "id" ? "ID" : "BusinessKey",
                KeyFields = new List<string> { fieldPath }
            };

            var groups = documents
                .Where(d => GetFieldValue(d, fieldPath) != null)
                .GroupBy(d => GetFieldValue(d, fieldPath)?.ToString() ?? "")
                .Where(g => g.Count() > 1)
                .Select(g => new DuplicateGroup
                {
                    KeyValue = g.Key,
                    OccurrenceCount = g.Count(),
                    DocumentIds = g.Select(d => GetDocumentId(d)).Take(_options.MaxSampleRecords).ToList(),
                    SampleData = GetDocumentSampleData(g.First())
                })
                .OrderByDescending(g => g.OccurrenceCount)
                .Take(10)
                .ToList();

            result.DuplicateGroupCount = groups.Count;
            result.TotalDuplicateRecords = groups.Sum(g => g.OccurrenceCount);
            result.DuplicatePercentage = documents.Count > 0 ? (double)result.TotalDuplicateRecords / documents.Count * 100 : 0;
            result.TopDuplicateGroups = groups.Take(5).ToList();
            result.RecommendedResolution = GetDuplicateRecommendation(fieldPath, result.DuplicatePercentage);

            return result;
        }

        private Task<List<TypeConsistencyResult>> AnalyzeTypeConsistencyAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<TypeConsistencyResult>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var typeDistribution = new Dictionary<string, long>();
                var mismatches = new List<TypeMismatchSample>();

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    if (value != null)
                    {
                        var typeName = GetTypeName(value);
                        if (!typeDistribution.ContainsKey(typeName))
                        {
                            typeDistribution[typeName] = 0;
                        }
                        typeDistribution[typeName]++;
                    }
                }

                if (typeDistribution.Count == 0) continue;

                var totalValues = typeDistribution.Values.Sum();
                var dominantType = typeDistribution.OrderByDescending(kvp => kvp.Value).First();
                var dominantPercentage = (double)dominantType.Value / totalValues * 100;
                var isConsistent = dominantPercentage > 95;

                if (!isConsistent)
                {
                    var expectedType = dominantType.Key;
                    foreach (var doc in documents.Take(100))
                    {
                        var value = GetFieldValue(doc, fieldPath);
                        if (value != null)
                        {
                            var actualType = GetTypeName(value);
                            if (actualType != expectedType && mismatches.Count < _options.MaxSampleRecords)
                            {
                                mismatches.Add(new TypeMismatchSample
                                {
                                    DocumentId = GetDocumentId(doc),
                                    ActualType = actualType,
                                    ExpectedType = expectedType,
                                    SampleValue = value
                                });
                            }
                        }
                    }

                    results.Add(new TypeConsistencyResult
                    {
                        FieldName = GetFieldName(fieldPath),
                        TypeDistribution = typeDistribution,
                        IsConsistent = isConsistent,
                        DominantType = dominantType.Key,
                        DominantTypePercentage = dominantPercentage,
                        Mismatches = mismatches,
                        RecommendedSqlType = MapToSqlType(dominantType.Key),
                        RecommendedAction = $"Consider type conversion or validation for {mismatches.Count} inconsistent values"
                    });
                }
            }

            return Task.FromResult(results);
        }

        private Task<List<OutlierAnalysisResult>> AnalyzeOutliersAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<OutlierAnalysisResult>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var numericValues = new List<(string DocId, double Value)>();

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    if (value != null && IsNumericType(value))
                    {
                        try
                        {
                            var numValue = Convert.ToDouble(value);
                            numericValues.Add((GetDocumentId(doc), numValue));
                        }
                        catch { }
                    }
                }

                if (numericValues.Count < 10) continue;

                var values = numericValues.Select(v => v.Value).OrderBy(v => v).ToList();
                var mean = values.Average();
                var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
                var stdDev = Math.Sqrt(variance);

                var q1Index = (int)(values.Count * 0.25);
                var q3Index = (int)(values.Count * 0.75);
                var q1 = values[q1Index];
                var q3 = values[q3Index];
                var iqr = q3 - q1;

                var lowerBound = q1 - 1.5 * iqr;
                var upperBound = q3 + 1.5 * iqr;

                var outliers = new List<OutlierSample>();
                foreach (var (docId, value) in numericValues)
                {
                    if (value < lowerBound || value > upperBound)
                    {
                        var zScore = stdDev > 0 ? (value - mean) / stdDev : 0;
                        if (outliers.Count < _options.MaxSampleRecords)
                        {
                            outliers.Add(new OutlierSample
                            {
                                DocumentId = docId,
                                Value = value,
                                ZScore = zScore,
                                OutlierType = value < lowerBound ? "Low" : "High"
                            });
                        }
                    }
                }

                if (outliers.Count > 0)
                {
                    results.Add(new OutlierAnalysisResult
                    {
                        FieldName = GetFieldName(fieldPath),
                        TotalValues = values.Count,
                        Mean = mean,
                        Median = values[values.Count / 2],
                        StandardDeviation = stdDev,
                        MinValue = values.First(),
                        MaxValue = values.Last(),
                        Q1 = q1,
                        Q3 = q3,
                        IQR = iqr,
                        OutlierCount = outliers.Count,
                        OutlierPercentage = (double)outliers.Count / values.Count * 100,
                        OutlierSamples = outliers.Take(_options.MaxSampleRecords).ToList(),
                        RecommendedAction = GetOutlierRecommendation(outliers.Count, values.Count)
                    });
                }
            }

            return Task.FromResult(results);
        }

        private Task<List<StringLengthAnalysisResult>> AnalyzeStringLengthsAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<StringLengthAnalysisResult>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var stringLengths = new List<(string DocId, int Length, string Value)>();

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    if (value != null && value is string strValue)
                    {
                        stringLengths.Add((GetDocumentId(doc), strValue.Length, strValue));
                    }
                }

                if (stringLengths.Count < 5) continue;

                var lengths = stringLengths.Select(s => s.Length).OrderBy(l => l).ToList();
                var minLength = lengths.First();
                var maxLength = lengths.Last();
                var avgLength = lengths.Average();
                var medianLength = lengths[lengths.Count / 2];

                var p95Index = (int)(lengths.Count * 0.95);
                var p99Index = (int)(lengths.Count * 0.99);
                var p95Length = lengths[Math.Min(p95Index, lengths.Count - 1)];
                var p99Length = lengths[Math.Min(p99Index, lengths.Count - 1)];

                var distribution = new Dictionary<int, int>();
                var buckets = new[] { 10, 50, 100, 255, 500, 1000, 2000, 4000, 8000 };
                foreach (var bucket in buckets)
                {
                    distribution[bucket] = lengths.Count(l => l <= bucket);
                }

                var extremeSamples = stringLengths
                    .Where(s => s.Length == maxLength || s.Length > p99Length)
                    .Take(_options.MaxSampleRecords)
                    .Select(s => new StringLengthSample
                    {
                        DocumentId = s.DocId,
                        Length = s.Length,
                        PreviewText = s.Value.Length > 100 ? s.Value.Substring(0, 100) + "..." : s.Value
                    })
                    .ToList();

                var recommendedSqlType = maxLength <= 255 ? $"NVARCHAR({Math.Max(maxLength, p95Length)})" :
                                        maxLength <= 4000 ? $"NVARCHAR({p99Length})" :
                                        "NVARCHAR(MAX)";

                results.Add(new StringLengthAnalysisResult
                {
                    FieldName = GetFieldName(fieldPath),
                    TotalValues = lengths.Count,
                    MinLength = minLength,
                    MaxLength = maxLength,
                    AverageLength = avgLength,
                    MedianLength = medianLength,
                    P95Length = p95Length,
                    P99Length = p99Length,
                    LengthDistribution = distribution,
                    RecommendedSqlType = recommendedSqlType,
                    ExtremeValueSamples = extremeSamples,
                    RecommendedAction = maxLength > _options.MaxStringLengthForVarchar
                        ? $"Consider using TEXT or NVARCHAR(MAX) due to max length of {maxLength}"
                        : $"Use {recommendedSqlType} based on 99th percentile length"
                });
            }

            return Task.FromResult(results);
        }

        private Task<List<EncodingIssue>> AnalyzeEncodingIssuesAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var issues = new List<EncodingIssue>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var nonAsciiSamples = new List<EncodingSample>();
                var controlCharSamples = new List<EncodingSample>();
                var emojiSamples = new List<EncodingSample>();

                var nonAsciiCount = 0;
                var controlCharCount = 0;
                var emojiCount = 0;

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    if (value != null && value is string strValue && !string.IsNullOrEmpty(strValue))
                    {
                        var hasNonAscii = strValue.Any(c => c > 127);
                        var hasControlChars = strValue.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
                        var hasEmoji = ContainsEmoji(strValue);

                        if (hasNonAscii)
                        {
                            nonAsciiCount++;
                            if (nonAsciiSamples.Count < _options.MaxSampleRecords)
                            {
                                nonAsciiSamples.Add(CreateEncodingSample(doc, strValue, "Non-ASCII characters found"));
                            }
                        }

                        if (hasControlChars)
                        {
                            controlCharCount++;
                            if (controlCharSamples.Count < _options.MaxSampleRecords)
                            {
                                controlCharSamples.Add(CreateEncodingSample(doc, strValue, "Control characters found"));
                            }
                        }

                        if (hasEmoji)
                        {
                            emojiCount++;
                            if (emojiSamples.Count < _options.MaxSampleRecords)
                            {
                                emojiSamples.Add(CreateEncodingSample(doc, strValue, "Emoji characters found"));
                            }
                        }
                    }
                }

                var totalStrings = documents.Count(d => GetFieldValue(d, fieldPath) is string);
                if (totalStrings == 0) continue;

                if (nonAsciiCount > 0)
                {
                    issues.Add(new EncodingIssue
                    {
                        FieldName = GetFieldName(fieldPath),
                        IssueType = "NonASCII",
                        AffectedDocumentCount = nonAsciiCount,
                        AffectedPercentage = (double)nonAsciiCount / totalStrings * 100,
                        Samples = nonAsciiSamples,
                        RecommendedAction = "Use NVARCHAR instead of VARCHAR to support Unicode characters"
                    });
                }

                if (controlCharCount > 0)
                {
                    issues.Add(new EncodingIssue
                    {
                        FieldName = GetFieldName(fieldPath),
                        IssueType = "ControlCharacters",
                        AffectedDocumentCount = controlCharCount,
                        AffectedPercentage = (double)controlCharCount / totalStrings * 100,
                        Samples = controlCharSamples,
                        RecommendedAction = "Review and sanitize control characters before migration"
                    });
                }

                if (emojiCount > 0)
                {
                    issues.Add(new EncodingIssue
                    {
                        FieldName = GetFieldName(fieldPath),
                        IssueType = "Emoji",
                        AffectedDocumentCount = emojiCount,
                        AffectedPercentage = (double)emojiCount / totalStrings * 100,
                        Samples = emojiSamples,
                        RecommendedAction = "Ensure SQL Server collation supports emoji (use UTF-8 collation)"
                    });
                }
            }

            return Task.FromResult(issues);
        }

        private Task<List<DateValidationResult>> AnalyzeDateValidityAsync(
            List<JsonDocument> documents,
            ContainerAnalysis containerInfo)
        {
            var results = new List<DateValidationResult>();
            var fieldPaths = GetAllFieldPaths(documents, containerInfo);

            foreach (var fieldPath in fieldPaths)
            {
                var dateValues = new List<(string DocId, DateTime? Date, string RawValue)>();
                var invalidCount = 0;
                var futureCount = 0;
                var oldCount = 0;
                var invalidSamples = new List<InvalidDateSample>();

                foreach (var doc in documents)
                {
                    var value = GetFieldValue(doc, fieldPath);
                    if (value != null)
                    {
                        var rawValue = value.ToString() ?? "";
                        DateTime? parsedDate = null;

                        if (DateTime.TryParse(rawValue, out var dt))
                        {
                            parsedDate = dt;
                        }
                        else if (value is DateTime dateTime)
                        {
                            parsedDate = dateTime;
                        }
                        else if (value is DateTimeOffset dateTimeOffset)
                        {
                            parsedDate = dateTimeOffset.DateTime;
                        }

                        if (parsedDate.HasValue)
                        {
                            dateValues.Add((GetDocumentId(doc), parsedDate, rawValue));

                            if (parsedDate.Value < _options.MinReasonableDate)
                            {
                                oldCount++;
                                if (invalidSamples.Count < _options.MaxSampleRecords)
                                {
                                    invalidSamples.Add(new InvalidDateSample
                                    {
                                        DocumentId = GetDocumentId(doc),
                                        RawValue = rawValue,
                                        IssueType = "TooOld",
                                        IssueDescription = $"Date {parsedDate:yyyy-MM-dd} is before minimum reasonable date"
                                    });
                                }
                            }
                            else if (parsedDate.Value > _options.MaxReasonableDate)
                            {
                                futureCount++;
                                if (invalidSamples.Count < _options.MaxSampleRecords)
                                {
                                    invalidSamples.Add(new InvalidDateSample
                                    {
                                        DocumentId = GetDocumentId(doc),
                                        RawValue = rawValue,
                                        IssueType = "Future",
                                        IssueDescription = $"Date {parsedDate:yyyy-MM-dd} is beyond maximum reasonable date"
                                    });
                                }
                            }
                        }
                        else if (LooksLikeDate(rawValue))
                        {
                            invalidCount++;
                            if (invalidSamples.Count < _options.MaxSampleRecords)
                            {
                                invalidSamples.Add(new InvalidDateSample
                                {
                                    DocumentId = GetDocumentId(doc),
                                    RawValue = rawValue,
                                    IssueType = "Invalid",
                                    IssueDescription = "Value appears to be a date but cannot be parsed"
                                });
                            }
                        }
                    }
                }

                var totalDateValues = dateValues.Count + invalidCount;
                if (totalDateValues > 0 && (invalidCount > 0 || futureCount > 0 || oldCount > 0))
                {
                    var validDates = dateValues.Where(d => d.Date.HasValue).Select(d => d.Date!.Value).ToList();
                    
                    results.Add(new DateValidationResult
                    {
                        FieldName = GetFieldName(fieldPath),
                        TotalValues = totalDateValues,
                        InvalidDateCount = invalidCount,
                        FutureDateCount = futureCount,
                        VeryOldDateCount = oldCount,
                        InvalidPercentage = (double)(invalidCount + futureCount + oldCount) / totalDateValues * 100,
                        MinDate = validDates.Any() ? validDates.Min() : null,
                        MaxDate = validDates.Any() ? validDates.Max() : null,
                        InvalidSamples = invalidSamples,
                        RecommendedAction = GetDateValidationRecommendation(invalidCount, futureCount, oldCount)
                    });
                }
            }

            return Task.FromResult(results);
        }

        private void GenerateIssuesFromAnalyses(ContainerQualityAnalysis analysis)
        {
            foreach (var nullResult in analysis.NullAnalysis)
            {
                var totalNullPercentage = nullResult.NullPercentage + nullResult.MissingPercentage;
                var severity = totalNullPercentage >= _options.NullThresholdCritical * 100 
                    ? DataQualitySeverity.Critical 
                    : totalNullPercentage >= _options.NullThresholdWarning * 100 
                        ? DataQualitySeverity.Warning 
                        : DataQualitySeverity.Info;

                analysis.AllIssues.Add(new DataQualityIssue
                {
                    ContainerName = analysis.ContainerName,
                    FieldName = nullResult.FieldName,
                    Severity = severity,
                    Category = "Null",
                    Title = $"{totalNullPercentage:F1}% null/missing values in {nullResult.FieldName}",
                    Description = $"Field '{nullResult.FieldName}' has {nullResult.NullCount} null values and {nullResult.MissingCount} missing values",
                    Impact = nullResult.WillImpactNotNullConstraint 
                        ? "Will prevent NOT NULL constraints and may require data cleanup or default values" 
                        : "May impact query results if NULL handling is not considered",
                    SampleRecordIds = nullResult.SampleNullDocumentIds,
                    Metrics = new Dictionary<string, object>
                    {
                        ["NullCount"] = nullResult.NullCount,
                        ["MissingCount"] = nullResult.MissingCount,
                        ["TotalPercentage"] = totalNullPercentage
                    },
                    Recommendations = new List<string> { nullResult.RecommendedAction }
                });
            }

            foreach (var dupResult in analysis.DuplicateAnalysis)
            {
                var severity = dupResult.KeyType == "ID" 
                    ? DataQualitySeverity.Critical 
                    : dupResult.DuplicatePercentage >= _options.DuplicateThresholdCritical * 100 
                        ? DataQualitySeverity.Critical 
                        : DataQualitySeverity.Warning;

                analysis.AllIssues.Add(new DataQualityIssue
                {
                    ContainerName = analysis.ContainerName,
                    FieldName = string.Join(", ", dupResult.KeyFields),
                    Severity = severity,
                    Category = "Duplicate",
                    Title = $"{dupResult.TotalDuplicateRecords} duplicate {dupResult.KeyType} values found",
                    Description = $"Found {dupResult.DuplicateGroupCount} groups with duplicate values ({dupResult.DuplicatePercentage:F1}% of records)",
                    Impact = dupResult.KeyType == "ID" 
                        ? "Critical: Duplicate IDs will prevent primary key constraints and cause migration failures" 
                        : "May indicate data quality issues or require deduplication logic",
                    SampleRecordIds = dupResult.TopDuplicateGroups.SelectMany(g => g.DocumentIds).Take(_options.MaxSampleRecords).ToList(),
                    Metrics = new Dictionary<string, object>
                    {
                        ["DuplicateGroups"] = dupResult.DuplicateGroupCount,
                        ["TotalDuplicates"] = dupResult.TotalDuplicateRecords
                    },
                    Recommendations = new List<string> { dupResult.RecommendedResolution }
                });
            }

            foreach (var typeResult in analysis.TypeConsistency)
            {
                analysis.AllIssues.Add(new DataQualityIssue
                {
                    ContainerName = analysis.ContainerName,
                    FieldName = typeResult.FieldName,
                    Severity = DataQualitySeverity.Warning,
                    Category = "Type",
                    Title = $"Type inconsistency in {typeResult.FieldName}",
                    Description = $"Field has {typeResult.TypeDistribution.Count} different types. Dominant: {typeResult.DominantType} ({typeResult.DominantTypePercentage:F1}%)",
                    Impact = "May cause type conversion errors during migration or data loss",
                    SampleRecordIds = typeResult.Mismatches.Select(m => m.DocumentId).ToList(),
                    Metrics = new Dictionary<string, object>
                    {
                        ["TypeCount"] = typeResult.TypeDistribution.Count,
                        ["DominantType"] = typeResult.DominantType
                    },
                    Recommendations = new List<string> { typeResult.RecommendedAction }
                });
            }

            foreach (var outlierResult in analysis.OutlierAnalysis)
            {
                if (outlierResult.OutlierPercentage > 1)
                {
                    analysis.AllIssues.Add(new DataQualityIssue
                    {
                        ContainerName = analysis.ContainerName,
                        FieldName = outlierResult.FieldName,
                        Severity = DataQualitySeverity.Info,
                        Category = "Outlier",
                        Title = $"{outlierResult.OutlierCount} outlier values in {outlierResult.FieldName}",
                        Description = $"Found {outlierResult.OutlierCount} extreme values ({outlierResult.OutlierPercentage:F1}%) outside normal range",
                        Impact = "May indicate data quality issues or valid edge cases that need review",
                        SampleRecordIds = outlierResult.OutlierSamples.Select(s => s.DocumentId).ToList(),
                        Metrics = new Dictionary<string, object>
                        {
                            ["OutlierCount"] = outlierResult.OutlierCount,
                            ["MinValue"] = outlierResult.MinValue,
                            ["MaxValue"] = outlierResult.MaxValue
                        },
                        Recommendations = new List<string> { outlierResult.RecommendedAction }
                    });
                }
            }

            foreach (var lengthResult in analysis.StringLengthAnalysis)
            {
                if (lengthResult.MaxLength > _options.MaxStringLengthForVarchar)
                {
                    analysis.AllIssues.Add(new DataQualityIssue
                    {
                        ContainerName = analysis.ContainerName,
                        FieldName = lengthResult.FieldName,
                        Severity = DataQualitySeverity.Info,
                        Category = "Length",
                        Title = $"Max string length {lengthResult.MaxLength} in {lengthResult.FieldName}",
                        Description = $"String lengths range from {lengthResult.MinLength} to {lengthResult.MaxLength} chars",
                        Impact = "Consider TEXT or NVARCHAR(MAX) type to accommodate maximum length",
                        SampleRecordIds = lengthResult.ExtremeValueSamples.Select(s => s.DocumentId).ToList(),
                        Metrics = new Dictionary<string, object>
                        {
                            ["MaxLength"] = lengthResult.MaxLength,
                            ["P95Length"] = lengthResult.P95Length,
                            ["P99Length"] = lengthResult.P99Length
                        },
                        Recommendations = new List<string> { lengthResult.RecommendedAction }
                    });
                }
            }

            foreach (var encodingIssue in analysis.EncodingIssues)
            {
                var severity = encodingIssue.IssueType == "ControlCharacters" 
                    ? DataQualitySeverity.Warning 
                    : DataQualitySeverity.Info;

                analysis.AllIssues.Add(new DataQualityIssue
                {
                    ContainerName = analysis.ContainerName,
                    FieldName = encodingIssue.FieldName,
                    Severity = severity,
                    Category = "Encoding",
                    Title = $"{encodingIssue.IssueType} in {encodingIssue.FieldName}",
                    Description = $"Found {encodingIssue.AffectedDocumentCount} documents ({encodingIssue.AffectedPercentage:F1}%) with {encodingIssue.IssueType}",
                    Impact = encodingIssue.IssueType == "ControlCharacters" 
                        ? "Control characters may cause display or parsing issues" 
                        : "May require specific collation or character set in SQL Server",
                    SampleRecordIds = encodingIssue.Samples.Select(s => s.DocumentId).ToList(),
                    Metrics = new Dictionary<string, object>
                    {
                        ["AffectedCount"] = encodingIssue.AffectedDocumentCount
                    },
                    Recommendations = new List<string> { encodingIssue.RecommendedAction }
                });
            }

            foreach (var dateResult in analysis.DateValidation)
            {
                var severity = dateResult.InvalidDateCount > 0 
                    ? DataQualitySeverity.Critical 
                    : DataQualitySeverity.Warning;

                analysis.AllIssues.Add(new DataQualityIssue
                {
                    ContainerName = analysis.ContainerName,
                    FieldName = dateResult.FieldName,
                    Severity = severity,
                    Category = "Date",
                    Title = $"Invalid dates in {dateResult.FieldName}",
                    Description = $"Found {dateResult.InvalidDateCount} invalid, {dateResult.FutureDateCount} future, and {dateResult.VeryOldDateCount} very old dates",
                    Impact = dateResult.InvalidDateCount > 0 
                        ? "Invalid dates will cause migration failures or data loss" 
                        : "Date ranges should be reviewed for business logic accuracy",
                    SampleRecordIds = dateResult.InvalidSamples.Select(s => s.DocumentId).ToList(),
                    Metrics = new Dictionary<string, object>
                    {
                        ["InvalidCount"] = dateResult.InvalidDateCount,
                        ["FutureCount"] = dateResult.FutureDateCount,
                        ["OldCount"] = dateResult.VeryOldDateCount
                    },
                    Recommendations = new List<string> { dateResult.RecommendedAction }
                });
            }
        }

        private void AggregateResults(DataQualityAnalysis analysis)
        {
            foreach (var container in analysis.ContainerAnalyses)
            {
                analysis.TotalFieldsAnalyzed += container.NullAnalysis.Count 
                    + container.TypeConsistency.Count 
                    + container.StringLengthAnalysis.Count;

                foreach (var issue in container.AllIssues)
                {
                    switch (issue.Severity)
                    {
                        case DataQualitySeverity.Critical:
                            analysis.CriticalIssuesCount++;
                            break;
                        case DataQualitySeverity.Warning:
                            analysis.WarningIssuesCount++;
                            break;
                        case DataQualitySeverity.Info:
                            analysis.InfoIssuesCount++;
                            break;
                    }
                }
            }
        }

        private void GenerateTopIssues(DataQualityAnalysis analysis)
        {
            analysis.TopIssues = analysis.ContainerAnalyses
                .SelectMany(c => c.AllIssues)
                .OrderByDescending(i => i.Severity)
                .ThenBy(i => i.Category)
                .Take(20)
                .ToList();
        }

        private void GenerateSummary(DataQualityAnalysis analysis)
        {
            var summary = analysis.Summary;
            var totalIssues = analysis.CriticalIssuesCount + analysis.WarningIssuesCount + analysis.InfoIssuesCount;
            var criticalWeight = analysis.CriticalIssuesCount * 10;
            var warningWeight = analysis.WarningIssuesCount * 3;
            var infoWeight = analysis.InfoIssuesCount * 1;
            
            var totalWeight = criticalWeight + warningWeight + infoWeight;
            var maxPossibleWeight = analysis.TotalFieldsAnalyzed * 10;
            
            summary.OverallQualityScore = maxPossibleWeight > 0 
                ? Math.Max(0, 100 - (totalWeight * 100.0 / maxPossibleWeight)) 
                : 100;

            summary.QualityRating = summary.OverallQualityScore >= 90 ? "Excellent" :
                                   summary.OverallQualityScore >= 75 ? "Good" :
                                   summary.OverallQualityScore >= 50 ? "Fair" : "Poor";

            summary.ReadyForMigration = analysis.CriticalIssuesCount == 0;

            summary.BlockingIssues = analysis.ContainerAnalyses
                .SelectMany(c => c.AllIssues)
                .Where(i => i.Severity == DataQualitySeverity.Critical)
                .Select(i => $"{i.ContainerName}.{i.FieldName}: {i.Title}")
                .ToList();

            summary.TopRecommendations = new List<string>();
            if (analysis.CriticalIssuesCount > 0)
            {
                summary.TopRecommendations.Add($"Address {analysis.CriticalIssuesCount} critical issues before migration");
            }
            if (analysis.WarningIssuesCount > 5)
            {
                summary.TopRecommendations.Add($"Review and resolve {analysis.WarningIssuesCount} warning issues");
            }

            summary.IssuesByCategory = analysis.ContainerAnalyses
                .SelectMany(c => c.AllIssues)
                .GroupBy(i => i.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            summary.IssuesBySeverity = new Dictionary<DataQualitySeverity, int>
            {
                [DataQualitySeverity.Critical] = analysis.CriticalIssuesCount,
                [DataQualitySeverity.Warning] = analysis.WarningIssuesCount,
                [DataQualitySeverity.Info] = analysis.InfoIssuesCount
            };

            summary.EstimatedCleanupHours = 
                analysis.CriticalIssuesCount * 2 +
                analysis.WarningIssuesCount * 1 +
                (analysis.InfoIssuesCount / 5);
        }

        // Helper methods
        private List<string> GetAllFieldPaths(List<JsonDocument> documents, ContainerAnalysis containerInfo)
        {
            var fieldPaths = new HashSet<string>();

            foreach (var schema in containerInfo.DetectedSchemas)
            {
                foreach (var field in schema.Fields.Keys)
                {
                    fieldPaths.Add(field);
                }
            }

            foreach (var doc in documents.Take(100))
            {
                ExtractFieldPaths(doc.RootElement, "", fieldPaths);
            }

            return fieldPaths.ToList();
        }

        private void ExtractFieldPaths(JsonElement element, string prefix, HashSet<string> paths)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var fieldPath = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    paths.Add(fieldPath);
                    
                    if (prefix.Split('.').Length < 3)
                    {
                        ExtractFieldPaths(property.Value, fieldPath, paths);
                    }
                }
            }
        }

        private object? GetFieldValue(JsonDocument doc, string fieldPath)
        {
            try
            {
                var parts = fieldPath.Split('.');
                JsonElement current = doc.RootElement;

                foreach (var part in parts)
                {
                    if (current.TryGetProperty(part, out var property))
                    {
                        current = property;
                    }
                    else
                    {
                        return null;
                    }
                }

                return current.ValueKind switch
                {
                    JsonValueKind.String => current.GetString(),
                    JsonValueKind.Number => current.TryGetInt64(out var i) ? i : current.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => current.ToString()
                };
            }
            catch
            {
                return null;
            }
        }

        private string GetDocumentId(JsonDocument doc)
        {
            try
            {
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString() ?? "unknown";
                }
            }
            catch { }
            return "unknown";
        }

        private bool IsNullValue(object? value)
        {
            return value == null || 
                   (value is string str && string.IsNullOrWhiteSpace(str)) ||
                   value.ToString() == "null";
        }

        private string GetFieldName(string fieldPath)
        {
            var parts = fieldPath.Split('.');
            return parts[parts.Length - 1];
        }

        private string GetNullRecommendation(double nullPercentage)
        {
            if (nullPercentage >= 0.15)
                return "Critical: Set default value or make field nullable in SQL schema";
            else if (nullPercentage >= 0.05)
                return "Warning: Review null handling strategy and consider default values";
            else
                return "Info: Low null percentage, can likely enforce NOT NULL constraint with cleanup";
        }

        private string GetDuplicateRecommendation(string fieldPath, double percentage)
        {
            if (fieldPath == "id")
                return "Critical: Remove duplicate IDs before migration - this will prevent primary key creation";
            else if (percentage > 1)
                return "Warning: Implement deduplication logic or add unique constraints carefully";
            else
                return "Info: Review duplicate records to determine if they are valid or need cleanup";
        }

        private string GetOutlierRecommendation(int outlierCount, int totalCount)
        {
            var percentage = (double)outlierCount / totalCount * 100;
            if (percentage > 5)
                return $"Review {outlierCount} outlier values - may indicate data quality issues";
            else
                return $"Monitor outlier values - likely valid edge cases";
        }

        private string GetDateValidationRecommendation(int invalid, int future, int old)
        {
            if (invalid > 0)
                return "Critical: Fix invalid date values before migration to prevent data loss";
            else if (future > 0 || old > 0)
                return "Warning: Review date ranges for business logic accuracy";
            else
                return "Info: All dates within expected ranges";
        }

        private Dictionary<string, object> GetDocumentSampleData(JsonDocument doc)
        {
            var data = new Dictionary<string, object>();
            try
            {
                foreach (var prop in doc.RootElement.EnumerateObject().Take(5))
                {
                    data[prop.Name] = prop.Value.ToString() ?? "";
                }
            }
            catch { }
            return data;
        }

        private string GetTypeName(object value)
        {
            return value switch
            {
                string => "string",
                int or long => "integer",
                double or float or decimal => "number",
                bool => "boolean",
                DateTime or DateTimeOffset => "datetime",
                _ => value.GetType().Name
            };
        }

        private bool IsNumericType(object value)
        {
            return value is int || value is long || value is double || 
                   value is float || value is decimal;
        }

        private string MapToSqlType(string cosmosType)
        {
            return cosmosType switch
            {
                "string" => "NVARCHAR(MAX)",
                "integer" => "BIGINT",
                "number" => "FLOAT",
                "boolean" => "BIT",
                "datetime" => "DATETIME2",
                _ => "NVARCHAR(MAX)"
            };
        }

        private EncodingSample CreateEncodingSample(JsonDocument doc, string value, string description)
        {
            var charCodes = string.Join(" ", value.Take(20).Select(c => $"U+{(int)c:X4}"));
            return new EncodingSample
            {
                DocumentId = GetDocumentId(doc),
                ProblematicValue = value.Length > 50 ? value.Substring(0, 50) + "..." : value,
                CharacterCodes = charCodes,
                IssueDescription = description
            };
        }

        private bool ContainsEmoji(string text)
        {
            return text.Any(c => c >= 0x1F600 && c <= 0x1F64F) ||
                   text.Any(c => c >= 0x1F300 && c <= 0x1F5FF) ||
                   text.Any(c => c >= 0x1F680 && c <= 0x1F6FF) ||
                   text.Any(c => c >= 0x2600 && c <= 0x26FF);
        }

        private bool LooksLikeDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            
            return Regex.IsMatch(value, @"\d{4}[-/]\d{1,2}[-/]\d{1,2}") ||
                   Regex.IsMatch(value, @"\d{1,2}[-/]\d{1,2}[-/]\d{4}") ||
                   (value.Contains("T") && value.Contains("Z"));
        }
    }
}
