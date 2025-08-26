using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.UnitTests.Infrastructure;

namespace CosmosToSqlAssessment.UnitTests.Integration
{
    /// <summary>
    /// Integration tests that verify the end-to-end workflow
    /// </summary>
    public class WorkflowIntegrationTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly string _tempDirectory;

        public WorkflowIntegrationTests()
        {
            // Setup dependency injection container for integration tests
            var services = new ServiceCollection();
            
            // Configuration
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("CosmosDb:ConnectionString", "AccountEndpoint=https://test.documents.azure.com:443/;AccountKey=testkey;"),
                    new KeyValuePair<string, string>("CosmosDb:DatabaseName", "test-database"),
                    new KeyValuePair<string, string>("Azure:SubscriptionId", "test-subscription"),
                    new KeyValuePair<string, string>("Azure:ResourceGroupName", "test-rg")
                })
                .Build();

            services.AddSingleton<IConfiguration>(configuration);
            
            // Logging
            services.AddLogging(builder => builder.AddConsole());
            
            // Services
            services.AddScoped<SqlMigrationAssessmentService>();
            services.AddScoped<DataFactoryEstimateService>();
            services.AddScoped<ReportGenerationService>();

            _serviceProvider = services.BuildServiceProvider();
            
            // Setup temp directory for test outputs
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlIntegrationTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);
        }

        [Fact]
        public async Task CompleteWorkflow_WithSampleData_ShouldGenerateReports()
        {
            // Arrange
            var sqlAssessmentService = _serviceProvider.GetRequiredService<SqlMigrationAssessmentService>();
            var dataFactoryService = _serviceProvider.GetRequiredService<DataFactoryEstimateService>();
            var reportService = _serviceProvider.GetRequiredService<ReportGenerationService>();

            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var excelOutputPath = Path.Combine(_tempDirectory, "integration_test_report.xlsx");
            var wordOutputPath = Path.Combine(_tempDirectory, "integration_test_report.docx");

            // Act - Step 1: Perform SQL Assessment
            var sqlAssessment = await sqlAssessmentService.AssessAsync(cosmosAnalysis);

            // Act - Step 2: Estimate Migration
            var migrationEstimate = await dataFactoryService.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Act - Step 3: Generate Reports
            await reportService.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, excelOutputPath);
            await reportService.GenerateWordReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, wordOutputPath);

            // Assert
            sqlAssessment.Should().NotBeNull();
            sqlAssessment.DatabaseName.Should().Be(cosmosAnalysis.DatabaseName);
            sqlAssessment.TableRecommendations.Should().NotBeEmpty();

            migrationEstimate.Should().NotBeNull();
            migrationEstimate.EstimatedDurationMinutes.Should().BeGreaterThan(0);
            migrationEstimate.EstimatedCost.Should().BeGreaterThan(0);

            File.Exists(excelOutputPath).Should().BeTrue();
            File.Exists(wordOutputPath).Should().BeTrue();

            var excelFileInfo = new FileInfo(excelOutputPath);
            var wordFileInfo = new FileInfo(wordOutputPath);
            excelFileInfo.Length.Should().BeGreaterThan(0);
            wordFileInfo.Length.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task SqlAssessmentService_WithComplexCosmosData_ShouldHandleComplexStructures()
        {
            // Arrange
            var service = _serviceProvider.GetRequiredService<SqlMigrationAssessmentService>();
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            
            // Add complexity to test data
            cosmosAnalysis.Containers.ForEach(container =>
            {
                container.DocumentStructure.MaxNestingLevel = 4;
                container.DocumentStructure.HasArrays = true;
                container.DocumentStructure.HasComplexObjects = true;
                container.DocumentStructure.UniqueFieldCount = 50;
            });

            // Act
            var result = await service.AssessAsync(cosmosAnalysis);

            // Assert
            result.Should().NotBeNull();
            result.ComplexityScore.Should().BeGreaterThan(1); // Should recognize complexity
            result.TransformationRules.Should().NotBeEmpty(); // Should have transformation rules for complex data
            result.TableRecommendations.Should().NotBeEmpty();
            
            // Should recommend appropriate platform for complex data
            result.RecommendedPlatform.Should().BeOneOf("Azure SQL Database", "Azure SQL Managed Instance");
        }

        [Fact]
        public async Task DataFactoryEstimateService_WithLargeDataset_ShouldRecommendOptimalSettings()
        {
            // Arrange
            var service = _serviceProvider.GetRequiredService<DataFactoryEstimateService>();
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = 100_000_000_000L; // 100GB
            cosmosAnalysis.DatabaseMetrics.TotalDocuments = 50_000_000L; // 50M documents

            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.Should().NotBeNull();
            result.TotalDataSizeGB.Should().Be(100); // Correctly converted from bytes
            result.RecommendedDIUs.Should().BeGreaterOrEqualTo(8); // Should recommend higher DIUs for large data
            result.RecommendedParallelCopies.Should().BeGreaterThan(1); // Should recommend parallel processing
            result.EstimatedDurationMinutes.Should().BeGreaterThan(120); // Large data takes time
            result.Considerations.Should().Contain(c => c.Contains("incremental") || c.Contains("parallel") || c.Contains("performance"));
        }

        [Fact]
        public async Task ReportGeneration_WithCompleteData_ShouldCreateDetailedReports()
        {
            // Arrange
            var reportService = _serviceProvider.GetRequiredService<ReportGenerationService>();
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();

            // Add comprehensive data for detailed reports
            sqlAssessment.IndexRecommendations.AddRange(new[]
            {
                new IndexRecommendation 
                { 
                    IndexName = "IX_Users_Email", 
                    TableName = "users", 
                    IndexType = "NONCLUSTERED",
                    Columns = new List<string> { "email" },
                    PerformanceImpact = "High"
                },
                new IndexRecommendation 
                { 
                    IndexName = "IX_Orders_CustomerId_Date", 
                    TableName = "orders", 
                    IndexType = "NONCLUSTERED",
                    Columns = new List<string> { "customer_id", "order_date" },
                    PerformanceImpact = "Medium"
                }
            });

            var excelPath = Path.Combine(_tempDirectory, "detailed_report.xlsx");
            var wordPath = Path.Combine(_tempDirectory, "detailed_report.docx");

            // Act
            await reportService.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, excelPath);
            await reportService.GenerateWordReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, wordPath);

            // Assert
            File.Exists(excelPath).Should().BeTrue();
            File.Exists(wordPath).Should().BeTrue();

            var excelSize = new FileInfo(excelPath).Length;
            var wordSize = new FileInfo(wordPath).Length;

            excelSize.Should().BeGreaterThan(5000); // Should be substantial with detailed data
            wordSize.Should().BeGreaterThan(10000); // Word docs tend to be larger
        }

        [Fact]
        public async Task ServiceChaining_WithCancellation_ShouldRespectCancellationTokens()
        {
            // Arrange
            var sqlAssessmentService = _serviceProvider.GetRequiredService<SqlMigrationAssessmentService>();
            var dataFactoryService = _serviceProvider.GetRequiredService<DataFactoryEstimateService>();
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => sqlAssessmentService.AssessAsync(cosmosAnalysis, cancellationTokenSource.Token));

            // For services that would have succeeded, test cancellation in the next step
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => dataFactoryService.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment, cancellationTokenSource.Token));
        }

        [Fact]
        public async Task ErrorHandling_WithInvalidData_ShouldProvideAppropriateErrors()
        {
            // Arrange
            var sqlAssessmentService = _serviceProvider.GetRequiredService<SqlMigrationAssessmentService>();
            CosmosAnalysis invalidAnalysis = null;

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => sqlAssessmentService.AssessAsync(invalidAnalysis));
        }

        [Theory]
        [InlineData(1_000_000L, "Azure SQL Database")] // Small dataset
        [InlineData(50_000_000_000L, "Azure SQL Managed Instance")] // Large dataset
        [InlineData(500_000_000_000L, "Azure Synapse Analytics")] // Very large dataset
        public async Task PlatformRecommendation_BasedOnDataSize_ShouldSelectAppropriately(
            long dataSizeBytes, string expectedPlatformType)
        {
            // Arrange
            var service = _serviceProvider.GetRequiredService<SqlMigrationAssessmentService>();
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = dataSizeBytes;

            // Act
            var result = await service.AssessAsync(cosmosAnalysis);

            // Assert
            result.RecommendedPlatform.Should().Contain(expectedPlatformType);
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
            
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }
}
