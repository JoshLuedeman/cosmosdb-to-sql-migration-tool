using CosmosToSqlAssessment.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CosmosToSqlAssessment.Reporting
{
    /// <summary>
    /// Service responsible for generating comprehensive Excel and Word reports
    /// Implements Azure best practices for reporting and documentation
    /// </summary>
    public class ReportGenerationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ReportGenerationService> _logger;

        public ReportGenerationService(IConfiguration configuration, ILogger<ReportGenerationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Generates a comprehensive assessment report with separate Excel files per database and one Word summary
        /// Follows Azure documentation standards and best practices
        /// </summary>
        public async Task<(List<string> ExcelPaths, string WordPath, string AnalysisFolderPath)> GenerateAssessmentReportAsync(
            AssessmentResult assessmentResult,
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            var analysisFolder = string.Empty;
            var excelPaths = new List<string>();
            var wordPath = string.Empty;
            
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var now = DateTime.Now;
                var timestamp = now.ToString("yyyy-MM-dd__HH-mm-ss");
                
                // Create main analysis folder
                var baseOutputDirectory = outputDirectory ?? _configuration.GetValue<string>("Reporting:OutputDirectory") ?? "Reports";
                analysisFolder = Path.Combine(baseOutputDirectory, $"CosmosDB-Analysis_{timestamp}");
                Directory.CreateDirectory(analysisFolder);

                // Create Reports subfolder for documentation
                var reportsFolder = Path.Combine(analysisFolder, "Reports");
                Directory.CreateDirectory(reportsFolder);

                wordPath = Path.Combine(reportsFolder, "Migration-Assessment.docx");

                // Check if this is a multi-database assessment
                if (assessmentResult.IndividualDatabaseResults?.Any() == true)
                {
                    // Generate separate Excel report for each individual database
                    foreach (var individualResult in assessmentResult.IndividualDatabaseResults)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        var sanitizedDbName = SanitizeFileName(individualResult.DatabaseName);
                        var excelPath = Path.Combine(reportsFolder, $"{sanitizedDbName}-Analysis.xlsx");
                        
                        await GenerateExcelReportAsync(individualResult, excelPath, cancellationToken);
                        excelPaths.Add(excelPath);
                    }
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Generate Word report for the combined assessment
                    await GenerateWordReportAsync(assessmentResult, wordPath, cancellationToken);
                }
                else
                {
                    // Single database - generate one Excel and one Word report
                    var databaseName = !string.IsNullOrEmpty(assessmentResult.DatabaseName) ? assessmentResult.DatabaseName : "Database";
                    var sanitizedDbName = SanitizeFileName(databaseName);
                    var excelPath = Path.Combine(reportsFolder, $"{sanitizedDbName}-Analysis.xlsx");
                    
                    await GenerateExcelReportAsync(assessmentResult, excelPath, cancellationToken);
                    excelPaths.Add(excelPath);
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Generate Word report (summary across all databases)
                    await GenerateWordReportAsync(assessmentResult, wordPath, cancellationToken);
                }

                _logger.LogInformation("Assessment reports generated successfully in folder: {AnalysisFolder}", analysisFolder);
                _logger.LogInformation("Reports generated:");
                foreach (var excelFile in excelPaths)
                {
                    _logger.LogInformation("  Excel Report: {ExcelFileName}", Path.GetFileName(excelFile));
                }
                _logger.LogInformation("  Word Report: {WordFileName}", Path.GetFileName(wordPath));

                return (excelPaths, wordPath, analysisFolder);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Report generation cancelled by user");
                
                // Clean up partial files
                CleanupPartialFiles(excelPaths, wordPath, analysisFolder);
                
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating assessment reports");
                throw;
            }
        }

        /// <summary>
        /// Sanitizes a file name by removing invalid characters
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            return sanitized.Trim();
        }

        /// <summary>
        /// Cleans up partial files created during report generation if cancelled
        /// </summary>
        private void CleanupPartialFiles(List<string> excelPaths, string wordPath, string analysisFolderPath)
        {
            try
            {
                // Delete partial Excel files
                foreach (var excelPath in excelPaths)
                {
                    if (File.Exists(excelPath))
                    {
                        try
                        {
                            File.Delete(excelPath);
                            _logger.LogInformation("Deleted partial Excel file: {FilePath}", excelPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete partial Excel file: {FilePath}", excelPath);
                        }
                    }
                }

                // Delete partial Word file
                if (!string.IsNullOrEmpty(wordPath) && File.Exists(wordPath))
                {
                    try
                    {
                        File.Delete(wordPath);
                        _logger.LogInformation("Deleted partial Word file: {FilePath}", wordPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete partial Word file: {FilePath}", wordPath);
                    }
                }

                // Delete analysis folder if it's empty or only contains the Reports subfolder
                if (!string.IsNullOrEmpty(analysisFolderPath) && Directory.Exists(analysisFolderPath))
                {
                    try
                    {
                        var reportsFolder = Path.Combine(analysisFolderPath, "Reports");
                        
                        // Try to delete Reports subfolder if empty
                        if (Directory.Exists(reportsFolder) && !Directory.EnumerateFileSystemEntries(reportsFolder).Any())
                        {
                            Directory.Delete(reportsFolder);
                        }
                        
                        // Try to delete analysis folder if empty
                        if (!Directory.EnumerateFileSystemEntries(analysisFolderPath).Any())
                        {
                            Directory.Delete(analysisFolderPath);
                            _logger.LogInformation("Deleted empty analysis folder: {FolderPath}", analysisFolderPath);
                        }
                        else
                        {
                            _logger.LogInformation("Analysis folder not empty, keeping: {FolderPath}", analysisFolderPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete analysis folder: {FolderPath}", analysisFolderPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cleanup of partial files");
            }
        }

        /// <summary>
        /// Generates detailed Excel report for a single database with multiple worksheets
        /// Implements Azure best practices for data presentation
        /// </summary>
        private async Task GenerateExcelReportAsync(AssessmentResult assessmentResult, string filePath, CancellationToken cancellationToken)
        {
            using var workbook = new XLWorkbook();

            var databaseName = !string.IsNullOrEmpty(assessmentResult.DatabaseName) ? assessmentResult.DatabaseName : "Database";

            cancellationToken.ThrowIfCancellationRequested();
            
            // Create database summary worksheet (overview of all containers)
            CreateDatabaseSummaryWorksheet(workbook, assessmentResult, databaseName);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            // Create individual container detail worksheets (one per container)
            CreateContainerDetailWorksheets(workbook, assessmentResult, databaseName);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            // Create other comprehensive worksheets for detailed analysis
            CreateExecutiveSummaryWorksheet(workbook, assessmentResult);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            CreateSqlMappingWorksheet(workbook, assessmentResult);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            CreateIndexRecommendationsWorksheet(workbook, assessmentResult);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            CreateConstraintsWorksheet(workbook, assessmentResult);
            
            cancellationToken.ThrowIfCancellationRequested();
            
            CreateMigrationEstimatesWorksheet(workbook, assessmentResult);

            cancellationToken.ThrowIfCancellationRequested();
            
            workbook.SaveAs(filePath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates executive summary worksheet with key findings and recommendations
        /// </summary>
        private void CreateExecutiveSummaryWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("Executive Summary");
            
            // Header
            ws.Cell("A1").Value = "Cosmos DB to SQL Migration Assessment";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.LightBlue;

            // Assessment Overview
            var row = 3;
            ws.Cell(row, 1).Value = "Assessment Overview";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            row += 2;

            ws.Cell(row, 1).Value = "Assessment Date:";
            ws.Cell(row, 2).Value = assessmentResult.AssessmentDate.ToString("yyyy-MM-dd HH:mm:ss");
            row++;

            ws.Cell(row, 1).Value = "Cosmos DB Account:";
            ws.Cell(row, 2).Value = assessmentResult.CosmosAccountName;
            row++;

            ws.Cell(row, 1).Value = "Database Name:";
            ws.Cell(row, 2).Value = assessmentResult.DatabaseName;
            row++;

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Creates detailed container analysis worksheet with schemas and document samples
        /// </summary>
        private void CreateContainerAnalysisWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("Container Analysis");
            
            // Header
            ws.Cell("A1").Value = "Container Analysis Details";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.LightGreen;

            if (assessmentResult.CosmosAnalysis?.Containers == null || !assessmentResult.CosmosAnalysis.Containers.Any())
            {
                ws.Cell("A3").Value = "No container data available.";
                return;
            }

            var row = 3;
            foreach (var container in assessmentResult.CosmosAnalysis.Containers)
            {
                // Container name header
                ws.Cell(row, 1).Value = $"Container: {container.ContainerName}";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 14;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
                row += 2;

                // Container details
                ws.Cell(row, 1).Value = "Partition Key:";
                ws.Cell(row, 2).Value = container.PartitionKey ?? "Not specified";
                row++;

                ws.Cell(row, 1).Value = "Provisioned RUs:";
                ws.Cell(row, 2).Value = container.ProvisionedRUs;
                row++;

                ws.Cell(row, 1).Value = "Document Count:";
                ws.Cell(row, 2).Value = container.DocumentCount;
                row++;

                ws.Cell(row, 1).Value = "Storage Size (GB):";
                ws.Cell(row, 2).Value = (container.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2");
                row += 2;

                // Schema information
                if (container.DetectedSchemas?.Any() == true)
                {
                    ws.Cell(row, 1).Value = "Detected Document Schemas:";
                    ws.Cell(row, 1).Style.Font.Bold = true;
                    row++;

                    foreach (var schema in container.DetectedSchemas)
                    {
                        ws.Cell(row, 2).Value = $"Schema {schema.SchemaName}:";
                        ws.Cell(row, 2).Style.Font.Bold = true;
                        row++;

                        ws.Cell(row, 3).Value = "Field Name";
                        ws.Cell(row, 4).Value = "SQL Type";
                        ws.Cell(row, 5).Value = "Detected Types";
                        ws.Cell(row, 6).Value = "Nullable";
                        
                        // Header styling for field headers
                        for (int col = 3; col <= 6; col++)
                        {
                            ws.Cell(row, col).Style.Font.Bold = true;
                            ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                        }
                        row++;

                        foreach (var field in schema.Fields)
                        {
                            var fieldInfo = field.Value;
                            ws.Cell(row, 3).Value = field.Key; // Field name only
                            ws.Cell(row, 4).Value = fieldInfo.RecommendedSqlType; // SQL type
                            ws.Cell(row, 5).Value = string.Join(", ", fieldInfo.DetectedTypes); // All detected types
                            ws.Cell(row, 6).Value = fieldInfo.IsRequired ? "No" : "Yes"; // Nullable status
                            row++;
                        }
                        row++;
                    }
                }
                row += 2;
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Creates database summary worksheet with container-level information
        /// </summary>
        private void CreateDatabaseSummaryWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult, string databaseName)
        {
            // Create a shorter worksheet name to stay within Excel's 31-character limit
            var worksheetName = CreateValidWorksheetName(databaseName, "Summary");
            var ws = workbook.Worksheets.Add(worksheetName);
            
            // Header
            ws.Cell("A1").Value = $"Database: {databaseName} - Container Summary";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.LightBlue;

            if (assessmentResult.CosmosAnalysis?.Containers == null || !assessmentResult.CosmosAnalysis.Containers.Any())
            {
                ws.Cell("A3").Value = "No container data available.";
                return;
            }

            // Table headers
            var row = 3;
            ws.Cell(row, 1).Value = "Container";
            ws.Cell(row, 2).Value = "Partition Key";
            ws.Cell(row, 3).Value = "Provisioned RUs";
            ws.Cell(row, 4).Value = "Document Count";
            ws.Cell(row, 5).Value = "Storage Size (GB)";
            
            // Header styling
            for (int col = 1; col <= 5; col++)
            {
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            row++;

            // Container data
            foreach (var container in assessmentResult.CosmosAnalysis.Containers)
            {
                ws.Cell(row, 1).Value = container.ContainerName;
                ws.Cell(row, 2).Value = container.PartitionKey ?? "Not specified";
                ws.Cell(row, 3).Value = container.ProvisionedRUs;
                ws.Cell(row, 4).Value = container.DocumentCount;
                ws.Cell(row, 5).Value = (container.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2");
                
                // Add borders
                for (int col = 1; col <= 5; col++)
                {
                    ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                row++;
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Creates individual worksheets for each container with detailed field information
        /// </summary>
        private void CreateContainerDetailWorksheets(XLWorkbook workbook, AssessmentResult assessmentResult, string databaseName)
        {
            if (assessmentResult.CosmosAnalysis?.Containers == null || !assessmentResult.CosmosAnalysis.Containers.Any())
                return;

            foreach (var container in assessmentResult.CosmosAnalysis.Containers)
            {
                // Use simple container name as worksheet name since each Excel file is for a single database
                var worksheetName = CreateValidWorksheetName(container.ContainerName);

                var ws = workbook.Worksheets.Add(worksheetName);
                
                // Header
                ws.Cell("A1").Value = $"Container: {container.ContainerName} - Field Analysis";
                ws.Cell("A1").Style.Font.Bold = true;
                ws.Cell("A1").Style.Font.FontSize = 14;
                ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.LightGreen;

                // Merge all schemas into a distinct field list
                var allFields = new Dictionary<string, FieldInfo>();
                
                if (container.DetectedSchemas?.Any() == true)
                {
                    foreach (var schema in container.DetectedSchemas)
                    {
                        foreach (var field in schema.Fields)
                        {
                            if (!allFields.ContainsKey(field.Key))
                            {
                                allFields[field.Key] = new FieldInfo
                                {
                                    FieldName = field.Value.FieldName,
                                    DetectedTypes = new List<string>(field.Value.DetectedTypes),
                                    RecommendedSqlType = field.Value.RecommendedSqlType,
                                    IsRequired = field.Value.IsRequired,
                                    IsNested = field.Value.IsNested
                                };
                            }
                            else
                            {
                                // Merge detected types from multiple schemas
                                foreach (var type in field.Value.DetectedTypes)
                                {
                                    if (!allFields[field.Key].DetectedTypes.Contains(type))
                                    {
                                        allFields[field.Key].DetectedTypes.Add(type);
                                    }
                                }
                                
                                // Update the recommended SQL type based on all detected types
                                allFields[field.Key].RecommendedSqlType = GetBestSqlType(allFields[field.Key].DetectedTypes);
                                
                                // If any schema shows it as not required, mark it as not required
                                if (!field.Value.IsRequired)
                                {
                                    allFields[field.Key].IsRequired = false;
                                }
                            }
                        }
                    }
                }

                // Table headers
                var row = 3;
                ws.Cell(row, 1).Value = "Container";
                ws.Cell(row, 2).Value = "Field Name";
                ws.Cell(row, 3).Value = "Detected Types";
                ws.Cell(row, 4).Value = "SQL Type";
                ws.Cell(row, 5).Value = "Nullable";
                
                // Header styling
                for (int col = 1; col <= 5; col++)
                {
                    ws.Cell(row, col).Style.Font.Bold = true;
                    ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                    ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                row++;

                // Field data
                foreach (var field in allFields.OrderBy(f => f.Key))
                {
                    ws.Cell(row, 1).Value = container.ContainerName;
                    ws.Cell(row, 2).Value = field.Key;
                    ws.Cell(row, 3).Value = string.Join(", ", field.Value.DetectedTypes);
                    ws.Cell(row, 4).Value = field.Value.RecommendedSqlType;
                    ws.Cell(row, 5).Value = field.Value.IsRequired ? "No" : "Yes";
                    
                    // Add borders
                    for (int col = 1; col <= 5; col++)
                    {
                        ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    }
                    row++;
                }

                if (!allFields.Any())
                {
                    ws.Cell(row, 2).Value = "No fields detected in this container";
                    ws.Cell(row, 2).Style.Font.Italic = true;
                }

                // Auto-fit columns
                ws.Columns().AdjustToContents();
            }
        }

        /// <summary>
        /// Helper method to determine the best SQL type from multiple detected types
        /// </summary>
        private string GetBestSqlType(List<string> detectedTypes)
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
            if (detectedTypes.Any(t => t.StartsWith("DECIMAL(")))
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
            if (detectedTypes.Any(t => t.StartsWith("NVARCHAR(")))
                return detectedTypes.Where(t => t.StartsWith("NVARCHAR(")).OrderByDescending(t => t).FirstOrDefault() ?? "NVARCHAR(255)";
            
            return "NVARCHAR(MAX)";
        }

        /// <summary>
        /// Creates SQL mapping worksheet with recommended table structures
        /// </summary>
        private void CreateSqlMappingWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("SQL Mapping");
            
            // Header
            ws.Cell("A1").Value = "SQL Migration Mapping";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.Orange;

            if (assessmentResult.SqlAssessment?.DatabaseMappings == null || !assessmentResult.SqlAssessment.DatabaseMappings.Any())
            {
                ws.Cell("A3").Value = "No SQL mapping data available.";
                return;
            }

            var row = 3;
            ws.Cell(row, 1).Value = "Recommended Platform:";
            ws.Cell(row, 2).Value = assessmentResult.SqlAssessment.RecommendedPlatform;
            ws.Cell(row, 2).Style.Font.Bold = true;
            row += 2;

            // Table mappings
            ws.Cell(row, 1).Value = "Table Type";
            ws.Cell(row, 2).Value = "Source Container/Field";
            ws.Cell(row, 3).Value = "Recommended SQL Table";
            ws.Cell(row, 4).Value = "Target Schema";
            ws.Cell(row, 5).Value = "Estimated Row Count";
            ws.Cell(row, 6).Value = "Transformation Required";
            ws.Cell(row, 7).Value = "Relationship";
            
            // Header styling
            for (int col = 1; col <= 7; col++)
            {
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
            }
            row++;

            foreach (var dbMapping in assessmentResult.SqlAssessment.DatabaseMappings)
            {
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    // Main table
                    ws.Cell(row, 1).Value = "Main Table";
                    ws.Cell(row, 2).Value = containerMapping.SourceContainer;
                    ws.Cell(row, 3).Value = containerMapping.TargetTable;
                    ws.Cell(row, 4).Value = containerMapping.TargetSchema;
                    ws.Cell(row, 5).Value = containerMapping.EstimatedRowCount;
                    ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
                    
                    // Add color coding based on row count thresholds
                    if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Critical)
                    {
                        ws.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.Red;
                        ws.Cell(row, 5).Style.Font.FontColor = XLColor.White;
                    }
                    else if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.HighPriority)
                    {
                        ws.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.Orange;
                    }
                    else if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Warning)
                    {
                        ws.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.Yellow;
                    }
                    
                    ws.Cell(row, 6).Value = containerMapping.RequiredTransformations.Any() ? "Yes" : "No";
                    ws.Cell(row, 7).Value = "Primary Entity";
                    ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
                    row++;

                    // Child tables (normalized from arrays and nested objects)
                    foreach (var childMapping in containerMapping.ChildTableMappings)
                    {
                        ws.Cell(row, 1).Value = "Child Table";
                        ws.Cell(row, 2).Value = $"{containerMapping.SourceContainer}.{childMapping.SourceFieldPath}";
                        ws.Cell(row, 3).Value = childMapping.TargetTable;
                        ws.Cell(row, 4).Value = childMapping.TargetSchema;
                        ws.Cell(row, 5).Value = "See parent";
                        ws.Cell(row, 6).Value = childMapping.RequiredTransformations.Any() ? "Yes" : "No";
                        ws.Cell(row, 7).Value = $"Related to {containerMapping.TargetTable} via {childMapping.ParentKeyColumn}";
                        ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                        row++;
                    }

                    // Add a blank row between containers for readability
                    if (containerMapping != dbMapping.ContainerMappings.Last())
                    {
                        row++;
                    }
                }
            }

            // Add detailed field mappings section
            row += 3;
            ws.Cell(row, 1).Value = "Detailed Field Mappings";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.Orange;
            row += 2;

            foreach (var dbMapping in assessmentResult.SqlAssessment.DatabaseMappings)
            {
                foreach (var containerMapping in dbMapping.ContainerMappings)
                {
                    // Main table field mappings
                    ws.Cell(row, 1).Value = $"Main Table: {containerMapping.TargetTable}";
                    ws.Cell(row, 1).Style.Font.Bold = true;
                    ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
                    row++;

                    // Field mapping headers
                    ws.Cell(row, 1).Value = "Source Field";
                    ws.Cell(row, 2).Value = "Target Column";
                    ws.Cell(row, 3).Value = "Source Type";
                    ws.Cell(row, 4).Value = "Target SQL Type";
                    ws.Cell(row, 5).Value = "Nullable";
                    ws.Cell(row, 6).Value = "Transformation Logic";

                    for (int col = 1; col <= 6; col++)
                    {
                        ws.Cell(row, col).Style.Font.Bold = true;
                        ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                    }
                    row++;

                    // Main table fields
                    foreach (var fieldMapping in containerMapping.FieldMappings)
                    {
                        ws.Cell(row, 1).Value = fieldMapping.SourceField;
                        ws.Cell(row, 2).Value = fieldMapping.TargetColumn;
                        ws.Cell(row, 3).Value = fieldMapping.SourceType;
                        ws.Cell(row, 4).Value = fieldMapping.TargetType;
                        ws.Cell(row, 5).Value = fieldMapping.IsNullable ? "Yes" : "No";
                        ws.Cell(row, 6).Value = string.IsNullOrEmpty(fieldMapping.TransformationLogic) ? "None" : fieldMapping.TransformationLogic;
                        row++;
                    }

                    row++;

                    // Child table field mappings
                    foreach (var childMapping in containerMapping.ChildTableMappings)
                    {
                        ws.Cell(row, 1).Value = $"Child Table: {childMapping.TargetTable}";
                        ws.Cell(row, 1).Style.Font.Bold = true;
                        ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                        row++;

                        // Field mapping headers for child table
                        ws.Cell(row, 1).Value = "Source Field";
                        ws.Cell(row, 2).Value = "Target Column";
                        ws.Cell(row, 3).Value = "Source Type";
                        ws.Cell(row, 4).Value = "Target SQL Type";
                        ws.Cell(row, 5).Value = "Nullable";
                        ws.Cell(row, 6).Value = "Transformation Logic";

                        for (int col = 1; col <= 6; col++)
                        {
                            ws.Cell(row, col).Style.Font.Bold = true;
                            ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                        }
                        row++;

                        // Child table fields
                        foreach (var fieldMapping in childMapping.FieldMappings)
                        {
                            ws.Cell(row, 1).Value = fieldMapping.SourceField;
                            ws.Cell(row, 2).Value = fieldMapping.TargetColumn;
                            ws.Cell(row, 3).Value = fieldMapping.SourceType;
                            ws.Cell(row, 4).Value = fieldMapping.TargetType;
                            ws.Cell(row, 5).Value = fieldMapping.IsNullable ? "Yes" : "No";
                            ws.Cell(row, 6).Value = string.IsNullOrEmpty(fieldMapping.TransformationLogic) ? "None" : fieldMapping.TransformationLogic;
                            row++;
                        }

                        row++;
                    }

                    row++;
                }
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Creates index recommendations worksheet
        /// </summary>
        private void CreateIndexRecommendationsWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("Index Recommendations");
            
            // Header
            ws.Cell("A1").Value = "SQL Index Recommendations";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.Yellow;

            if (assessmentResult.SqlAssessment?.IndexRecommendations == null || !assessmentResult.SqlAssessment.IndexRecommendations.Any())
            {
                ws.Cell("A3").Value = "No index recommendations available.";
                return;
            }

            var row = 3;
            ws.Cell(row, 1).Value = "Table Name";
            ws.Cell(row, 2).Value = "Index Type";
            ws.Cell(row, 3).Value = "Columns";
            ws.Cell(row, 4).Value = "Rationale";
            
            // Header styling
            for (int col = 1; col <= 4; col++)
            {
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
            }
            row++;

            foreach (var recommendation in assessmentResult.SqlAssessment.IndexRecommendations)
            {
                ws.Cell(row, 1).Value = recommendation.TableName;
                ws.Cell(row, 2).Value = recommendation.IndexType;
                ws.Cell(row, 3).Value = string.Join(", ", recommendation.Columns);
                ws.Cell(row, 4).Value = recommendation.Justification;
                row++;
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Creates migration estimates worksheet with costs and timeline
        /// </summary>
        private void CreateMigrationEstimatesWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("Migration Estimates");
            
            // Header
            ws.Cell("A1").Value = "Azure Data Factory Migration Estimates";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.LightCyan;

            if (assessmentResult.DataFactoryEstimate == null)
            {
                ws.Cell("A3").Value = "No migration estimates available.";
                return;
            }

            var row = 3;
            var estimate = assessmentResult.DataFactoryEstimate;

            ws.Cell(row, 1).Value = "Estimated Duration:";
            ws.Cell(row, 2).Value = estimate.EstimatedDuration.ToString(@"hh\:mm\:ss");
            row++;

            ws.Cell(row, 1).Value = "Estimated Cost (USD):";
            ws.Cell(row, 2).Value = $"${estimate.EstimatedCostUSD:F2}";
            row++;

            ws.Cell(row, 1).Value = "Recommended DIUs:";
            ws.Cell(row, 2).Value = estimate.RecommendedDIUs;
            row++;

            ws.Cell(row, 1).Value = "Total Data Size (GB):";
            ws.Cell(row, 2).Value = estimate.TotalDataSizeGB;
            row += 2;

            // Prerequisites
            if (estimate.Prerequisites?.Any() == true)
            {
                ws.Cell(row, 1).Value = "Prerequisites:";
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;

                foreach (var prerequisite in estimate.Prerequisites)
                {
                    ws.Cell(row, 2).Value = $"• {prerequisite}";
                    row++;
                }
            }

            // Recommendations
            if (estimate.Recommendations?.Any() == true)
            {
                row++;
                ws.Cell(row, 1).Value = "Recommendations:";
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;

                foreach (var recommendation in estimate.Recommendations)
                {
                    ws.Cell(row, 2).Value = $"• {recommendation}";
                    row++;
                }
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Generates comprehensive Word report with the new structure
        /// Follows Azure documentation standards
        /// </summary>
        private async Task GenerateWordReportAsync(AssessmentResult assessmentResult, string filePath, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                using var document = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
                
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                // Add style definitions to the document
                AddStyleDefinitions(document);

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Document Title: Database Migration Project for <Instance Name>
                    var instanceName = !string.IsNullOrEmpty(assessmentResult.CosmosAccountName) 
                        ? assessmentResult.CosmosAccountName 
                        : "Cosmos DB Instance";
                    AddWordTitle(body, $"Database Migration Project for {instanceName}");
                    AddEmptyLine(body);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Executive Overview
                    AddWordHeading(body, "Executive Overview", 1);
                    AddExecutiveOverviewTable(body, assessmentResult);
                    AddExecutiveOverviewExplanation(body);
                    AddEmptyLine(body);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Databases
                    AddWordHeading(body, "Databases", 1);
                    
                    // Check if this is a multi-database assessment
                    if (assessmentResult.IndividualDatabaseResults?.Any() == true)
            {
                // Multi-database structure: each database as Level 2
                foreach (var dbResult in assessmentResult.IndividualDatabaseResults)
                {
                    // Level 2 Header: Individual Database Name
                    AddWordHeading(body, dbResult.DatabaseName, 2);
                    AddDatabaseInformationTable(body, dbResult);
                    AddEmptyLine(body);
                    
                    // Level 3 Headers: Individual containers under this database
                    if (dbResult.CosmosAnalysis?.Containers?.Any() == true)
                    {
                        foreach (var container in dbResult.CosmosAnalysis.Containers)
                        {
                            try
                            {
                                AddWordHeading(body, container.ContainerName, 3);
                                AddContainerConfiguration(body, container);
                                AddContainerFieldsTable(body, container);
                                AddEmptyLine(body);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to process container {ContainerName} for Word report", container.ContainerName);
                                AddWordHeading(body, $"{container.ContainerName} [Processing Error]", 3);
                                AddWordParagraph(body, "Unable to process container details due to data corruption or unexpected format.");
                                AddEmptyLine(body);
                            }
                        }

                        // Level 3 Header: Assessment Summary for this database
                        AddWordHeading(body, "Assessment Summary", 3);
                        AddAssessmentSummaryBullets(body, dbResult);
                        AddEmptyLine(body);
                    }
                }
            }
            else
            {
                // Single database structure: database as Level 2
                var databaseName = !string.IsNullOrEmpty(assessmentResult.DatabaseName) 
                    ? assessmentResult.DatabaseName 
                    : "Database";
                
                // Level 2 Header: Database Name
                AddWordHeading(body, databaseName, 2);
                AddDatabaseInformationTable(body, assessmentResult);
                AddEmptyLine(body);
                
                // Level 3 Headers: Individual containers
                if (assessmentResult.CosmosAnalysis?.Containers?.Any() == true)
                {
                    foreach (var container in assessmentResult.CosmosAnalysis.Containers)
                    {
                        try
                        {
                            AddWordHeading(body, container.ContainerName, 3);
                            AddContainerConfiguration(body, container);
                            AddContainerFieldsTable(body, container);
                            AddEmptyLine(body);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to process container {ContainerName} for Word report", container.ContainerName);
                            AddWordHeading(body, $"{container.ContainerName} [Processing Error]", 3);
                            AddWordParagraph(body, "Unable to process container details due to data corruption or unexpected format.");
                            AddEmptyLine(body);
                        }
                    }

                    // Level 3 Header: Assessment Summary
                    AddWordHeading(body, "Assessment Summary", 3);
                    AddAssessmentSummaryBullets(body, assessmentResult);
                    AddEmptyLine(body);
                }
            }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Migration Recommendations
                    AddWordHeading(body, "Migration Recommendations", 1);
                    AddMigrationRecommendationsBullets(body, assessmentResult);
                    AddMigrationRecommendationsExplanation(body, assessmentResult);
                    AddEmptyLine(body);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Data Factory Estimates
                    AddWordHeading(body, "Data Factory Estimates", 1);
                    AddDataFactoryEstimatesBullets(body, assessmentResult);
                    AddEmptyLine(body);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Proposed SQL Schema Layout
                    AddWordHeading(body, "Proposed SQL Schema Layout", 1);
                    AddSqlSchemaLayout(body, assessmentResult);
                    AddEmptyLine(body);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Level 1 Header: Next Steps
                    AddWordHeading(body, "Next Steps", 1);
                    AddNextStepsNumberedList(body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add some content to Word report, adding error message");
                    // Add minimal content to prevent completely empty document
                    try
                    {
                        body.RemoveAllChildren();
                        AddWordTitle(body, "Migration Assessment Report [Partial Content]");
                        AddWordParagraph(body, "Some content could not be generated due to data formatting issues.");
                        AddWordParagraph(body, $"Assessment Date: {DateTime.Now:yyyy-MM-dd}");
                        AddWordParagraph(body, $"Database: {assessmentResult.DatabaseName ?? "Unknown"}");
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "Failed to create even fallback Word content");
                        throw; // This will be caught by outer try-catch
                    }
                }

                mainPart.Document.Save();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate Word report entirely");
                // Delete corrupted file if it exists
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Failed to delete corrupted Word file: {FilePath}", filePath);
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Adds style definitions to the Word document to ensure proper heading styles
        /// </summary>
        private void AddStyleDefinitions(WordprocessingDocument document)
        {
            var styleDefinitionsPart = document.MainDocumentPart!.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles();

            // Title style
            var titleStyle = new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = "Title"
            };
            titleStyle.AppendChild(new Name() { Val = "Title" });
            titleStyle.AppendChild(new BasedOn() { Val = "Normal" });
            titleStyle.AppendChild(new PrimaryStyle());
            titleStyle.AppendChild(new StyleParagraphProperties(
                new SpacingBetweenLines() { After = "240" },
                new Justification() { Val = JustificationValues.Center }
            ));
            titleStyle.AppendChild(new StyleRunProperties(
                new Bold(),
                new FontSize() { Val = "32" },
                new FontSizeComplexScript() { Val = "32" }
            ));

            // Heading 1 style
            var heading1Style = new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading1"
            };
            heading1Style.AppendChild(new Name() { Val = "heading 1" });
            heading1Style.AppendChild(new BasedOn() { Val = "Normal" });
            heading1Style.AppendChild(new NextParagraphStyle() { Val = "Normal" });
            heading1Style.AppendChild(new PrimaryStyle());
            heading1Style.AppendChild(new StyleParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines() { Before = "240", After = "60" },
                new OutlineLevel() { Val = 0 }
            ));
            heading1Style.AppendChild(new StyleRunProperties(
                new Bold(),
                new Color() { Val = "2F5496" },
                new FontSize() { Val = "32" },
                new FontSizeComplexScript() { Val = "32" }
            ));

            // Heading 2 style
            var heading2Style = new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading2"
            };
            heading2Style.AppendChild(new Name() { Val = "heading 2" });
            heading2Style.AppendChild(new BasedOn() { Val = "Normal" });
            heading2Style.AppendChild(new NextParagraphStyle() { Val = "Normal" });
            heading2Style.AppendChild(new PrimaryStyle());
            heading2Style.AppendChild(new StyleParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines() { Before = "40", After = "60" },
                new OutlineLevel() { Val = 1 }
            ));
            heading2Style.AppendChild(new StyleRunProperties(
                new Bold(),
                new Color() { Val = "2F5496" },
                new FontSize() { Val = "26" },
                new FontSizeComplexScript() { Val = "26" }
            ));

            // Heading 3 style
            var heading3Style = new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading3"
            };
            heading3Style.AppendChild(new Name() { Val = "heading 3" });
            heading3Style.AppendChild(new BasedOn() { Val = "Normal" });
            heading3Style.AppendChild(new NextParagraphStyle() { Val = "Normal" });
            heading3Style.AppendChild(new PrimaryStyle());
            heading3Style.AppendChild(new StyleParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines() { Before = "40", After = "60" },
                new OutlineLevel() { Val = 2 }
            ));
            heading3Style.AppendChild(new StyleRunProperties(
                new Bold(),
                new Color() { Val = "1F3763" },
                new FontSize() { Val = "24" },
                new FontSizeComplexScript() { Val = "24" }
            ));

            // Normal style (required as base)
            var normalStyle = new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            };
            normalStyle.AppendChild(new Name() { Val = "Normal" });
            normalStyle.AppendChild(new PrimaryStyle());
            normalStyle.AppendChild(new StyleRunProperties(
                new FontSize() { Val = "22" },
                new FontSizeComplexScript() { Val = "22" }
            ));

            styles.AppendChild(normalStyle);
            styles.AppendChild(titleStyle);
            styles.AppendChild(heading1Style);
            styles.AppendChild(heading2Style);
            styles.AppendChild(heading3Style);

            styleDefinitionsPart.Styles = styles;
        }

        /// <summary>
        /// Adds a document title using Word's Title style
        /// </summary>
        private void AddWordTitle(Body body, string text)
        {
            try
            {
                var safeText = SanitizeTextForWord(text);
                var paragraph = new Paragraph();
                var run = new Run(new Text(safeText) { Space = SpaceProcessingModeValues.Preserve });
                
                // Use Word's built-in Title style
                var paragraphProperties = new ParagraphProperties();
                var paragraphStyleId = new ParagraphStyleId() { Val = "Title" };
                paragraphProperties.AppendChild(paragraphStyleId);
                paragraph.PrependChild(paragraphProperties);
                
                paragraph.AppendChild(run);
                body.AppendChild(paragraph);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add Word title: {Title}", text);
                // Add fallback content
                var fallbackParagraph = new Paragraph(new Run(new Text("Document Title") { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(fallbackParagraph);
            }
        }

        /// <summary>
        /// Adds a heading to the Word document using Word's built-in heading styles
        /// This ensures proper navigation pane support and accessibility
        /// </summary>
        private void AddWordHeading(Body body, string text, int level)
        {
            try
            {
                var safeText = SanitizeTextForWord(text);
                var paragraph = new Paragraph();
                var run = new Run(new Text(safeText) { Space = SpaceProcessingModeValues.Preserve });
                
                // Use Word's built-in heading styles instead of direct formatting
                var paragraphProperties = new ParagraphProperties();
                var paragraphStyleId = new ParagraphStyleId() { Val = $"Heading{level}" };
                paragraphProperties.AppendChild(paragraphStyleId);
                paragraph.PrependChild(paragraphProperties);
                
                paragraph.AppendChild(run);
                body.AppendChild(paragraph);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add Word heading: {Text}", text);
                // Add fallback heading
                var fallbackParagraph = new Paragraph();
                var fallbackRun = new Run(new Text($"Section {level}") { Space = SpaceProcessingModeValues.Preserve });
                fallbackParagraph.AppendChild(fallbackRun);
                body.AppendChild(fallbackParagraph);
            }
        }

        /// <summary>
        /// Adds an empty line
        /// </summary>
        private void AddEmptyLine(Body body)
        {
            var paragraph = new Paragraph(new Run(new Text("") { Space = SpaceProcessingModeValues.Preserve }));
            body.AppendChild(paragraph);
        }

        /// <summary>
        /// Adds a paragraph to the Word document
        /// </summary>
        private void AddWordParagraph(Body body, string text)
        {
            try
            {
                var safeText = SanitizeTextForWord(text);
                var paragraph = new Paragraph(new Run(new Text(safeText) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(paragraph);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add Word paragraph: {Text}", text);
                // Add fallback paragraph
                var fallbackParagraph = new Paragraph(new Run(new Text("[Content Error]") { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(fallbackParagraph);
            }
        }

        /// <summary>
        /// Adds Executive Overview table
        /// </summary>
        private void AddExecutiveOverviewTable(Body body, AssessmentResult assessmentResult)
        {
            var table = new Table();

            // Table properties
            var tableProperties = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 }
                )
            );
            table.AppendChild(tableProperties);

            // Add rows
            AddTableRow(table, "Assessment ID", assessmentResult.AssessmentId);
            AddTableRow(table, "Assessment Date", assessmentResult.AssessmentDate.ToString("yyyy-MM-dd HH:mm:ss"));
            AddTableRow(table, "Cosmos Account", assessmentResult.CosmosAccountName ?? "Not specified");
            AddTableRow(table, "Database Name", assessmentResult.DatabaseName ?? "Not specified");
            AddTableRow(table, "Total Containers", assessmentResult.CosmosAnalysis?.Containers?.Count.ToString() ?? "0");
            AddTableRow(table, "Total Documents", assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.DocumentCount).ToString() ?? "0");
            AddTableRow(table, "Total Size (GB)", assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") ?? "0.00");

            body.AppendChild(table);
        }

        /// <summary>
        /// Adds Executive Overview explanation
        /// </summary>
        private void AddExecutiveOverviewExplanation(Body body)
        {
            AddWordParagraph(body, "This report provides a comprehensive analysis of your Cosmos DB database for migration to Azure SQL platforms. " +
                                   "The analysis includes container structure assessment, field mapping recommendations, indexing strategies, " +
                                   "and Azure Data Factory migration estimates. The recommendations are based on current usage patterns, " +
                                   "data distribution, and Azure SQL best practices.");
        }

        /// <summary>
        /// Adds Database Information table
        /// </summary>
        private void AddDatabaseInformationTable(Body body, AssessmentResult assessmentResult)
        {
            var table = new Table();

            // Table properties
            var tableProperties = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 }
                )
            );
            table.AppendChild(tableProperties);

            var dbMetrics = assessmentResult.CosmosAnalysis?.DatabaseMetrics;
            
            AddTableRow(table, "Database Name", assessmentResult.DatabaseName ?? "Not specified");
            AddTableRow(table, "Container Count", dbMetrics?.ContainerCount.ToString() ?? "0");
            AddTableRow(table, "Consistency Level", dbMetrics?.ConsistencyLevel ?? "Unknown");
            AddTableRow(table, "Total Documents", assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.DocumentCount).ToString() ?? "0");
            AddTableRow(table, "Total Storage Size (GB)", assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") ?? "0.00");
            AddTableRow(table, "Total Provisioned RUs", assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.ProvisionedRUs).ToString() ?? "0");

            body.AppendChild(table);
        }

        /// <summary>
        /// Adds container configuration details
        /// </summary>
        private void AddContainerConfiguration(Body body, ContainerAnalysis container)
        {
            AddWordParagraph(body, $"Partition Key: {container.PartitionKey ?? "Not specified"}");
            AddWordParagraph(body, $"Provisioned RUs: {container.ProvisionedRUs}");
            AddWordParagraph(body, $"Document Count: {container.DocumentCount:N0}");
            AddWordParagraph(body, $"Storage Size: {(container.SizeBytes / (1024.0 * 1024.0 * 1024.0)):F2} GB");
            AddWordParagraph(body, $"Detected Schemas: {container.DetectedSchemas?.Count ?? 0}");
        }

        /// <summary>
        /// Adds container fields table
        /// </summary>
        private void AddContainerFieldsTable(Body body, ContainerAnalysis container)
        {
            if (container.DetectedSchemas?.Any() != true)
            {
                AddWordParagraph(body, "No field schemas detected for this container.");
                return;
            }

            var table = new Table();

            // Table properties
            var tableProperties = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 }
                )
            );
            table.AppendChild(tableProperties);

            // Header row
            var headerRow = new TableRow();
            AddTableCell(headerRow, "Field Name", true);
            AddTableCell(headerRow, "Detected Types", true);
            AddTableCell(headerRow, "SQL Type", true);
            AddTableCell(headerRow, "Nullable", true);
            AddTableCell(headerRow, "Is Nested", true);
            table.AppendChild(headerRow);

            // Merge all schemas into distinct field list
            var allFields = new Dictionary<string, FieldInfo>();
            
            foreach (var schema in container.DetectedSchemas)
            {
                foreach (var field in schema.Fields)
                {
                    if (!allFields.ContainsKey(field.Key))
                    {
                        allFields[field.Key] = new FieldInfo
                        {
                            FieldName = field.Value.FieldName,
                            DetectedTypes = new List<string>(field.Value.DetectedTypes),
                            RecommendedSqlType = field.Value.RecommendedSqlType,
                            IsRequired = field.Value.IsRequired,
                            IsNested = field.Value.IsNested
                        };
                    }
                    else
                    {
                        // Merge detected types
                        foreach (var type in field.Value.DetectedTypes)
                        {
                            if (!allFields[field.Key].DetectedTypes.Contains(type))
                            {
                                allFields[field.Key].DetectedTypes.Add(type);
                            }
                        }
                        
                        // Update SQL type and required status
                        allFields[field.Key].RecommendedSqlType = GetBestSqlType(allFields[field.Key].DetectedTypes);
                        if (!field.Value.IsRequired)
                        {
                            allFields[field.Key].IsRequired = false;
                        }
                    }
                }
            }

            // Data rows
            foreach (var field in allFields.OrderBy(f => f.Key))
            {
                var dataRow = new TableRow();
                AddTableCell(dataRow, field.Key);
                AddTableCell(dataRow, string.Join(", ", field.Value.DetectedTypes));
                AddTableCell(dataRow, field.Value.RecommendedSqlType);
                AddTableCell(dataRow, field.Value.IsRequired ? "No" : "Yes");
                AddTableCell(dataRow, field.Value.IsNested ? "Yes" : "No");
                table.AppendChild(dataRow);
            }

            body.AppendChild(table);
        }

        /// <summary>
        /// Adds assessment summary bullets
        /// </summary>
        private void AddAssessmentSummaryBullets(Body body, AssessmentResult assessmentResult)
        {
            AddWordParagraph(body, $"• Database: {assessmentResult.DatabaseName ?? "Not specified"}");
            AddWordParagraph(body, $"• Containers: {assessmentResult.CosmosAnalysis?.Containers?.Count ?? 0}");
            AddWordParagraph(body, $"• Total Documents: {assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.DocumentCount):N0 ?? 0}");
            AddWordParagraph(body, $"• Total Size: {assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.SizeBytes / (1024.0 * 1024.0 * 1024.0)):F2 ?? 0} GB");
        }

        /// <summary>
        /// Adds migration recommendations bullets
        /// </summary>
        private void AddMigrationRecommendationsBullets(Body body, AssessmentResult assessmentResult)
        {
            if (assessmentResult.SqlAssessment != null)
            {
                AddWordParagraph(body, $"• Recommended Platform: {assessmentResult.SqlAssessment.RecommendedPlatform}");
                AddWordParagraph(body, $"• Recommended Tier: {assessmentResult.SqlAssessment.RecommendedTier}");
                AddWordParagraph(body, $"• Complexity: {assessmentResult.SqlAssessment.Complexity?.OverallComplexity ?? "Not assessed"}");
                AddWordParagraph(body, $"• Estimated Migration Days: {assessmentResult.SqlAssessment.Complexity?.EstimatedMigrationDays ?? 0}");
                
                // Group tables by size category in a single pass
                var allContainers = assessmentResult.SqlAssessment.DatabaseMappings
                    .SelectMany(dm => dm.ContainerMappings)
                    .ToList();
                
                var criticalTables = new List<ContainerMapping>();
                var highPriorityTables = new List<ContainerMapping>();
                var warningTables = new List<ContainerMapping>();
                
                foreach (var cm in allContainers)
                {
                    if (cm.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Critical)
                    {
                        criticalTables.Add(cm);
                    }
                    else if (cm.EstimatedRowCount >= MigrationConstants.RowCountThresholds.HighPriority)
                    {
                        highPriorityTables.Add(cm);
                    }
                    else if (cm.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Warning)
                    {
                        warningTables.Add(cm);
                    }
                }
                
                if (criticalTables.Any())
                {
                    AddWordParagraph(body, $"• 🔴 Critical: {criticalTables.Count} table(s) with >100M rows");
                }
                if (highPriorityTables.Any())
                {
                    AddWordParagraph(body, $"• 🟠 High Priority: {highPriorityTables.Count} table(s) with >10M rows");
                }
                if (warningTables.Any())
                {
                    AddWordParagraph(body, $"• 🟡 Warning: {warningTables.Count} table(s) with >1M rows");
                }
            }
        }

        /// <summary>
        /// Adds migration recommendations explanation
        /// </summary>
        private void AddMigrationRecommendationsExplanation(Body body, AssessmentResult assessmentResult)
        {
            if (assessmentResult.SqlAssessment != null)
            {
                var complexityText = assessmentResult.SqlAssessment.Complexity?.OverallComplexity?.ToLower() ?? "unknown";
                var explanation = $"The recommended {assessmentResult.SqlAssessment.RecommendedPlatform} platform with {assessmentResult.SqlAssessment.RecommendedTier} tier " +
                                $"is optimal for your workload based on the analysis of storage requirements, performance patterns, and throughput needs across all containers. " +
                                $"The {complexityText} complexity rating reflects the data structure analysis and indicates " +
                                $"the level of effort required for schema transformation and data migration. This recommendation encompasses sufficient capacity " +
                                $"for your current {assessmentResult.CosmosAnalysis?.Containers?.Sum(c => c.SizeBytes / (1024.0 * 1024.0 * 1024.0)):F2} GB of data " +
                                $"with appropriate performance headroom for future growth.";
                
                AddWordParagraph(body, explanation);
            }
        }

        /// <summary>
        /// Adds Data Factory estimates bullets
        /// </summary>
        private void AddDataFactoryEstimatesBullets(Body body, AssessmentResult assessmentResult)
        {
            if (assessmentResult.DataFactoryEstimate != null)
            {
                AddWordParagraph(body, $"• Migration Duration: {assessmentResult.DataFactoryEstimate.EstimatedDuration:hh\\:mm\\:ss}");
                AddWordParagraph(body, $"• Estimated Cost: ${assessmentResult.DataFactoryEstimate.EstimatedCostUSD:F2}");
                AddWordParagraph(body, $"• Data Volume (GB): {assessmentResult.DataFactoryEstimate.TotalDataSizeGB}");
                AddWordParagraph(body, $"• Recommended DIUs: {assessmentResult.DataFactoryEstimate.RecommendedDIUs}");
            }
        }

        /// <summary>
        /// Adds next steps numbered list
        /// </summary>
        private void AddNextStepsNumberedList(Body body)
        {
            AddWordParagraph(body, "1. Review the generated Excel report for detailed analysis");
            AddWordParagraph(body, "2. Share the Word document with stakeholders");
            AddWordParagraph(body, "3. Plan migration based on complexity assessment");
            AddWordParagraph(body, "4. Provision target Azure SQL infrastructure");
            AddWordParagraph(body, "5. Execute proof-of-concept migration if complexity is high");
            AddWordParagraph(body, "6. Validate schema transformations with sample data");
            AddWordParagraph(body, "7. Implement recommended indexing strategies post-migration");
            AddWordParagraph(body, "8. Monitor performance and optimize as needed");
        }

        /// <summary>
        /// Helper method to add a table row with two cells
        /// </summary>
        private void AddTableRow(Table table, string label, string value)
        {
            var row = new TableRow();
            AddTableCell(row, label, true);
            AddTableCell(row, value);
            table.AppendChild(row);
        }

        /// <summary>
        /// Helper method to add a table cell
        /// </summary>
        private void AddTableCell(TableRow row, string text, bool bold = false)
        {
            try
            {
                var safeText = SanitizeTextForWord(text);
                var cell = new TableCell();
                var paragraph = new Paragraph();
                var run = new Run(new Text(safeText) { Space = SpaceProcessingModeValues.Preserve });
                
                if (bold)
                {
                    var runProperties = new RunProperties(new Bold());
                    run.PrependChild(runProperties);
                }
                
                paragraph.AppendChild(run);
                cell.AppendChild(paragraph);
                row.AppendChild(cell);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add table cell with text: {Text}", text);
                // Add fallback cell with error indicator
                var fallbackCell = new TableCell();
                var fallbackParagraph = new Paragraph();
                var fallbackRun = new Run(new Text("[Error: Invalid Content]") { Space = SpaceProcessingModeValues.Preserve });
                
                if (bold)
                {
                    var runProperties = new RunProperties(new Bold());
                    fallbackRun.PrependChild(runProperties);
                }
                
                fallbackParagraph.AppendChild(fallbackRun);
                fallbackCell.AppendChild(fallbackParagraph);
                row.AppendChild(fallbackCell);
            }
        }

        /// <summary>
        /// Creates a valid Excel worksheet name that stays within Excel's constraints
        /// </summary>
        private string CreateValidWorksheetName(string baseName, string suffix = "")
        {
            // Remove invalid characters for Excel worksheet names
            var invalidChars = new char[] { ':', '\\', '/', '?', '*', '[', ']' };
            var cleanBaseName = baseName;
            foreach (var invalidChar in invalidChars)
            {
                cleanBaseName = cleanBaseName.Replace(invalidChar.ToString(), "");
            }

            // Handle multiple databases case with shorter naming
            if (cleanBaseName.StartsWith("Multiple Databases"))
            {
                // Extract database count from the name like "Multiple Databases (11)"
                var match = System.Text.RegularExpressions.Regex.Match(cleanBaseName, @"Multiple Databases \((\d+)\)");
                if (match.Success)
                {
                    var count = match.Groups[1].Value;
                    cleanBaseName = $"MultiDB({count})";
                }
                else
                {
                    cleanBaseName = "MultiDB";
                }
            }

            // Clean the suffix as well
            var cleanSuffix = suffix;
            foreach (var invalidChar in invalidChars)
            {
                cleanSuffix = cleanSuffix.Replace(invalidChar.ToString(), "");
            }

            // Combine base name with suffix
            var worksheetName = string.IsNullOrEmpty(cleanSuffix) ? cleanBaseName : $"{cleanBaseName}-{cleanSuffix}";

            // Ensure it stays within Excel's 31-character limit
            if (worksheetName.Length > 31)
            {
                if (!string.IsNullOrEmpty(cleanSuffix))
                {
                    // Calculate available space for base name (31 total - suffix length - 1 for dash)
                    var maxSuffixLength = Math.Min(cleanSuffix.Length, 15); // Limit suffix to 15 chars max
                    var availableForBase = 31 - maxSuffixLength - 1; // -1 for the dash
                    
                    // Ensure we have at least 3 characters for the base name
                    if (availableForBase < 3)
                    {
                        availableForBase = 3;
                        maxSuffixLength = 31 - availableForBase - 1;
                    }

                    var truncatedBase = cleanBaseName.Length > availableForBase 
                        ? cleanBaseName.Substring(0, Math.Max(1, availableForBase)) 
                        : cleanBaseName;
                    
                    var truncatedSuffix = cleanSuffix.Length > maxSuffixLength 
                        ? cleanSuffix.Substring(0, Math.Max(1, maxSuffixLength)) 
                        : cleanSuffix;

                    worksheetName = $"{truncatedBase}-{truncatedSuffix}";
                }
                else
                {
                    worksheetName = cleanBaseName.Length > 31 
                        ? cleanBaseName.Substring(0, 31) 
                        : cleanBaseName;
                }
            }

            return worksheetName;
        }

        /// <summary>
        /// Creates database constraints worksheet with foreign keys and unique constraints
        /// </summary>
        private void CreateConstraintsWorksheet(XLWorkbook workbook, AssessmentResult assessmentResult)
        {
            var ws = workbook.Worksheets.Add("Database Constraints");
            
            // Header
            ws.Cell("A1").Value = "Database Constraints and Referential Integrity";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 16;
            ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.Purple;

            var row = 3;

            // Foreign Key Constraints Section
            ws.Cell(row, 1).Value = "Foreign Key Constraints";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
            row += 2;

            if (assessmentResult.SqlAssessment?.ForeignKeyConstraints?.Any() == true)
            {
                // FK Headers
                ws.Cell(row, 1).Value = "Constraint Name";
                ws.Cell(row, 2).Value = "Child Table";
                ws.Cell(row, 3).Value = "Child Column";
                ws.Cell(row, 4).Value = "Parent Table";
                ws.Cell(row, 5).Value = "Parent Column";
                ws.Cell(row, 6).Value = "On Delete";
                ws.Cell(row, 7).Value = "On Update";
                ws.Cell(row, 8).Value = "Justification";

                for (int col = 1; col <= 8; col++)
                {
                    ws.Cell(row, col).Style.Font.Bold = true;
                    ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                }
                row++;

                // FK Data
                foreach (var fk in assessmentResult.SqlAssessment.ForeignKeyConstraints)
                {
                    ws.Cell(row, 1).Value = fk.ConstraintName;
                    ws.Cell(row, 2).Value = fk.ChildTable;
                    ws.Cell(row, 3).Value = fk.ChildColumn;
                    ws.Cell(row, 4).Value = fk.ParentTable;
                    ws.Cell(row, 5).Value = fk.ParentColumn;
                    ws.Cell(row, 6).Value = fk.OnDeleteAction;
                    ws.Cell(row, 7).Value = fk.OnUpdateAction;
                    ws.Cell(row, 8).Value = fk.Justification;
                    row++;
                }
            }
            else
            {
                ws.Cell(row, 1).Value = "No foreign key constraints generated.";
                row++;
            }

            row += 2;

            // Unique Constraints Section
            ws.Cell(row, 1).Value = "Unique Constraints (Business Keys)";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
            row += 2;

            if (assessmentResult.SqlAssessment?.UniqueConstraints?.Any() == true)
            {
                // UK Headers
                ws.Cell(row, 1).Value = "Constraint Name";
                ws.Cell(row, 2).Value = "Table Name";
                ws.Cell(row, 3).Value = "Columns";
                ws.Cell(row, 4).Value = "Type";
                ws.Cell(row, 5).Value = "Composite";
                ws.Cell(row, 6).Value = "Justification";

                for (int col = 1; col <= 6; col++)
                {
                    ws.Cell(row, col).Style.Font.Bold = true;
                    ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                }
                row++;

                // UK Data
                foreach (var uk in assessmentResult.SqlAssessment.UniqueConstraints)
                {
                    ws.Cell(row, 1).Value = uk.ConstraintName;
                    ws.Cell(row, 2).Value = uk.TableName;
                    ws.Cell(row, 3).Value = string.Join(", ", uk.Columns);
                    ws.Cell(row, 4).Value = uk.ConstraintType;
                    ws.Cell(row, 5).Value = uk.IsComposite ? "Yes" : "No";
                    ws.Cell(row, 6).Value = uk.Justification;
                    
                    // Color composite keys differently
                    if (uk.IsComposite)
                    {
                        ws.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    }
                    
                    row++;
                }
            }
            else
            {
                ws.Cell(row, 1).Value = "No unique constraints generated.";
                row++;
            }

            row += 2;

            // Implementation Notes
            ws.Cell(row, 1).Value = "Implementation Notes";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.Orange;
            row += 2;

            var notes = new[]
            {
                "• Foreign key constraints enforce referential integrity between related tables",
                "• CASCADE delete removes child records when parent is deleted",
                "• RESTRICT delete prevents deletion of parent if child records exist",
                "• Unique constraints ensure business key uniqueness and support efficient lookups",
                "• Composite constraints require ALL columns to be unique together",
                "• Always create supporting indexes for foreign key constraints",
                "• Consider performance impact of constraints on high-volume operations"
            };

            foreach (var note in notes)
            {
                ws.Cell(row, 1).Value = note;
                row++;
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Sanitizes text for safe inclusion in Word documents
        /// Handles XML special characters and invalid control characters
        /// </summary>
        private string SanitizeTextForWord(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                // Remove null characters and other problematic control characters
                // Allow only printable characters plus CR, LF, and TAB
                var cleaned = new string(text.Where(c => 
                    !char.IsControl(c) || 
                    c == '\r' || 
                    c == '\n' || 
                    c == '\t').ToArray());
                
                // Remove any remaining problematic Unicode characters
                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");
                
                // Limit length to prevent Word document bloat (Word has internal limits)
                if (cleaned.Length > 1000)
                {
                    cleaned = cleaned.Substring(0, 997) + "...";
                }
                
                // XML encode special characters to prevent XML parsing errors
                cleaned = cleaned.Replace("&", "&amp;")
                               .Replace("<", "&lt;")
                               .Replace(">", "&gt;")
                               .Replace("\"", "&quot;")
                               .Replace("'", "&apos;");
                
                // Handle edge case of completely empty result
                return string.IsNullOrWhiteSpace(cleaned) ? "[Empty]" : cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sanitize text: {Text}", text?.Substring(0, Math.Min(text.Length, 50)));
                return "[Text Processing Error]";
            }
        }

        /// <summary>
        /// Adds comprehensive SQL schema layout section showing the complete relational database design
        /// </summary>
        private void AddSqlSchemaLayout(Body body, AssessmentResult assessmentResult)
        {
            try
            {
                if (assessmentResult.SqlAssessment?.DatabaseMappings?.Any() != true)
                {
                    AddWordParagraph(body, "No SQL schema mappings available for this assessment.");
                    return;
                }

                AddWordParagraph(body, "This section outlines the complete relational database schema design generated from your Cosmos DB structure analysis. The proposed schema includes table structures, relationships, constraints, and performance optimizations.");
                AddEmptyLine(body);

                // Process each database mapping
                foreach (var databaseMapping in assessmentResult.SqlAssessment.DatabaseMappings)
                {
                    AddWordHeading(body, $"Database: {databaseMapping.TargetDatabase}", 2);
                    
                    // Table Structure Overview
                    AddWordHeading(body, "Table Structure Overview", 3);
                    AddTableStructureOverview(body, databaseMapping, assessmentResult);
                    AddEmptyLine(body);

                    // Table Size Estimates
                    AddWordHeading(body, "Table Size Estimates", 3);
                    AddTableSizeEstimates(body, databaseMapping);
                    AddEmptyLine(body);

                    // Shared Schema Analysis (if any)
                    if (assessmentResult.SqlAssessment.SharedSchemas?.Any() == true)
                    {
                        AddWordHeading(body, "Shared Schema Analysis", 3);
                        AddSharedSchemaAnalysis(body, assessmentResult.SqlAssessment.SharedSchemas);
                        AddEmptyLine(body);
                    }

                    // Relationship Diagram
                    AddWordHeading(body, "Table Relationships", 3);
                    AddTableRelationships(body, databaseMapping, assessmentResult);
                    AddEmptyLine(body);

                    // Index Recommendations
                    AddWordHeading(body, "Index Recommendations", 3);
                    AddIndexRecommendations(body, assessmentResult.SqlAssessment.IndexRecommendations);
                    AddEmptyLine(body);

                    // Constraint Definitions
                    AddWordHeading(body, "Constraint Definitions", 3);
                    AddConstraintDefinitions(body, assessmentResult.SqlAssessment);
                    AddEmptyLine(body);

                    // Normalization Results
                    AddWordHeading(body, "Normalization Results", 3);
                    AddNormalizationResults(body, databaseMapping);
                    AddEmptyLine(body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add SQL schema layout section");
                AddWordParagraph(body, "Unable to generate SQL schema layout due to data processing issues.");
            }
        }

        /// <summary>
        /// Adds table structure overview showing all tables and their columns
        /// </summary>
        private void AddTableStructureOverview(Body body, DatabaseMapping databaseMapping, AssessmentResult assessmentResult)
        {
            var table = new Table();
            AddTableBorders(table);

            // Header row
            var headerRow = new TableRow();
            AddTableCell(headerRow, "Table Name", true);
            AddTableCell(headerRow, "Column Name", true);
            AddTableCell(headerRow, "Data Type", true);
            AddTableCell(headerRow, "Nullable", true);
            AddTableCell(headerRow, "Source Field", true);
            table.AppendChild(headerRow);

            // Main tables
            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                foreach (var fieldMapping in containerMapping.FieldMappings)
                {
                    var dataRow = new TableRow();
                    AddTableCell(dataRow, $"{containerMapping.TargetSchema}.{containerMapping.TargetTable}");
                    AddTableCell(dataRow, fieldMapping.TargetColumn);
                    AddTableCell(dataRow, fieldMapping.TargetType);
                    AddTableCell(dataRow, fieldMapping.IsNullable ? "Yes" : "No");
                    AddTableCell(dataRow, fieldMapping.SourceField);
                    table.AppendChild(dataRow);
                }

                // Child tables
                foreach (var childTableMapping in containerMapping.ChildTableMappings)
                {
                    // Parent key column
                    var parentKeyRow = new TableRow();
                    AddTableCell(parentKeyRow, $"{childTableMapping.TargetSchema}.{childTableMapping.TargetTable}");
                    AddTableCell(parentKeyRow, childTableMapping.ParentKeyColumn);
                    AddTableCell(parentKeyRow, "UNIQUEIDENTIFIER");
                    AddTableCell(parentKeyRow, "No");
                    AddTableCell(parentKeyRow, "Generated (Foreign Key)");
                    table.AppendChild(parentKeyRow);

                    // Child table fields
                    foreach (var childFieldMapping in childTableMapping.FieldMappings)
                    {
                        var childDataRow = new TableRow();
                        AddTableCell(childDataRow, $"{childTableMapping.TargetSchema}.{childTableMapping.TargetTable}");
                        AddTableCell(childDataRow, childFieldMapping.TargetColumn);
                        AddTableCell(childDataRow, childFieldMapping.TargetType);
                        AddTableCell(childDataRow, childFieldMapping.IsNullable ? "Yes" : "No");
                        AddTableCell(childDataRow, $"{childTableMapping.SourceFieldPath}.{childFieldMapping.SourceField}");
                        table.AppendChild(childDataRow);
                    }
                }
            }

            body.AppendChild(table);
        }

        /// <summary>
        /// Adds shared schema analysis showing deduplicated table structures
        /// </summary>
        private void AddSharedSchemaAnalysis(Body body, List<SharedSchema> sharedSchemas)
        {
            AddWordParagraph(body, $"The analysis identified {sharedSchemas.Count} shared schema(s) that can be reused across multiple containers, reducing redundancy and improving maintainability:");
            AddEmptyLine(body);

            foreach (var sharedSchema in sharedSchemas)
            {
                AddWordParagraph(body, $"• {sharedSchema.TargetTable} - Used by {sharedSchema.UsageCount} container(s)");
                AddWordParagraph(body, $"  Containers: {string.Join(", ", sharedSchema.SourceContainers)}");
                AddWordParagraph(body, $"  Field Paths: {string.Join(", ", sharedSchema.SourceFieldPaths)}");
                AddEmptyLine(body);
            }
        }

        /// <summary>
        /// Adds table relationships showing foreign key connections
        /// </summary>
        private void AddTableRelationships(Body body, DatabaseMapping databaseMapping, AssessmentResult assessmentResult)
        {
            var relationships = new List<string>();

            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                var parentTable = $"{containerMapping.TargetSchema}.{containerMapping.TargetTable}";
                
                foreach (var childTableMapping in containerMapping.ChildTableMappings)
                {
                    var childTable = $"{childTableMapping.TargetSchema}.{childTableMapping.TargetTable}";
                    relationships.Add($"{parentTable} ←→ {childTable} (via {childTableMapping.ParentKeyColumn})");
                }
            }

            if (relationships.Any())
            {
                AddWordParagraph(body, "The following relationships are established through foreign key constraints:");
                AddEmptyLine(body);

                foreach (var relationship in relationships)
                {
                    AddWordParagraph(body, $"• {relationship}");
                }
            }
            else
            {
                AddWordParagraph(body, "No foreign key relationships identified in the current schema design.");
            }
        }

        /// <summary>
        /// Adds index recommendations for performance optimization
        /// </summary>
        private void AddIndexRecommendations(Body body, List<IndexRecommendation> indexRecommendations)
        {
            if (!indexRecommendations.Any())
            {
                AddWordParagraph(body, "No specific index recommendations generated for this schema.");
                return;
            }

            var table = new Table();
            AddTableBorders(table);

            // Header row
            var headerRow = new TableRow();
            AddTableCell(headerRow, "Table", true);
            AddTableCell(headerRow, "Index Name", true);
            AddTableCell(headerRow, "Type", true);
            AddTableCell(headerRow, "Columns", true);
            AddTableCell(headerRow, "Justification", true);
            table.AppendChild(headerRow);

            foreach (var index in indexRecommendations.OrderBy(i => i.TableName).ThenBy(i => i.Priority))
            {
                var dataRow = new TableRow();
                AddTableCell(dataRow, index.TableName);
                AddTableCell(dataRow, index.IndexName);
                AddTableCell(dataRow, index.IndexType);
                AddTableCell(dataRow, string.Join(", ", index.Columns));
                AddTableCell(dataRow, index.Justification);
                table.AppendChild(dataRow);
            }

            body.AppendChild(table);
        }

        /// <summary>
        /// Adds constraint definitions including foreign keys and unique constraints
        /// </summary>
        private void AddConstraintDefinitions(Body body, SqlMigrationAssessment sqlAssessment)
        {
            var constraintCount = (sqlAssessment.ForeignKeyConstraints?.Count ?? 0) + 
                                (sqlAssessment.UniqueConstraints?.Count ?? 0);

            if (constraintCount == 0)
            {
                AddWordParagraph(body, "No additional constraints defined beyond primary keys.");
                return;
            }

            AddWordParagraph(body, $"The schema includes {constraintCount} constraint(s) to ensure data integrity:");
            AddEmptyLine(body);

            // Foreign Key Constraints
            if (sqlAssessment.ForeignKeyConstraints?.Any() == true)
            {
                AddWordParagraph(body, "Foreign Key Constraints:");
                foreach (var fk in sqlAssessment.ForeignKeyConstraints)
                {
                    AddWordParagraph(body, $"• {fk.ConstraintName}: {fk.ChildTable}.{fk.ChildColumn} → {fk.ParentTable}.{fk.ParentColumn}");
                }
                AddEmptyLine(body);
            }

            // Unique Constraints
            if (sqlAssessment.UniqueConstraints?.Any() == true)
            {
                AddWordParagraph(body, "Unique Constraints:");
                foreach (var unique in sqlAssessment.UniqueConstraints)
                {
                    AddWordParagraph(body, $"• {unique.ConstraintName}: {unique.TableName}({string.Join(", ", unique.Columns)})");
                }
            }
        }

        /// <summary>
        /// Adds normalization results explaining how nested data was restructured
        /// </summary>
        private void AddNormalizationResults(Body body, DatabaseMapping databaseMapping)
        {
            var childTableCount = databaseMapping.ContainerMappings.Sum(c => c.ChildTableMappings.Count);
            
            if (childTableCount == 0)
            {
                AddWordParagraph(body, "No normalization was required - all data fits into flat table structures.");
                return;
            }

            AddWordParagraph(body, $"The normalization process created {childTableCount} additional table(s) to properly structure nested and array data:");
            AddEmptyLine(body);

            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                if (containerMapping.ChildTableMappings.Any())
                {
                    AddWordParagraph(body, $"Container '{containerMapping.SourceContainer}' normalization:");
                    
                    foreach (var childTable in containerMapping.ChildTableMappings)
                    {
                        var sourceDescription = childTable.ChildTableType == "Array" ? "array field" : "nested object";
                        AddWordParagraph(body, $"• {childTable.SourceFieldPath} ({sourceDescription}) → {childTable.TargetTable} table");
                    }
                    AddEmptyLine(body);
                }
            }
        }

        /// <summary>
        /// Adds table size estimates showing estimated row counts for each table
        /// </summary>
        private void AddTableSizeEstimates(Body body, DatabaseMapping databaseMapping)
        {
            AddWordParagraph(body, "The following table shows estimated row counts for each table in the target SQL database:");
            AddEmptyLine(body);

            var table = new Table();
            AddTableBorders(table);

            // Header row
            var headerRow = new TableRow();
            AddTableCell(headerRow, "Table Name", true);
            AddTableCell(headerRow, "Estimated Row Count", true);
            AddTableCell(headerRow, "Size Category", true);
            AddTableCell(headerRow, "Migration Considerations", true);
            table.AppendChild(headerRow);

            // Data rows for main tables
            foreach (var containerMapping in databaseMapping.ContainerMappings)
            {
                var dataRow = new TableRow();
                AddTableCell(dataRow, $"{containerMapping.TargetSchema}.{containerMapping.TargetTable}");
                AddTableCell(dataRow, containerMapping.EstimatedRowCount.ToString("N0"));
                
                // Determine size category and considerations
                string sizeCategory;
                string considerations;
                
                if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Critical)
                {
                    sizeCategory = "🔴 Critical (>100M)";
                    considerations = "Requires careful partitioning, incremental migration, and extensive performance testing";
                }
                else if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.HighPriority)
                {
                    sizeCategory = "🟠 High (>10M)";
                    considerations = "Consider partitioning strategy and incremental migration approach";
                }
                else if (containerMapping.EstimatedRowCount >= MigrationConstants.RowCountThresholds.Warning)
                {
                    sizeCategory = "🟡 Warning (>1M)";
                    considerations = "Monitor migration performance and consider batch processing";
                }
                else if (containerMapping.EstimatedRowCount > 0)
                {
                    sizeCategory = "✓ Standard";
                    considerations = "Standard migration approach suitable";
                }
                else
                {
                    sizeCategory = "Empty";
                    considerations = "No data to migrate";
                }
                
                AddTableCell(dataRow, sizeCategory);
                AddTableCell(dataRow, considerations);
                table.AppendChild(dataRow);
            }

            body.AppendChild(table);
        }

        /// <summary>
        /// Helper method to add consistent table borders
        /// </summary>
        private void AddTableBorders(Table table)
        {
            var tableProperties = new TableProperties(
                new TableBorders(
                    new TopBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new BottomBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new LeftBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new RightBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 12 },
                    new InsideHorizontalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 },
                    new InsideVerticalBorder() { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 6 }
                )
            );
            table.AppendChild(tableProperties);
        }
    }
}