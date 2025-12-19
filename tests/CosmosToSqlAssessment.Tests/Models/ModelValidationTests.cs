using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Models;

public class ModelValidationTests : TestBase
{
    [Fact]
    public void AssessmentResult_ShouldInitialize()
    {
        // Act
        var result = new AssessmentResult();

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void AssessmentResult_WithTestData_ShouldBeValid()
    {
        // Act
        var result = TestDataFactory.CreateSampleAssessmentResult();

        // Assert
        result.DatabaseName.Should().NotBeNullOrEmpty();
        result.CosmosAnalysis.Should().NotBeNull();
        result.SqlAssessment.Should().NotBeNull();
        result.DataFactoryEstimate.Should().NotBeNull();
    }

    [Fact]
    public void CosmosDbAnalysis_ShouldInitializeCollections()
    {
        // Act
        var analysis = new CosmosDbAnalysis();

        // Assert
        analysis.Containers.Should().NotBeNull();
        analysis.Containers.Should().BeEmpty();
    }

    [Fact]
    public void SqlMigrationAssessment_ShouldInitializeCollections()
    {
        // Act
        var assessment = new SqlMigrationAssessment();

        // Assert
        assessment.DatabaseMappings.Should().NotBeNull();
        assessment.IndexRecommendations.Should().NotBeNull();
        assessment.TransformationRules.Should().NotBeNull();
    }

    [Fact]
    public void DataFactoryEstimate_ShouldInitializeCollections()
    {
        // Act
        var estimate = new DataFactoryEstimate();

        // Assert
        estimate.PipelineEstimates.Should().NotBeNull();
        estimate.Prerequisites.Should().NotBeNull();
        estimate.Recommendations.Should().NotBeNull();
    }

    [Fact]
    public void DatabaseMapping_ShouldInitializeCollections()
    {
        // Act
        var mapping = new DatabaseMapping();

        // Assert
        mapping.ContainerMappings.Should().NotBeNull();
        mapping.ContainerMappings.Should().BeEmpty();
    }

    [Fact]
    public void ContainerMapping_ShouldInitializeCollections()
    {
        // Act
        var mapping = new ContainerMapping();

        // Assert
        mapping.FieldMappings.Should().NotBeNull();
        mapping.RequiredTransformations.Should().NotBeNull();
    }

    [Fact]
    public void IndexRecommendation_ShouldInitializeCollections()
    {
        // Act
        var index = new IndexRecommendation();

        // Assert
        index.Columns.Should().NotBeNull();
        index.IncludedColumns.Should().NotBeNull();
    }

    [Fact]
    public void MigrationComplexity_ShouldInitializeCollections()
    {
        // Act
        var complexity = new MigrationComplexity();

        // Assert
        complexity.ComplexityFactors.Should().NotBeNull();
        complexity.Risks.Should().NotBeNull();
        complexity.Assumptions.Should().NotBeNull();
    }

    [Fact]
    public void TransformationRule_ShouldInitializeCollections()
    {
        // Act
        var rule = new TransformationRule();

        // Assert
        rule.AffectedTables.Should().NotBeNull();
    }
}
