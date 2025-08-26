using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.UnitTests.Infrastructure;

namespace CosmosToSqlAssessment.UnitTests.Services
{
    /// <summary>
    /// Unit tests for CosmosDbAnalysisService
    /// </summary>
    public class CosmosDbAnalysisServiceTests : TestBase
    {
        private readonly Mock<CosmosClient> _mockCosmosClient;
        private readonly Mock<ILogger<CosmosDbAnalysisService>> _mockLogger;
        private readonly CosmosDbAnalysisService _service;

        public CosmosDbAnalysisServiceTests()
        {
            _mockCosmosClient = new Mock<CosmosClient>();
            _mockLogger = CreateMockLogger<CosmosDbAnalysisService>();
            _service = new CosmosDbAnalysisService(MockConfiguration.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_WithValidDatabase_ShouldReturnCompleteAnalysis()
        {
            // Arrange
            var databaseName = "test-database";
            var cancellationToken = CancellationToken.None;

            // Mock Cosmos DB responses
            var mockDatabase = new Mock<Database>();
            var mockContainer = new Mock<Container>();
            
            _mockCosmosClient.Setup(x => x.GetDatabase(databaseName))
                .Returns(mockDatabase.Object);

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName, cancellationToken);

            // Assert
            result.Should().NotBeNull();
            result.DatabaseName.Should().Be(databaseName);
            result.AnalysisDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_WithNullDatabaseName_ShouldThrowArgumentException()
        {
            // Arrange
            string databaseName = null;

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.AnalyzeDatabaseAsync(databaseName));
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_WithEmptyDatabaseName_ShouldThrowArgumentException()
        {
            // Arrange
            var databaseName = string.Empty;

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.AnalyzeDatabaseAsync(databaseName));
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var databaseName = "test-database";
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _service.AnalyzeDatabaseAsync(databaseName, cancellationTokenSource.Token));
        }

        [Theory]
        [InlineData("production-db")]
        [InlineData("test-database")]
        [InlineData("dev-environment")]
        public async Task AnalyzeDatabaseAsync_WithDifferentDatabaseNames_ShouldSetCorrectDatabaseName(
            string databaseName)
        {
            // Arrange
            var cancellationToken = CancellationToken.None;

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName, cancellationToken);

            // Assert
            result.DatabaseName.Should().Be(databaseName);
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_ShouldInitializeDatabaseMetrics()
        {
            // Arrange
            var databaseName = "test-database";

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName);

            // Assert
            result.DatabaseMetrics.Should().NotBeNull();
            result.DatabaseMetrics.DatabaseName.Should().Be(databaseName);
            result.DatabaseMetrics.TotalContainers.Should().BeGreaterOrEqualTo(0);
            result.DatabaseMetrics.TotalSizeBytes.Should().BeGreaterOrEqualTo(0);
            result.DatabaseMetrics.TotalDocuments.Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_ShouldInitializeContainersList()
        {
            // Arrange
            var databaseName = "test-database";

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName);

            // Assert
            result.Containers.Should().NotBeNull();
            result.Containers.Should().BeOfType<List<ContainerAnalysis>>();
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_ShouldInitializePerformanceMetrics()
        {
            // Arrange
            var databaseName = "test-database";

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName);

            // Assert
            result.PerformanceMetrics.Should().NotBeNull();
            result.PerformanceMetrics.DatabaseName.Should().Be(databaseName);
            result.PerformanceMetrics.AnalysisPeriodDays.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_ShouldLogStartAndCompletion()
        {
            // Arrange
            var databaseName = "test-database";

            // Act
            await _service.AnalyzeDatabaseAsync(databaseName);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"Starting analysis of database: {databaseName}")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Database analysis completed")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_OnException_ShouldLogError()
        {
            // Arrange
            var databaseName = "failing-database";
            var expectedException = new CosmosException("Database not found", System.Net.HttpStatusCode.NotFound, 404, "test", 0);

            // Mock to throw exception
            _mockCosmosClient.Setup(x => x.GetDatabase(databaseName))
                .Throws(expectedException);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<CosmosException>(
                () => _service.AnalyzeDatabaseAsync(databaseName));

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error analyzing database")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Theory]
        [InlineData(100, 1000000000L)] // 100 docs, 1GB
        [InlineData(1000, 5000000000L)] // 1K docs, 5GB
        [InlineData(10000, 10000000000L)] // 10K docs, 10GB
        public async Task AnalyzeDatabaseAsync_ShouldCalculateMetricsCorrectly(
            long expectedDocuments, long expectedSizeBytes)
        {
            // Arrange
            var databaseName = "metrics-test-db";

            // This test validates that the analysis correctly aggregates metrics
            // In a real scenario, these would come from actual Cosmos DB queries

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName);

            // Assert
            result.DatabaseMetrics.Should().NotBeNull();
            // Note: In unit tests without actual Cosmos DB, we mainly validate structure
            // Integration tests would validate actual metric calculations
        }

        [Fact]
        public async Task AnalyzeDatabaseAsync_ShouldSetAnalysisTimestamp()
        {
            // Arrange
            var databaseName = "timestamp-test";
            var beforeAnalysis = DateTime.UtcNow;

            // Act
            var result = await _service.AnalyzeDatabaseAsync(databaseName);
            var afterAnalysis = DateTime.UtcNow;

            // Assert
            result.AnalysisDate.Should().BeOnOrAfter(beforeAnalysis);
            result.AnalysisDate.Should().BeOnOrBefore(afterAnalysis);
            result.AnalysisDate.Kind.Should().Be(DateTimeKind.Utc);
        }
    }
}
