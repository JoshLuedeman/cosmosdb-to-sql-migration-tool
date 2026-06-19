using CosmosToSqlAssessment.Services;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class WorkloadSimilarityTests
{
    private static WorkloadProfile Profile(
        MigrationComplexityRating complexity,
        WorkloadSizeBucket size,
        int containers,
        int maxRu) => new()
    {
        ComplexityRating = complexity,
        SizeBucket = size,
        ContainerCount = containers,
        MaxProvisionedRUs = maxRu
    };

    [Fact]
    public void Score_IdenticalProfiles_IsOne()
    {
        var p = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);

        WorkloadSimilarity.Score(p, p).Should().Be(1.0);
    }

    [Fact]
    public void Score_SameComplexityAndSize_ExceedsThresholdRegardlessOfCounts()
    {
        var a = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var b = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 1, 1);

        // 0.35 (complexity) + 0.35 (size) alone clear the 0.6 threshold.
        WorkloadSimilarity.Score(a, b).Should().BeGreaterThanOrEqualTo(0.6);
        WorkloadSimilarity.AreComparable(a, b).Should().BeTrue();
    }

    [Fact]
    public void Score_DifferentComplexityAndSize_IsNotComparable()
    {
        var a = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var b = Profile(MigrationComplexityRating.Low, WorkloadSizeBucket.Small, 1, 50);

        WorkloadSimilarity.AreComparable(a, b).Should().BeFalse();
    }

    [Fact]
    public void Score_AdjacentComplexity_StaysComparable()
    {
        var a = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var b = Profile(MigrationComplexityRating.Medium, WorkloadSizeBucket.Large, 10, 1000);

        WorkloadSimilarity.AreComparable(a, b).Should().BeTrue();
    }

    [Fact]
    public void Score_UnknownComplexity_IsTreatedAsNeutral()
    {
        var a = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var b = Profile(MigrationComplexityRating.Unknown, WorkloadSizeBucket.Large, 10, 1000);

        // 0.35*0.5 (neutral complexity) + 0.35 (size) + 0.15 + 0.15 = 0.825
        WorkloadSimilarity.Score(a, b).Should().BeApproximately(0.825, 1e-9);
    }

    [Fact]
    public void Score_ZeroProvisionedRus_TreatedAsUnknownNotPenalized()
    {
        var a = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var withRu = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);
        var zeroRu = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 0);

        var fullScore = WorkloadSimilarity.Score(a, withRu);
        var zeroScore = WorkloadSimilarity.Score(a, zeroRu);

        // Unknown RU costs only half of the throughput weight (0.15 * 0.5 = 0.075), not all of it.
        fullScore.Should().Be(1.0);
        zeroScore.Should().BeApproximately(0.925, 1e-9);
    }

    [Fact]
    public void Score_NullProfile_Throws()
    {
        var p = Profile(MigrationComplexityRating.High, WorkloadSizeBucket.Large, 10, 1000);

        var act = () => WorkloadSimilarity.Score(p, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
