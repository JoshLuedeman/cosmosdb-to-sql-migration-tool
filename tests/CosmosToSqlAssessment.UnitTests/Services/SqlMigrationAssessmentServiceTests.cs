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
    /// Unit tests for SqlMigrationAssessmentService
    /// </summary>
    public class SqlMigrationAssessmentServiceTests : TestBase
    {
        private readonly SqlMigrationAssessmentService _service;
        private readonly Mock<ILogger<SqlMigrationAssessmentService>> _mockLogger;

        public SqlMigrationAssessmentServiceTests()
        {
            _mockLogger = CreateMockLogger<SqlMigrationAssessmentService>();
            _service = new SqlMigrationAssessmentService(MockConfiguration.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task AssessMigrationAsync_WithValidCosmosAnalysis_ShouldReturnCompleteAssessment()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var cancellationToken = CancellationToken.None;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis, cancellationToken);

            // Assert
            result.Should().NotBeNull();
            result.RecommendedPlatform.Should().NotBeNullOrEmpty();
            result.RecommendedTier.Should().NotBeNullOrEmpty();
            result.DatabaseMappings.Should().NotBeEmpty();
            result.IndexRecommendations.Should().NotBeEmpty();
            result.Complexity.Should().NotBeNull();
            result.TransformationRules.Should().NotBeEmpty();
        }

        [Theory]
        [InlineData(5000000000L, 50000L, "AzureSqlDatabase")]
        [InlineData(500000000L, 5000L, "AzureSqlDatabase")]
        [InlineData(50000000000L, 500000L, "AzureSqlManagedInstance")]
        public async Task AssessMigrationAsync_WithDifferentDataSizes_ShouldRecommendCorrectPlatform(
            long totalSizeBytes, long totalDocuments, string expectedPlatform)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;
            cosmosAnalysis.DatabaseMetrics.TotalDocuments = totalDocuments;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.RecommendedPlatform.Should().Be(expectedPlatform);
        }

        [Theory]
        [InlineData(100, 1000000, "Basic")]
        [InlineData(500, 5000000, "Standard")]
        [InlineData(1000, 10000000, "Premium")]
        public async Task AssessMigrationAsync_WithDifferentPerformanceRequirements_ShouldRecommendCorrectTier(
            double averageRUs, long totalSizeBytes, string expectedTier)
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.PerformanceMetrics.AverageRUs = averageRUs;
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = totalSizeBytes;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.RecommendedTier.Should().Be(expectedTier);
        }

        [Fact]
        public async Task AssessMigrationAsync_WithHighNestingLevel_ShouldGenerateTransformationRules()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.Containers[0].DocumentStructure.MaxNestingLevel = 5;
            cosmosAnalysis.Containers[0].DocumentStructure.HasComplexObjects = true;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.TransformationRules.Should().NotBeEmpty();
            result.TransformationRules.Should().Contain(r => r.TransformationType == "Flatten");
        }

        [Fact]
        public async Task AssessMigrationAsync_WithArrayFields_ShouldGenerateSplitTransformations()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.Containers[0].DocumentStructure.HasArrays = true;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.TransformationRules.Should().Contain(r => r.TransformationType == "Split");
        }

        [Fact]
        public async Task AssessMigrationAsync_WithMultipleContainers_ShouldCreateMappingsForAll()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var containerCount = cosmosAnalysis.Containers.Count;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.DatabaseMappings.Should().NotBeEmpty();
            result.DatabaseMappings[0].ContainerMappings.Should().HaveCount(containerCount);
        }

        [Fact]
        public async Task AssessMigrationAsync_WithLargeDataset_ShouldAssessHighComplexity()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            cosmosAnalysis.DatabaseMetrics.TotalSizeBytes = 100000000000L; // 100GB
            cosmosAnalysis.DatabaseMetrics.ContainerCount = 20;

            // Act
            var result = await _service.AssessMigrationAsync(cosmosAnalysis);

            // Assert
            result.Complexity.OverallComplexity.Should().Be("High");
            result.Complexity.EstimatedMigrationDays.Should().BeGreaterThan(20);
        }

        [Fact]
        public async Task AssessMigrationAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var cosmosAnalysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _service.AssessMigrationAsync(cosmosAnalysis, cancellationTokenSource.Token));
        }
    }
}
