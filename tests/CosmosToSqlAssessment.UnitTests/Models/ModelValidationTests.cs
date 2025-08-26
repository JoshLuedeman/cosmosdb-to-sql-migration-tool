using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using FluentAssertions;
using Xunit;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.UnitTests.Infrastructure;

namespace CosmosToSqlAssessment.UnitTests.Models
{
    /// <summary>
    /// Unit tests for model validation and data integrity
    /// </summary>
    public class ModelValidationTests
    {
        [Fact]
        public void CosmosAnalysis_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();

            // Act
            var validationResults = ValidateModel(analysis);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Fact]
        public void CosmosAnalysis_WithNullDatabaseName_ShouldFailValidation()
        {
            // Arrange
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();
            analysis.DatabaseName = null;

            // Act
            var validationResults = ValidateModel(analysis);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(CosmosAnalysis.DatabaseName)));
        }

        [Fact]
        public void CosmosAnalysis_WithEmptyDatabaseName_ShouldFailValidation()
        {
            // Arrange
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();
            analysis.DatabaseName = string.Empty;

            // Act
            var validationResults = ValidateModel(analysis);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(CosmosAnalysis.DatabaseName)));
        }

        [Fact]
        public void DatabaseMetrics_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var metrics = TestDataFactory.CreateSampleDatabaseMetrics();

            // Act
            var validationResults = ValidateModel(metrics);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-100)]
        public void DatabaseMetrics_WithNegativeTotalContainers_ShouldFailValidation(int invalidValue)
        {
            // Arrange
            var metrics = TestDataFactory.CreateSampleDatabaseMetrics();
            metrics.TotalContainers = invalidValue;

            // Act
            var validationResults = ValidateModel(metrics);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(DatabaseMetrics.TotalContainers)));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-1000)]
        public void DatabaseMetrics_WithNegativeTotalSizeBytes_ShouldFailValidation(long invalidValue)
        {
            // Arrange
            var metrics = TestDataFactory.CreateSampleDatabaseMetrics();
            metrics.TotalSizeBytes = invalidValue;

            // Act
            var validationResults = ValidateModel(metrics);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(DatabaseMetrics.TotalSizeBytes)));
        }

        [Fact]
        public void ContainerAnalysis_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var container = TestDataFactory.CreateSampleContainerAnalysis();

            // Act
            var validationResults = ValidateModel(container);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Fact]
        public void ContainerAnalysis_WithNullContainerName_ShouldFailValidation()
        {
            // Arrange
            var container = TestDataFactory.CreateSampleContainerAnalysis();
            container.ContainerName = null;

            // Act
            var validationResults = ValidateModel(container);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(ContainerAnalysis.ContainerName)));
        }

        [Fact]
        public void SqlAssessment_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var assessment = TestDataFactory.CreateSampleSqlAssessment();

            // Act
            var validationResults = ValidateModel(assessment);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void SqlAssessment_WithInvalidDatabaseName_ShouldFailValidation(string invalidDatabaseName)
        {
            // Arrange
            var assessment = TestDataFactory.CreateSampleSqlAssessment();
            assessment.DatabaseName = invalidDatabaseName;

            // Act
            var validationResults = ValidateModel(assessment);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(SqlAssessment.DatabaseName)));
        }

        [Fact]
        public void TableRecommendation_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var recommendation = TestDataFactory.CreateSampleTableRecommendation();

            // Act
            var validationResults = ValidateModel(recommendation);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Fact]
        public void TableRecommendation_WithNullTableName_ShouldFailValidation()
        {
            // Arrange
            var recommendation = TestDataFactory.CreateSampleTableRecommendation();
            recommendation.TableName = null;

            // Act
            var validationResults = ValidateModel(recommendation);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(TableRecommendation.TableName)));
        }

        [Fact]
        public void ColumnDefinition_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var column = TestDataFactory.CreateSampleColumnDefinition();

            // Act
            var validationResults = ValidateModel(column);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ColumnDefinition_WithInvalidColumnName_ShouldFailValidation(string invalidColumnName)
        {
            // Arrange
            var column = TestDataFactory.CreateSampleColumnDefinition();
            column.ColumnName = invalidColumnName;

            // Act
            var validationResults = ValidateModel(column);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(ColumnDefinition.ColumnName)));
        }

        [Fact]
        public void MigrationEstimate_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var estimate = TestDataFactory.CreateSampleMigrationEstimate();

            // Act
            var validationResults = ValidateModel(estimate);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-100)]
        public void MigrationEstimate_WithNegativeDuration_ShouldFailValidation(int invalidDuration)
        {
            // Arrange
            var estimate = TestDataFactory.CreateSampleMigrationEstimate();
            estimate.EstimatedDurationMinutes = invalidDuration;

            // Act
            var validationResults = ValidateModel(estimate);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(MigrationEstimate.EstimatedDurationMinutes)));
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(-100.50)]
        public void MigrationEstimate_WithNegativeCost_ShouldFailValidation(decimal invalidCost)
        {
            // Arrange
            var estimate = TestDataFactory.CreateSampleMigrationEstimate();
            estimate.EstimatedCost = invalidCost;

            // Act
            var validationResults = ValidateModel(estimate);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(MigrationEstimate.EstimatedCost)));
        }

        [Fact]
        public void PerformanceMetrics_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var metrics = TestDataFactory.CreateSamplePerformanceMetrics();

            // Act
            var validationResults = ValidateModel(metrics);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(-50.5)]
        public void PerformanceMetrics_WithNegativeAverageRUs_ShouldFailValidation(double invalidRUs)
        {
            // Arrange
            var metrics = TestDataFactory.CreateSamplePerformanceMetrics();
            metrics.AverageRUs = invalidRUs;

            // Act
            var validationResults = ValidateModel(metrics);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(PerformanceMetrics.AverageRUs)));
        }

        [Fact]
        public void TransformationRule_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var rule = TestDataFactory.CreateSampleTransformationRule();

            // Act
            var validationResults = ValidateModel(rule);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void TransformationRule_WithInvalidTransformationType_ShouldFailValidation(string invalidType)
        {
            // Arrange
            var rule = TestDataFactory.CreateSampleTransformationRule();
            rule.TransformationType = invalidType;

            // Act
            var validationResults = ValidateModel(rule);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(TransformationRule.TransformationType)));
        }

        [Fact]
        public void IndexRecommendation_WithValidData_ShouldPassValidation()
        {
            // Arrange
            var index = TestDataFactory.CreateSampleIndexRecommendation();

            // Act
            var validationResults = ValidateModel(index);

            // Assert
            validationResults.Should().BeEmpty();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void IndexRecommendation_WithInvalidIndexName_ShouldFailValidation(string invalidIndexName)
        {
            // Arrange
            var index = TestDataFactory.CreateSampleIndexRecommendation();
            index.IndexName = invalidIndexName;

            // Act
            var validationResults = ValidateModel(index);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Should().Contain(vr => vr.MemberNames.Contains(nameof(IndexRecommendation.IndexName)));
        }

        [Fact]
        public void ComplexModel_WithNestedValidationErrors_ShouldReportAllErrors()
        {
            // Arrange
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();
            analysis.DatabaseName = null; // Invalid
            analysis.DatabaseMetrics.TotalContainers = -1; // Invalid
            analysis.Containers.First().ContainerName = ""; // Invalid

            // Act
            var validationResults = ValidateModel(analysis);

            // Assert
            validationResults.Should().NotBeEmpty();
            validationResults.Count.Should().BeGreaterOrEqualTo(3); // Should report multiple errors
        }

        [Fact]
        public void Model_CollectionProperties_ShouldNotBeNull()
        {
            // Arrange & Act
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();

            // Assert
            analysis.Containers.Should().NotBeNull();
            analysis.PerformanceMetrics.Should().NotBeNull();
            
            var sqlAssessment = TestDataFactory.CreateSampleSqlAssessment();
            sqlAssessment.TableRecommendations.Should().NotBeNull();
            sqlAssessment.IndexRecommendations.Should().NotBeNull();
            sqlAssessment.TransformationRules.Should().NotBeNull();
        }

        [Fact]
        public void Model_DateTimeProperties_ShouldBeUtc()
        {
            // Arrange & Act
            var analysis = TestDataFactory.CreateSampleCosmosAnalysis();
            var assessment = TestDataFactory.CreateSampleSqlAssessment();

            // Assert
            analysis.AnalysisDate.Kind.Should().Be(DateTimeKind.Utc);
            assessment.AssessmentDate.Kind.Should().Be(DateTimeKind.Utc);
        }

        /// <summary>
        /// Helper method to validate a model using data annotations
        /// </summary>
        private static List<ValidationResult> ValidateModel(object model)
        {
            var context = new ValidationContext(model, serviceProvider: null, items: null);
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(model, context, validationResults, validateAllProperties: true);
            return validationResults;
        }
    }
}
