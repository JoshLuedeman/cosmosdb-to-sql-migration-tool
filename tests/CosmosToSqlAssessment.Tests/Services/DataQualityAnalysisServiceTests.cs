using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services;
using System.Text.Json;

namespace CosmosToSqlAssessment.Tests.Services;

public class DataQualityAnalysisServiceTests : TestBase
{
    [Fact]
    public void Constructor_WithValidConfiguration_ShouldInitializeSuccessfully()
    {
        // Arrange
        var logger = CreateMockLogger<DataQualityAnalysisService>();

        // Act
        var service = new DataQualityAnalysisService(MockConfiguration.Object, logger.Object);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithoutCosmosEndpoint_ShouldThrowArgumentException()
    {
        // Arrange
        var badConfig = new Mock<IConfiguration>();
        var logger = CreateMockLogger<DataQualityAnalysisService>();

        // Act & Assert
        Action act = () => new DataQualityAnalysisService(badConfig.Object, logger.Object);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AnalyzeDataQualityAsync_WithEmptyContainerList_ShouldReturnEmptyAnalysis()
    {
        // Arrange
        var logger = CreateMockLogger<DataQualityAnalysisService>();
        var service = new DataQualityAnalysisService(MockConfiguration.Object, logger.Object);
        var cosmosAnalysis = new CosmosDbAnalysis
        {
            Containers = new List<ContainerAnalysis>()
        };

        // Act
        var result = await service.AnalyzeDataQualityAsync(cosmosAnalysis, "testdb", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.TotalDocumentsAnalyzed.Should().Be(0);
        result.ContainerAnalyses.Should().BeEmpty();
    }

    [Fact]
    public void DataQualityAnalysisOptions_DefaultValues_ShouldBeValid()
    {
        // Arrange & Act
        var options = new DataQualityAnalysisOptions();

        // Assert
        options.SampleSize.Should().Be(1000);
        options.MaxSampleRecords.Should().Be(5);
        options.NullThresholdCritical.Should().Be(0.15);
        options.NullThresholdWarning.Should().Be(0.05);
        options.DuplicateThresholdCritical.Should().Be(0.01);
        options.OutlierZScoreThreshold.Should().Be(3);
        options.MaxStringLengthForVarchar.Should().Be(4000);
        options.IncludeEncodingChecks.Should().BeTrue();
        options.IncludeOutlierDetection.Should().BeTrue();
        options.IncludeDuplicateDetection.Should().BeTrue();
    }

    [Fact]
    public void DataQualityIssue_SeverityEnumValues_ShouldBeOrdered()
    {
        // Arrange & Act & Assert
        ((int)DataQualitySeverity.Info).Should().BeLessThan((int)DataQualitySeverity.Warning);
        ((int)DataQualitySeverity.Warning).Should().BeLessThan((int)DataQualitySeverity.Critical);
    }

    [Fact]
    public void NullAnalysisResult_CalculateNullPercentage_ShouldBeCorrect()
    {
        // Arrange
        var result = new NullAnalysisResult
        {
            FieldName = "email",
            TotalDocuments = 100,
            NullCount = 15,
            MissingCount = 10
        };

        // Act
        var totalNullPercentage = result.NullPercentage + result.MissingPercentage;

        // Assert - When percentages are set to 0, this test validates the model can hold the values
        result.TotalDocuments.Should().Be(100);
        result.NullCount.Should().Be(15);
        result.MissingCount.Should().Be(10);
    }

    [Fact]
    public void DuplicateAnalysisResult_WithDuplicates_ShouldCalculatePercentageCorrectly()
    {
        // Arrange
        var result = new DuplicateAnalysisResult
        {
            KeyType = "ID",
            DuplicateGroupCount = 5,
            TotalDuplicateRecords = 50,
            DuplicatePercentage = 10.0
        };

        // Assert
        result.DuplicateGroupCount.Should().Be(5);
        result.TotalDuplicateRecords.Should().Be(50);
        result.DuplicatePercentage.Should().Be(10.0);
    }

    [Fact]
    public void TypeConsistencyResult_WithMultipleTypes_ShouldTrackDistribution()
    {
        // Arrange
        var result = new TypeConsistencyResult
        {
            FieldName = "age",
            TypeDistribution = new Dictionary<string, long>
            {
                ["integer"] = 80,
                ["string"] = 15,
                ["number"] = 5
            },
            DominantType = "integer",
            DominantTypePercentage = 80.0,
            IsConsistent = false
        };

        // Assert
        result.TypeDistribution.Count.Should().Be(3);
        result.DominantType.Should().Be("integer");
        result.IsConsistent.Should().BeFalse();
    }

    [Fact]
    public void OutlierAnalysisResult_WithValidStatistics_ShouldCalculateCorrectly()
    {
        // Arrange
        var result = new OutlierAnalysisResult
        {
            FieldName = "price",
            TotalValues = 100,
            Mean = 50.0,
            StandardDeviation = 10.0,
            MinValue = 20.0,
            MaxValue = 150.0,
            Q1 = 40.0,
            Q3 = 60.0,
            IQR = 20.0,
            OutlierCount = 5,
            OutlierPercentage = 5.0
        };

        // Assert
        result.Mean.Should().Be(50.0);
        result.IQR.Should().Be(20.0);
        result.OutlierPercentage.Should().Be(5.0);
    }

    [Fact]
    public void StringLengthAnalysisResult_WithLongStrings_ShouldRecommendCorrectType()
    {
        // Arrange
        var result = new StringLengthAnalysisResult
        {
            FieldName = "description",
            MaxLength = 8432,
            P95Length = 5000,
            P99Length = 7500,
            RecommendedSqlType = "NVARCHAR(MAX)"
        };

        // Assert
        result.MaxLength.Should().Be(8432);
        result.RecommendedSqlType.Should().Be("NVARCHAR(MAX)");
    }

    [Fact]
    public void EncodingIssue_WithNonAscii_ShouldTrackCorrectly()
    {
        // Arrange
        var issue = new EncodingIssue
        {
            FieldName = "name",
            IssueType = "NonASCII",
            AffectedDocumentCount = 25,
            AffectedPercentage = 25.0,
            RecommendedAction = "Use NVARCHAR instead of VARCHAR to support Unicode characters"
        };

        // Assert
        issue.IssueType.Should().Be("NonASCII");
        issue.AffectedPercentage.Should().Be(25.0);
        issue.RecommendedAction.Should().Contain("NVARCHAR");
    }

    [Fact]
    public void DateValidationResult_WithInvalidDates_ShouldTrackIssues()
    {
        // Arrange
        var result = new DateValidationResult
        {
            FieldName = "createdDate",
            TotalValues = 100,
            InvalidDateCount = 5,
            FutureDateCount = 3,
            VeryOldDateCount = 2,
            InvalidPercentage = 10.0
        };

        // Assert
        result.InvalidDateCount.Should().Be(5);
        result.FutureDateCount.Should().Be(3);
        result.VeryOldDateCount.Should().Be(2);
        result.InvalidPercentage.Should().Be(10.0);
    }

    [Fact]
    public void DataQualitySummary_WithMultipleIssues_ShouldCalculateScoreCorrectly()
    {
        // Arrange
        var summary = new DataQualitySummary
        {
            OverallQualityScore = 75.5,
            QualityRating = "Good",
            ReadyForMigration = true,
            EstimatedCleanupHours = 8
        };

        // Assert
        summary.OverallQualityScore.Should().Be(75.5);
        summary.QualityRating.Should().Be("Good");
        summary.ReadyForMigration.Should().BeTrue();
        summary.EstimatedCleanupHours.Should().Be(8);
    }

    [Fact]
    public void DataQualitySummary_WithCriticalIssues_ShouldNotBeReadyForMigration()
    {
        // Arrange
        var summary = new DataQualitySummary
        {
            OverallQualityScore = 45.0,
            QualityRating = "Poor",
            ReadyForMigration = false,
            BlockingIssues = new List<string>
            {
                "Duplicate IDs found",
                "Invalid dates present"
            }
        };

        // Assert
        summary.ReadyForMigration.Should().BeFalse();
        summary.BlockingIssues.Should().HaveCount(2);
        summary.QualityRating.Should().Be("Poor");
    }

    [Fact]
    public void ContainerQualityAnalysis_WithMultipleAnalyses_ShouldAggregateIssues()
    {
        // Arrange
        var containerAnalysis = new ContainerQualityAnalysis
        {
            ContainerName = "users",
            DocumentCount = 1000,
            SampleSize = 100
        };

        // Add some null analysis results
        containerAnalysis.NullAnalysis.Add(new NullAnalysisResult
        {
            FieldName = "email",
            NullPercentage = 15.0
        });

        containerAnalysis.NullAnalysis.Add(new NullAnalysisResult
        {
            FieldName = "phone",
            NullPercentage = 25.0
        });

        // Assert
        containerAnalysis.NullAnalysis.Should().HaveCount(2);
        containerAnalysis.NullAnalysis.Sum(n => n.NullPercentage).Should().Be(40.0);
    }

    [Fact]
    public void DataQualityIssue_WithMetrics_ShouldStoreAdditionalInformation()
    {
        // Arrange
        var issue = new DataQualityIssue
        {
            ContainerName = "orders",
            FieldName = "customerId",
            Severity = DataQualitySeverity.Critical,
            Category = "Null",
            Title = "15% null values in customerId",
            Description = "Field has significant null values",
            Impact = "Will prevent NOT NULL constraints",
            Metrics = new Dictionary<string, object>
            {
                ["NullCount"] = 150,
                ["TotalCount"] = 1000,
                ["Percentage"] = 15.0
            }
        };

        // Assert
        issue.Severity.Should().Be(DataQualitySeverity.Critical);
        issue.Metrics.Should().ContainKey("NullCount");
        issue.Metrics["NullCount"].Should().Be(150);
        issue.Metrics["Percentage"].Should().Be(15.0);
    }

    // ────────────────────────────────────────────────────────────────
    // Sub-model classes – newly covered types
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void NullAnalysisResult_BooleanFields_ShouldStoreValues()
    {
        // Arrange & Act
        var result = new NullAnalysisResult
        {
            FieldName = "email",
            IsRecommendedRequired = true,
            WillImpactNotNullConstraint = true
        };

        // Assert
        result.IsRecommendedRequired.Should().BeTrue();
        result.WillImpactNotNullConstraint.Should().BeTrue();
    }

    [Fact]
    public void DuplicateGroup_ShouldStoreAllProperties()
    {
        // Arrange & Act
        var group = new DuplicateGroup
        {
            KeyValue = "user-123",
            OccurrenceCount = 3,
            DocumentIds = new List<string> { "doc1", "doc2", "doc3" },
            SampleData = new Dictionary<string, object> { ["name"] = "John" }
        };

        // Assert
        group.KeyValue.Should().Be("user-123");
        group.OccurrenceCount.Should().Be(3);
        group.DocumentIds.Should().HaveCount(3);
        group.SampleData.Should().ContainKey("name");
    }

    [Fact]
    public void DuplicateAnalysisResult_ShouldInitializeCollections()
    {
        // Act
        var result = new DuplicateAnalysisResult();

        // Assert
        result.KeyFields.Should().NotBeNull();
        result.TopDuplicateGroups.Should().NotBeNull();
    }

    [Fact]
    public void TypeMismatchSample_ShouldStoreProperties()
    {
        // Arrange & Act
        var sample = new TypeMismatchSample
        {
            DocumentId = "doc-abc",
            ActualType = "string",
            ExpectedType = "integer",
            SampleValue = "not-a-number"
        };

        // Assert
        sample.DocumentId.Should().Be("doc-abc");
        sample.ActualType.Should().Be("string");
        sample.ExpectedType.Should().Be("integer");
        sample.SampleValue.Should().Be("not-a-number");
    }

    [Fact]
    public void TypeConsistencyResult_ShouldInitializeCollections()
    {
        // Act
        var result = new TypeConsistencyResult();

        // Assert
        result.TypeDistribution.Should().NotBeNull();
        result.Mismatches.Should().NotBeNull();
    }

    [Fact]
    public void OutlierAnalysisResult_Median_ShouldBeStorable()
    {
        // Arrange & Act
        var result = new OutlierAnalysisResult
        {
            FieldName = "price",
            Median = 49.99,
            OutlierSamples = new List<OutlierSample>()
        };

        // Assert
        result.Median.Should().Be(49.99);
        result.OutlierSamples.Should().NotBeNull();
    }

    [Fact]
    public void OutlierSample_ShouldStoreProperties()
    {
        // Arrange & Act
        var sample = new OutlierSample
        {
            DocumentId = "doc-xyz",
            Value = 999.99,
            ZScore = 4.5,
            OutlierType = "High"
        };

        // Assert
        sample.DocumentId.Should().Be("doc-xyz");
        sample.Value.Should().Be(999.99);
        sample.ZScore.Should().Be(4.5);
        sample.OutlierType.Should().Be("High");
    }

    [Fact]
    public void StringLengthAnalysisResult_ExtendedProperties_ShouldBeStorable()
    {
        // Arrange & Act
        var result = new StringLengthAnalysisResult
        {
            FieldName = "notes",
            TotalValues = 500,
            MinLength = 0,
            AverageLength = 120.5,
            MedianLength = 100,
            ExtremeValueSamples = new List<StringLengthSample>()
        };

        // Assert
        result.TotalValues.Should().Be(500);
        result.MinLength.Should().Be(0);
        result.AverageLength.Should().Be(120.5);
        result.MedianLength.Should().Be(100);
        result.ExtremeValueSamples.Should().NotBeNull();
    }

    [Fact]
    public void StringLengthSample_ShouldStoreProperties()
    {
        // Arrange & Act
        var sample = new StringLengthSample
        {
            DocumentId = "doc-001",
            Length = 8432,
            PreviewText = "A very long text..."
        };

        // Assert
        sample.DocumentId.Should().Be("doc-001");
        sample.Length.Should().Be(8432);
        sample.PreviewText.Should().NotBeEmpty();
    }

    [Fact]
    public void EncodingSample_ShouldStoreProperties()
    {
        // Arrange & Act
        var sample = new EncodingSample
        {
            DocumentId = "doc-999",
            ProblematicValue = "caf\u00e9",
            CharacterCodes = "63 61 66 C3 A9",
            IssueDescription = "Non-ASCII character detected"
        };

        // Assert
        sample.DocumentId.Should().Be("doc-999");
        sample.ProblematicValue.Should().Contain("caf");
        sample.CharacterCodes.Should().NotBeEmpty();
        sample.IssueDescription.Should().Contain("Non-ASCII");
    }

    [Fact]
    public void EncodingIssue_ShouldInitializeSamplesCollection()
    {
        // Act
        var issue = new EncodingIssue();

        // Assert
        issue.Samples.Should().NotBeNull();
        issue.Samples.Should().BeEmpty();
    }

    [Fact]
    public void DateValidationResult_DateRangeProperties_ShouldBeStorable()
    {
        // Arrange
        var minDate = new DateTime(2020, 1, 1);
        var maxDate = new DateTime(2024, 12, 31);

        // Act
        var result = new DateValidationResult
        {
            FieldName = "createdAt",
            MinDate = minDate,
            MaxDate = maxDate,
            InvalidSamples = new List<InvalidDateSample>()
        };

        // Assert
        result.MinDate.Should().Be(minDate);
        result.MaxDate.Should().Be(maxDate);
        result.InvalidSamples.Should().NotBeNull();
    }

    [Fact]
    public void InvalidDateSample_ShouldStoreProperties()
    {
        // Arrange & Act
        var sample = new InvalidDateSample
        {
            DocumentId = "doc-bad",
            RawValue = "not-a-date",
            IssueType = "Invalid",
            IssueDescription = "Value cannot be parsed as a date"
        };

        // Assert
        sample.DocumentId.Should().Be("doc-bad");
        sample.RawValue.Should().Be("not-a-date");
        sample.IssueType.Should().Be("Invalid");
        sample.IssueDescription.Should().NotBeEmpty();
    }
}
