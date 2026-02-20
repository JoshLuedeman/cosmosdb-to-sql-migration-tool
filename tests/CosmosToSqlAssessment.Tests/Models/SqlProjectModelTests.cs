using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Models;

/// <summary>
/// Tests for SQL project model classes – verifies default values and collection initialization
/// </summary>
public class SqlProjectModelTests : TestBase
{
    // ────────────────────────────────────────────────────────────────
    // SqlProjectOptions
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlProjectOptions_DefaultValues_ShouldBeValid()
    {
        // Act
        var options = new SqlProjectOptions();

        // Assert
        options.ProjectName.Should().BeEmpty();
        options.OutputPath.Should().BeEmpty();
        options.GenerateDeploymentArtifacts.Should().BeTrue();
        options.GenerateDocumentation.Should().BeTrue();
        options.GenerateAzureDevOpsPipeline.Should().BeTrue();
        options.GeneratePowerShellDeployment.Should().BeTrue();
        options.OverwriteExistingFiles.Should().BeFalse();
        options.TargetCompatibilityLevel.Should().Be("150");
        options.IncludeSampleData.Should().BeFalse();
        options.CustomOptions.Should().NotBeNull();
        options.CustomOptions.Should().BeEmpty();
    }

    [Fact]
    public void SqlProjectOptions_CreateDefault_ShouldReturnExpectedDefaults()
    {
        // Act
        var options = SqlProjectOptions.CreateDefault();

        // Assert
        options.GenerateDeploymentArtifacts.Should().BeTrue();
        options.GenerateDocumentation.Should().BeTrue();
        options.GenerateAzureDevOpsPipeline.Should().BeTrue();
        options.GeneratePowerShellDeployment.Should().BeTrue();
        options.OverwriteExistingFiles.Should().BeFalse();
        options.TargetCompatibilityLevel.Should().Be("150");
    }

    [Fact]
    public void SqlProjectOptions_CreateMinimal_ShouldReturnMinimalDefaults()
    {
        // Act
        var options = SqlProjectOptions.CreateMinimal();

        // Assert
        options.GenerateDeploymentArtifacts.Should().BeFalse();
        options.GenerateDocumentation.Should().BeFalse();
        options.GenerateAzureDevOpsPipeline.Should().BeFalse();
        options.GeneratePowerShellDeployment.Should().BeFalse();
        options.OverwriteExistingFiles.Should().BeFalse();
        options.TargetCompatibilityLevel.Should().Be("150");
    }

    [Fact]
    public void SqlProjectOptions_CanSetAllProperties()
    {
        // Act
        var options = new SqlProjectOptions
        {
            ProjectName = "MyProject",
            OutputPath = "/output/path",
            GenerateDeploymentArtifacts = false,
            GenerateDocumentation = false,
            GenerateAzureDevOpsPipeline = true,
            GeneratePowerShellDeployment = true,
            OverwriteExistingFiles = true,
            TargetCompatibilityLevel = "160",
            IncludeSampleData = true
        };
        options.CustomOptions["key"] = "value";

        // Assert
        options.ProjectName.Should().Be("MyProject");
        options.OutputPath.Should().Be("/output/path");
        options.GenerateDeploymentArtifacts.Should().BeFalse();
        options.GenerateDocumentation.Should().BeFalse();
        options.GenerateAzureDevOpsPipeline.Should().BeTrue();
        options.GeneratePowerShellDeployment.Should().BeTrue();
        options.OverwriteExistingFiles.Should().BeTrue();
        options.TargetCompatibilityLevel.Should().Be("160");
        options.IncludeSampleData.Should().BeTrue();
        options.CustomOptions.Should().ContainKey("key");
    }

    // ────────────────────────────────────────────────────────────────
    // SqlProjectGenerationResult
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlProjectGenerationResult_DefaultValues_ShouldBeValid()
    {
        // Act
        var result = new SqlProjectGenerationResult();

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().BeEmpty();
        result.Project.Should().BeNull();
        result.Options.Should().NotBeNull();
        result.Warnings.Should().NotBeNull();
        result.Warnings.Should().BeEmpty();
        result.Messages.Should().NotBeNull();
        result.Messages.Should().BeEmpty();
        result.Stats.Should().NotBeNull();
    }

