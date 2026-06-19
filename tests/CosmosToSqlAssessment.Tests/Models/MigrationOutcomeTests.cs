using System.Reflection;
using System.Text.Json;
using CosmosToSqlAssessment.Tests.Infrastructure;

namespace CosmosToSqlAssessment.Tests.Models;

/// <summary>
/// Tests for the anonymized <see cref="MigrationOutcome"/> / <see cref="WorkloadProfile"/>
/// feedback schema (parent #132, sub-issue #218).
/// </summary>
public class MigrationOutcomeTests
{
    [Fact]
    public void MigrationOutcome_Defaults_AreSafeAndVersioned()
    {
        var outcome = new MigrationOutcome();

        outcome.SchemaVersion.Should().Be(MigrationOutcome.CurrentSchemaVersion);
        outcome.OutcomeId.Should().NotBeNullOrEmpty();
        outcome.OutcomeId.Should().HaveLength(32, "the id is a non-correlatable Guid 'N' format");
        outcome.Profile.Should().NotBeNull();
        outcome.Status.Should().Be(MigrationOutcomeStatus.Unknown);
        outcome.RecordedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void OutcomeId_IsUniquePerInstance()
    {
        var a = new MigrationOutcome();
        var b = new MigrationOutcome();

        a.OutcomeId.Should().NotBe(b.OutcomeId);
    }

    [Fact]
    public void MonthlyCostVariance_ComputesSignedDifference()
    {
        var outcome = new MigrationOutcome
        {
            EstimatedMonthlyCostUsd = 100m,
            ActualMonthlyCostUsd = 130m
        };

        outcome.MonthlyCostVarianceUsd.Should().Be(30m);
        outcome.MonthlyCostVariancePercent.Should().BeApproximately(30.0, 0.0001);
    }

    [Fact]
    public void MonthlyCostVariancePercent_IsNull_WhenEstimateIsZero()
    {
        var outcome = new MigrationOutcome
        {
            EstimatedMonthlyCostUsd = 0m,
            ActualMonthlyCostUsd = 50m
        };

        outcome.MonthlyCostVariancePercent.Should().BeNull();
        outcome.MonthlyCostVarianceUsd.Should().Be(50m);
    }

    [Theory]
    [InlineData(MigrationOutcomeStatus.Succeeded, true)]
    [InlineData(MigrationOutcomeStatus.PartiallySucceeded, true)]
    [InlineData(MigrationOutcomeStatus.Failed, false)]
    [InlineData(MigrationOutcomeStatus.RolledBack, false)]
    [InlineData(MigrationOutcomeStatus.Unknown, false)]
    public void Succeeded_ReflectsStatus(MigrationOutcomeStatus status, bool expected)
    {
        new MigrationOutcome { Status = status }.Succeeded.Should().Be(expected);
    }

    [Theory]
    [InlineData(0.0, WorkloadSizeBucket.Small)]
    [InlineData(9.99, WorkloadSizeBucket.Small)]
    [InlineData(10.0, WorkloadSizeBucket.Medium)]
    [InlineData(99.99, WorkloadSizeBucket.Medium)]
    [InlineData(100.0, WorkloadSizeBucket.Large)]
    [InlineData(1023.0, WorkloadSizeBucket.Large)]
    [InlineData(1024.0, WorkloadSizeBucket.VeryLarge)]
    [InlineData(50000.0, WorkloadSizeBucket.VeryLarge)]
    public void BucketFor_MapsSizeBoundaries(double gb, WorkloadSizeBucket expected)
    {
        WorkloadProfile.BucketFor(gb).Should().Be(expected);
    }

    [Theory]
    [InlineData("Low", MigrationComplexityRating.Low)]
    [InlineData("medium", MigrationComplexityRating.Medium)]
    [InlineData("HIGH", MigrationComplexityRating.High)]
    [InlineData("  Medium  ", MigrationComplexityRating.Medium)]
    [InlineData("", MigrationComplexityRating.Unknown)]
    [InlineData(null, MigrationComplexityRating.Unknown)]
    [InlineData("nonsense", MigrationComplexityRating.Unknown)]
    public void ParseComplexity_NormalizesLabels(string? input, MigrationComplexityRating expected)
    {
        WorkloadProfile.ParseComplexity(input).Should().Be(expected);
    }

    [Fact]
    public void FromAssessment_ProjectsAggregateFingerprint()
    {
        var assessment = TestDataFactory.CreateSampleAssessmentResult();

        var profile = WorkloadProfile.FromAssessment(assessment);

        profile.ComplexityRating.Should().Be(MigrationComplexityRating.Medium);
        profile.ContainerCount.Should().Be(1);
        profile.TotalDocumentCount.Should().Be(500000);
        profile.TotalDataSizeGb.Should().BeApproximately(5_000_000_000 / (1024.0 * 1024.0 * 1024.0), 0.01);
        profile.SizeBucket.Should().Be(WorkloadSizeBucket.Small);
        profile.MaxProvisionedRUs.Should().Be(400);
        profile.IndexRecommendationCount.Should().Be(1);
        profile.RecommendedPlatform.Should().Be("Azure SQL Database");
        profile.RecommendedTier.Should().Be("General Purpose");
    }

    [Fact]
    public void FromAssessment_Null_Throws()
    {
        Action act = () => WorkloadProfile.FromAssessment(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromAssessment_HandlesEmptyAssessment()
    {
        var profile = WorkloadProfile.FromAssessment(new AssessmentResult());

        profile.ContainerCount.Should().Be(0);
        profile.MaxProvisionedRUs.Should().Be(0);
        profile.TotalDocumentCount.Should().Be(0);
        profile.ComplexityRating.Should().Be(MigrationComplexityRating.Unknown);
    }

    /// <summary>
    /// Privacy contract: the only string-typed (free-text-capable) properties allowed in the
    /// persisted feedback graph are a fixed allow-list of tool-generated categorical labels.
    /// Any newly added string property forces a conscious privacy decision (update this list),
    /// preventing accidental introduction of a name/notes/identifier field.
    /// </summary>
    [Fact]
    public void Schema_ExposesNoUnapprovedStringFields()
    {
        var allowed = new HashSet<string>
        {
            // MigrationOutcome
            "OutcomeId",
            "DeployedPlatform",
            "DeployedTier",
            // WorkloadProfile
            "RecommendedPlatform",
            "RecommendedTier"
        };

        var stringProps = CollectStringProperties(typeof(MigrationOutcome))
            .Concat(CollectStringProperties(typeof(WorkloadProfile)))
            .Select(p => p.Name)
            .Distinct()
            .ToList();

        stringProps.Should().OnlyContain(name => allowed.Contains(name),
            "the feedback schema must not introduce free-text / identifying string fields without an explicit privacy decision");
    }

    [Fact]
    public void Schema_DoesNotPersistDerivedProperties()
    {
        var outcome = new MigrationOutcome
        {
            EstimatedMonthlyCostUsd = 100m,
            ActualMonthlyCostUsd = 150m,
            Status = MigrationOutcomeStatus.Succeeded
        };

        var json = JsonSerializer.Serialize(outcome);

        json.Should().Contain("SchemaVersion");
        json.Should().NotContain("MonthlyCostVarianceUsd");
        json.Should().NotContain("MonthlyCostVariancePercent");
        json.Should().NotContain("Succeeded");
    }

    [Fact]
    public void Schema_RoundTripsThroughJson()
    {
        var original = new MigrationOutcome
        {
            Status = MigrationOutcomeStatus.PartiallySucceeded,
            ActualMigrationDays = 7,
            DataCompletenessPercent = 99.5,
            DeployedPlatform = "Azure SQL Managed Instance",
            DeployedTier = "Business Critical",
            AvgResourceUtilizationPercent = 62.0,
            PerformanceSatisfactory = true,
            EstimatedMonthlyCostUsd = 1000m,
            ActualMonthlyCostUsd = 1250m,
            Profile = new WorkloadProfile
            {
                ComplexityRating = MigrationComplexityRating.High,
                ContainerCount = 12,
                SizeBucket = WorkloadSizeBucket.Large,
                TotalDocumentCount = 42_000_000,
                TotalDataSizeGb = 250.0,
                MaxProvisionedRUs = 40000,
                IndexRecommendationCount = 9,
                RecommendedPlatform = "Azure SQL Managed Instance",
                RecommendedTier = "General Purpose"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<MigrationOutcome>(json)!;

        restored.Status.Should().Be(original.Status);
        restored.ActualMigrationDays.Should().Be(7);
        restored.DeployedTier.Should().Be("Business Critical");
        restored.PerformanceSatisfactory.Should().BeTrue();
        restored.Profile.ComplexityRating.Should().Be(MigrationComplexityRating.High);
        restored.Profile.SizeBucket.Should().Be(WorkloadSizeBucket.Large);
        restored.Profile.MaxProvisionedRUs.Should().Be(40000);
        restored.MonthlyCostVarianceUsd.Should().Be(250m);
    }

    private static IEnumerable<PropertyInfo> CollectStringProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string));
}
