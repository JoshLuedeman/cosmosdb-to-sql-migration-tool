using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.UnitTests.Infrastructure;

namespace CosmosToSqlAssessment.UnitTests.Reporting
{
    /// <summary>
    /// Unit tests for ReportGenerationService
    /// </summary>
    public class ReportGenerationServiceTests : TestBase, IDisposable
    {
        private readonly ReportGenerationService _service;
        private readonly Mock<ILogger<ReportGenerationService>> _mockLogger;
        private readonly string _tempDirectory;

        public ReportGenerationServiceTests()
        {
            _mockLogger = CreateMockLogger<ReportGenerationService>();
            _service = new ReportGenerationService(MockConfiguration.Object, _mockLogger.Object);
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"CosmosToSqlTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithValidData_ShouldCreateExcelFile()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "test_report.xlsx");

            // Act
            await _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            File.Exists(outputPath).Should().BeTrue();
            var fileInfo = new FileInfo(outputPath);
            fileInfo.Length.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithNullCosmosAnalysis_ShouldThrowArgumentNullException()
        {
            // Arrange
            CosmosAnalysis cosmosAnalysis = null;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "test_report.xlsx");

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath));
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithNullSqlAssessment_ShouldThrowArgumentNullException()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            SqlAssessment sqlAssessment = null;
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "test_report.xlsx");

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath));
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithNullMigrationEstimate_ShouldThrowArgumentNullException()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            MigrationEstimate migrationEstimate = null;
            var outputPath = Path.Combine(_tempDirectory, "test_report.xlsx");

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath));
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithInvalidOutputPath_ShouldThrowArgumentException()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = ""; // Invalid path

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath));
        }

        [Fact]
        public async Task GenerateWordReportAsync_WithValidData_ShouldCreateWordFile()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "test_report.docx");

            // Act
            await _service.GenerateWordReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            File.Exists(outputPath).Should().BeTrue();
            var fileInfo = new FileInfo(outputPath);
            fileInfo.Length.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData("summary_report.xlsx")]
        [InlineData("detailed_analysis.xlsx")]
        [InlineData("migration_assessment.xlsx")]
        public async Task GenerateExcelReportAsync_WithDifferentFilenames_ShouldCreateFileWithCorrectName(
            string filename)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, filename);

            // Act
            await _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            File.Exists(outputPath).Should().BeTrue();
            Path.GetFileName(outputPath).Should().Be(filename);
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "cancelled_report.xlsx");
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath, cancellationTokenSource.Token));
        }

        [Fact]
        public async Task GenerateWordReportAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "cancelled_report.docx");
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _service.GenerateWordReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath, cancellationTokenSource.Token));
        }

        [Fact]
        public async Task GenerateExcelReportAsync_ShouldLogProgressMessages()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "logged_report.xlsx");

            // Act
            await _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Generating Excel report")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Excel report generated successfully")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task GenerateWordReportAsync_ShouldLogProgressMessages()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "logged_report.docx");

            // Act
            await _service.GenerateWordReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Generating Word report")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Word report generated successfully")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WhenDirectoryDoesNotExist_ShouldCreateDirectory()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var nonExistentDir = Path.Combine(_tempDirectory, "new_dir");
            var outputPath = Path.Combine(nonExistentDir, "report.xlsx");

            // Act
            await _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            Directory.Exists(nonExistentDir).Should().BeTrue();
            File.Exists(outputPath).Should().BeTrue();
        }

        [Fact]
        public async Task GenerateExcelReportAsync_WithComplexData_ShouldHandleAllDataTypes()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            
            // Add complex data scenarios
            cosmosAnalysis.Containers.ForEach(c =>
            {
                c.DocumentStructure.MaxNestingLevel = 5;
                c.DocumentStructure.HasArrays = true;
                c.DocumentStructure.HasComplexObjects = true;
                c.PerformanceMetrics.AverageRUs = 15.5;
                c.PerformanceMetrics.MaxRUs = 100.25;
            });

            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var migrationEstimate = TestDataFactory.CreateSampleMigrationEstimate();
            var outputPath = Path.Combine(_tempDirectory, "complex_data_report.xlsx");

            // Act
            await _service.GenerateExcelReportAsync(cosmosAnalysis, sqlAssessment, migrationEstimate, outputPath);

            // Assert
            File.Exists(outputPath).Should().BeTrue();
            var fileInfo = new FileInfo(outputPath);
            fileInfo.Length.Should().BeGreaterThan(1000); // Should be a substantial file with complex data
        }

        public void Dispose()
        {
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