    [Fact]
    public void SqlProjectGenerationResult_GetSummary_WhenFailed_ShouldIncludeError()
    {
        // Arrange
        var result = new SqlProjectGenerationResult
        {
            Success = false,
            Error = "Connection timeout"
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        summary.Should().Contain("failed");
        summary.Should().Contain("Connection timeout");
    }

    [Fact]
    public void SqlProjectGenerationResult_GetSummary_WhenSucceeded_ShouldIncludeDetails()
    {
        // Arrange
        var project = new SqlDatabaseProject
        {
            ProjectName = "TestProject",
            OutputPath = "/output",
            ProjectFilePath = "/output/TestProject.sqlproj"
        };
        var result = new SqlProjectGenerationResult
        {
            Success = true,
            Project = project,
            Duration = TimeSpan.FromSeconds(5)
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        summary.Should().Contain("TestProject");
        summary.Should().Contain("successfully");
    }

    // ────────────────────────────────────────────────────────────────
    // SqlProjectGenerationStats
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlProjectGenerationStats_DefaultValues_ShouldBeZero()
    {
        // Act
        var stats = new SqlProjectGenerationStats();

        // Assert
        stats.TablesGenerated.Should().Be(0);
        stats.IndexesGenerated.Should().Be(0);
        stats.StoredProceduresGenerated.Should().Be(0);
        stats.DeploymentScriptsGenerated.Should().Be(0);
        stats.TotalFilesGenerated.Should().Be(0);
        stats.TotalFileSizeBytes.Should().Be(0);
        stats.TransformationRulesProcessed.Should().Be(0);
        stats.ComplexTransformations.Should().Be(0);
    }

    [Fact]
    public void SqlProjectGenerationStats_GetFormattedFileSize_ForBytes_ShouldReturnBytes()
    {
        // Arrange
        var stats = new SqlProjectGenerationStats { TotalFileSizeBytes = 512 };

        // Act
        var formatted = stats.GetFormattedFileSize();

        // Assert
        formatted.Should().Contain("bytes");
        formatted.Should().Contain("512");
    }

    [Fact]
    public void SqlProjectGenerationStats_GetFormattedFileSize_ForKilobytes_ShouldReturnKB()
    {
        // Arrange
        var stats = new SqlProjectGenerationStats { TotalFileSizeBytes = 2048 }; // 2 KB

        // Act
        var formatted = stats.GetFormattedFileSize();

        // Assert
        formatted.Should().Contain("KB");
    }

    [Fact]
    public void SqlProjectGenerationStats_GetFormattedFileSize_ForMegabytes_ShouldReturnMB()
    {
        // Arrange
        var stats = new SqlProjectGenerationStats { TotalFileSizeBytes = 2 * 1024 * 1024 }; // 2 MB

        // Act
        var formatted = stats.GetFormattedFileSize();

        // Assert
        formatted.Should().Contain("MB");
    }

    [Fact]
    public void SqlProjectGenerationStats_CanSetAllProperties()
    {
        // Act
        var stats = new SqlProjectGenerationStats
        {
            TablesGenerated = 5,
            IndexesGenerated = 10,
            StoredProceduresGenerated = 3,
            DeploymentScriptsGenerated = 2,
            TotalFilesGenerated = 20,
            TotalFileSizeBytes = 1024 * 1024,
            TransformationRulesProcessed = 8,
            ComplexTransformations = 3
        };

        // Assert
        stats.TablesGenerated.Should().Be(5);
        stats.IndexesGenerated.Should().Be(10);
        stats.StoredProceduresGenerated.Should().Be(3);
        stats.DeploymentScriptsGenerated.Should().Be(2);
        stats.TotalFilesGenerated.Should().Be(20);
        stats.TotalFileSizeBytes.Should().Be(1024 * 1024);
        stats.TransformationRulesProcessed.Should().Be(8);
        stats.ComplexTransformations.Should().Be(3);
    }
}
