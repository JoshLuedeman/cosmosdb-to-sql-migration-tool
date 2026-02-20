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

    // ────────────────────────────────────────────────────────────────
    // SqlModels – newly covered types
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SharedSchema_ShouldInitializeCollections()
    {
        // Act
        var schema = new SharedSchema();

        // Assert
        schema.FieldMappings.Should().NotBeNull();
        schema.SourceContainers.Should().NotBeNull();
        schema.SourceFieldPaths.Should().NotBeNull();
        schema.TargetSchema.Should().Be("dbo");
    }

    [Fact]
    public void ChildTableMapping_DefaultValues_ShouldBeValid()
    {
        // Act
        var mapping = new ChildTableMapping();

        // Assert
        mapping.TargetSchema.Should().Be("dbo");
        mapping.ParentKeyColumn.Should().Be("ParentId");
        mapping.FieldMappings.Should().NotBeNull();
        mapping.RequiredTransformations.Should().NotBeNull();
        mapping.SharedSchemaId.Should().BeNull();
    }

    [Fact]
    public void ForeignKeyConstraint_DefaultValues_ShouldBeValid()
    {
        // Act
        var fk = new ForeignKeyConstraint();

        // Assert
        fk.OnDeleteAction.Should().Be("CASCADE");
        fk.OnUpdateAction.Should().Be("CASCADE");
        fk.IsDeferrable.Should().BeFalse();
        fk.ConstraintName.Should().BeEmpty();
    }

    [Fact]
    public void UniqueConstraint_DefaultValues_ShouldBeValid()
    {
        // Act
        var uc = new UniqueConstraint();

        // Assert
        uc.Columns.Should().NotBeNull();
        uc.IsComposite.Should().BeFalse();
        uc.ConstraintName.Should().BeEmpty();
    }

    [Fact]
    public void ComplexityFactor_CanStoreValues()
    {
        // Act
        var factor = new ComplexityFactor
        {
            Factor = "Schema Depth",
            Impact = "High",
            Description = "Documents nested more than 5 levels deep"
        };

        // Assert
        factor.Factor.Should().Be("Schema Depth");
        factor.Impact.Should().Be("High");
        factor.Description.Should().NotBeEmpty();
    }

    [Fact]
    public void PipelineEstimate_DefaultValues_ShouldBeValid()
    {
        // Act
        var estimate = new PipelineEstimate();

        // Assert
        estimate.SourceContainer.Should().BeEmpty();
        estimate.TargetTable.Should().BeEmpty();
        estimate.DataSizeGB.Should().Be(0);
        estimate.RequiresTransformation.Should().BeFalse();
    }

    [Fact]
    public void RecommendationItem_DefaultValues_ShouldBeValid()
    {
        // Act
        var item = new RecommendationItem();

        // Assert
        item.Category.Should().BeEmpty();
        item.Title.Should().BeEmpty();
        item.Priority.Should().BeEmpty();
        item.Impact.Should().BeEmpty();
    }

    [Fact]
    public void FieldMapping_DefaultValues_ShouldBeValid()
    {
        // Act
        var mapping = new FieldMapping();

        // Assert
        mapping.IsNullable.Should().BeTrue();
        mapping.IsPartitionKey.Should().BeFalse();
        mapping.RequiresTransformation.Should().BeFalse();
        mapping.SourceField.Should().BeEmpty();
        mapping.TargetColumn.Should().BeEmpty();
    }

    // ────────────────────────────────────────────────────────────────
    // CosmosModels – newly covered types
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void DatabaseMetrics_DefaultValues_ShouldBeValid()
    {
        // Act
        var metrics = new DatabaseMetrics();

        // Assert
        metrics.TotalDocuments.Should().Be(0);
        metrics.TotalSizeBytes.Should().Be(0);
        metrics.ContainerCount.Should().Be(0);
        metrics.IsServerlessAccount.Should().BeFalse();
    }

    [Fact]
    public void ContainerIndexingPolicy_ShouldInitializeCollections()
    {
        // Act
        var policy = new ContainerIndexingPolicy();

        // Assert
        policy.IncludedPaths.Should().NotBeNull();
        policy.ExcludedPaths.Should().NotBeNull();
        policy.CompositeIndexes.Should().NotBeNull();
        policy.SpatialIndexes.Should().NotBeNull();
    }

    [Fact]
    public void CompositeIndex_ShouldInitializePathList()
    {
        // Act
        var index = new CompositeIndex();

        // Assert
        index.Paths.Should().NotBeNull();
        index.Paths.Should().BeEmpty();
    }

    [Fact]
    public void SpatialIndex_ShouldInitializeTypesList()
    {
        // Act
        var index = new SpatialIndex();

        // Assert
        index.Types.Should().NotBeNull();
        index.Types.Should().BeEmpty();
    }

    [Fact]
    public void ContainerPerformanceMetrics_ShouldInitializeCollections()
    {
        // Act
        var metrics = new ContainerPerformanceMetrics();

        // Assert
        metrics.TopQueries.Should().NotBeNull();
        metrics.HotPartitions.Should().NotBeNull();
    }

    [Fact]
    public void PerformanceMetrics_ShouldInitializeCollections()
    {
        // Act
        var metrics = new PerformanceMetrics();

        // Assert
        metrics.Trends.Should().NotBeNull();
        metrics.AnalysisPeriod.Should().NotBeNull();
    }

    [Fact]
    public void ArrayAnalysis_DefaultValues_ShouldBeValid()
    {
        // Act
        var analysis = new ArrayAnalysis();

        // Assert
        analysis.ArrayName.Should().BeEmpty();
        analysis.ItemCount.Should().Be(0);
        analysis.ShouldCreateTable.Should().BeFalse();
        analysis.RecommendedStorage.Should().BeEmpty();
    }

    [Fact]
    public void ChildTableSchema_ShouldInitializeCollections()
    {
        // Act
        var schema = new ChildTableSchema();

        // Assert
        schema.RecommendedIndexes.Should().NotBeNull();
        schema.TransformationNotes.Should().NotBeNull();
        schema.Fields.Should().NotBeNull();
        schema.ParentKeyField.Should().Be("ParentId");
    }

    // ────────────────────────────────────────────────────────────────
    // MigrationConstants
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void MigrationConstants_RowCountThresholds_ShouldBeOrdered()
    {
        // Assert
        MigrationConstants.RowCountThresholds.Warning.Should().BeLessThan(MigrationConstants.RowCountThresholds.HighPriority);
        MigrationConstants.RowCountThresholds.HighPriority.Should().BeLessThan(MigrationConstants.RowCountThresholds.Critical);
        MigrationConstants.RowCountThresholds.Warning.Should().Be(1_000_000);
        MigrationConstants.RowCountThresholds.HighPriority.Should().Be(10_000_000);
        MigrationConstants.RowCountThresholds.Critical.Should().Be(100_000_000);
    }

    // ────────────────────────────────────────────────────────────────
    // SqlDatabaseProject and SqlProjectMetadata
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlDatabaseProject_TotalFileCount_ShouldIncludeProjFile()
    {
        // Arrange
        var project = new SqlDatabaseProject();

        // Act – empty project should report 1 (the .sqlproj file)
        var count = project.TotalFileCount;

        // Assert
        count.Should().Be(1);
    }

    [Fact]
    public void SqlDatabaseProject_TotalFileCount_ShouldSumAllScripts()
    {
        // Arrange
        var project = new SqlDatabaseProject();
        project.TableScripts.Add("/Tables/Users.sql");
        project.TableScripts.Add("/Tables/Orders.sql");
        project.IndexScripts.Add("/Indexes/Indexes.sql");
        project.StoredProcedureScripts.Add("/SP/FlattenProc.sql");
        project.DeploymentScripts.Add("/Scripts/PostDeploy.sql");
        project.DataMigrationScripts.Add("/Scripts/DataMig.sql");

        // Act
        var count = project.TotalFileCount;

        // Assert – 2+1+1+1+1+1(.sqlproj) = 7
        count.Should().Be(7);
    }

    [Fact]
    public void SqlDatabaseProject_GetProjectSummary_ShouldMentionProjectName()
    {
        // Arrange
        var project = new SqlDatabaseProject { ProjectName = "MyMigration" };

        // Act
        var summary = project.GetProjectSummary();

        // Assert
        summary.Should().Contain("MyMigration");
    }

    [Fact]
    public void SqlProjectMetadata_DefaultValues_ShouldBeValid()
    {
        // Act
        var metadata = new SqlProjectMetadata();

        // Assert
        metadata.GeneratorVersion.Should().Be("1.0.0");
        metadata.TargetSqlVersion.Should().Be("Azure SQL Database");
        metadata.ComplexityLevel.Should().Be("Medium");
        metadata.GenerationWarnings.Should().NotBeNull();
        metadata.ManualInterventionRequired.Should().NotBeNull();
        metadata.DeploymentOptions.Should().NotBeNull();
    }

    [Fact]
    public void DeploymentOptions_DefaultValues_ShouldBeValid()
    {
        // Act
        var options = new DeploymentOptions();

        // Assert
        options.DropObjectsNotInSource.Should().BeFalse();
        options.BackupDatabaseBeforeChanges.Should().BeTrue();
        options.BlockOnPossibleDataLoss.Should().BeTrue();
        options.IgnoreWhitespace.Should().BeTrue();
        options.IgnoreColumnCollation.Should().BeFalse();
        options.VerifyDeployment.Should().BeTrue();
        options.CommandTimeout.Should().Be(60);
        options.IncludeCompositeObjects.Should().BeTrue();
    }
}
