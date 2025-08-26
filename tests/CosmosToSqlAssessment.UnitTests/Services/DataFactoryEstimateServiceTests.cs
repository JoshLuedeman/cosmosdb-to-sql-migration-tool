using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.UnitTests.Infrastructure;

namespace CosmosToSqlAssessment.UnitTests.Services
{
    /// <summary>
    /// Unit tests for DataFactoryEstimateService
    /// </summary>
    public class DataFactoryEstimateServiceTests : TestBase
    {
        private readonly DataFactoryEstimateService _service;
        private readonly Mock<ILogger<DataFactoryEstimateService>> _mockLogger;

        public DataFactoryEstimateServiceTests()
        {
            _mockLogger = CreateMockLogger<DataFactoryEstimateService>();
            _service = new DataFactoryEstimateService(MockConfiguration.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task EstimateMigrationAsync_WithValidInputs_ShouldReturnCompleteEstimate()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var cancellationToken = CancellationToken.None;

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment, cancellationToken);

            // Assert
            result.Should().NotBeNull();
            result.EstimatedDurationMinutes.Should().BeGreaterThan(0);
            result.EstimatedCost.Should().BeGreaterThan(0);
            result.RecommendedDIUs.Should().BeGreaterThan(0);
            result.RecommendedParallelCopies.Should().BeGreaterThan(0);
            result.TotalDataSizeGB.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData(1000000000L, 1)] // 1GB
        [InlineData(5000000000L, 5)] // 5GB  
        [InlineData(10000000000L, 10)] // 10GB
        [InlineData(50000000000L, 50)] // 50GB
        public async Task EstimateMigrationAsync_WithDifferentDataSizes_ShouldCalculateCorrectDataSize(
            long totalSizeBytes, int expectedSizeGB)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.TotalDataSizeGB.Should().Be(expectedSizeGB);
        }

        [Theory]
        [InlineData(1000000000L, 2)] // 1GB -> 2 DIUs (minimum)
        [InlineData(10000000000L, 4)] // 10GB -> 4 DIUs
        [InlineData(50000000000L, 8)] // 50GB -> 8 DIUs
        [InlineData(100000000000L, 16)] // 100GB -> 16 DIUs
        public async Task EstimateMigrationAsync_WithDifferentDataSizes_ShouldRecommendAppropriateDIUs(
            long totalSizeBytes, int minimumExpectedDIUs)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.RecommendedDIUs.Should().BeGreaterOrEqualTo(minimumExpectedDIUs);
        }

        [Fact]
        public async Task EstimateMigrationAsync_WithMultipleContainers_ShouldRecommendParallelCopies()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var containerCount = cosmosAnalysis.Containers.Count;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.RecommendedParallelCopies.Should().BeGreaterOrEqualTo(1);
            result.RecommendedParallelCopies.Should().BeLessOrEqualTo(containerCount);
        }

        [Theory]
        [InlineData(1000000000L, 30, 120)] // 1GB: 30-120 minutes
        [InlineData(10000000000L, 60, 300)] // 10GB: 60-300 minutes  
        [InlineData(100000000000L, 300, 1440)] // 100GB: 300-1440 minutes
        public async Task EstimateMigrationAsync_WithDifferentDataSizes_ShouldEstimateReasonableDuration(
            long totalSizeBytes, int minMinutes, int maxMinutes)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.EstimatedDurationMinutes.Should().BeInRange(minMinutes, maxMinutes);
        }

        [Fact]
        public async Task EstimateMigrationAsync_WithComplexTransformations_ShouldIncreaseEstimate()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.Containers.ForEach(c => 
            {
                c.DocumentStructure.MaxNestingLevel = 5;
                c.DocumentStructure.HasArrays = true;
                c.DocumentStructure.HasComplexObjects = true;
            });

            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            sqlAssessment.TransformationRules.Add(new TransformationRule 
            { 
                TransformationType = "Flatten",
                AffectedTables = new List<string> { "users", "orders" }
            });

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.EstimatedDurationMinutes.Should().BeGreaterThan(60); // Complex transformations take time
            result.EstimatedCost.Should().BeGreaterThan(5); // Higher cost due to complexity
        }

        [Fact]
        public async Task EstimateMigrationAsync_ShouldIncludePerformanceConsiderations()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.Considerations.Should().NotBeEmpty();
            result.Considerations.Should().Contain(c => c.Contains("parallel") || c.Contains("batch") || c.Contains("performance"));
        }

        [Fact]
        public async Task EstimateMigrationAsync_WithHighVolumeData_ShouldRecommendIncrementalCopy()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = 100000000000L; // 100GB
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.Considerations.Should().Contain(c => c.Contains("incremental") || c.Contains("delta"));
        }

        [Fact]
        public async Task EstimateMigrationAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment, cancellationTokenSource.Token));
        }

        [Theory]
        [InlineData(1000000000L, 2, 1)] // 1GB: min DIUs, parallel copies
        [InlineData(10000000000L, 4, 2)] // 10GB: moderate DIUs
        [InlineData(100000000000L, 16, 4)] // 100GB: high DIUs
        public async Task EstimateMigrationAsync_ShouldCalculateCostBasedOnDIUsAndDuration(
            long totalSizeBytes, int expectedMinDIUs, int expectedMinParallel)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var result = await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            result.RecommendedDIUs.Should().BeGreaterOrEqualTo(expectedMinDIUs);
            result.RecommendedParallelCopies.Should().BeGreaterOrEqualTo(expectedMinParallel);
            
            // Cost should be reasonable (basic validation)
            result.EstimatedCost.Should().BeInRange(1m, 1000m);
        }

        [Fact]
        public async Task EstimateMigrationAsync_ShouldLogAppropriateMessages()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            await _service.EstimateMigrationAsync(cosmosAnalysis, sqlAssessment);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Starting Data Factory migration estimation")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }
    }
}
