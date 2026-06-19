using CosmosToSqlAssessment.Services.Feedback;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class CoarsenedOutcomeTests
{
    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "1")]
    [InlineData(2, "2-5")]
    [InlineData(5, "2-5")]
    [InlineData(6, "6-20")]
    [InlineData(20, "6-20")]
    [InlineData(21, "21-100")]
    [InlineData(100, "21-100")]
    [InlineData(101, "100+")]
    public void BucketContainerCount_MapsBoundaries(int count, string expected)
    {
        CoarsenedOutcome.BucketContainerCount(count).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(-25.0, "UnderEstimate")]
    [InlineData(-10.0, "OnTarget")]
    [InlineData(0.0, "OnTarget")]
    [InlineData(10.0, "OnTarget")]
    [InlineData(25.0, "OverEstimate")]
    [InlineData(50.0, "OverEstimate")]
    [InlineData(75.0, "WellOverEstimate")]
    public void BucketCostVariance_MapsBoundaries(double? variance, string expected)
    {
        CoarsenedOutcome.BucketCostVariance(variance).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "0-5")]
    [InlineData(5, "0-5")]
    [InlineData(6, "6-15")]
    [InlineData(15, "6-15")]
    [InlineData(16, "16-30")]
    [InlineData(30, "16-30")]
    [InlineData(31, "30+")]
    public void BucketDays_MapsBoundaries(int days, string expected)
    {
        CoarsenedOutcome.BucketDays(days).Should().Be(expected);
    }

    [Fact]
    public void From_ProjectsOnlyAnonymizedAggregateFields()
    {
        var outcome = new MigrationOutcome
        {
            Profile = new WorkloadProfile
            {
                ComplexityRating = MigrationComplexityRating.High,
                SizeBucket = WorkloadSizeBucket.Large,
                ContainerCount = 12,
                RecommendedPlatform = "Azure SQL Database",
                RecommendedTier = "General Purpose"
            },
            DeployedPlatform = "Azure SQL Managed Instance",
            DeployedTier = "Business Critical",
            Status = MigrationOutcomeStatus.Succeeded,
            PerformanceSatisfactory = true,
            EstimatedMonthlyCostUsd = 100m,
            ActualMonthlyCostUsd = 130m,
            ActualMigrationDays = 9
        };

        var coarsened = CoarsenedOutcome.From(outcome);

        coarsened.ComplexityRating.Should().Be(MigrationComplexityRating.High);
        coarsened.SizeBucket.Should().Be(WorkloadSizeBucket.Large);
        coarsened.ContainerCountBucket.Should().Be("6-20");
        coarsened.RecommendedPlatform.Should().Be("Azure SQL Database");
        coarsened.RecommendedTier.Should().Be("General Purpose");
        coarsened.DeployedPlatform.Should().Be("Azure SQL Managed Instance");
        coarsened.DeployedTier.Should().Be("Business Critical");
        coarsened.Status.Should().Be(MigrationOutcomeStatus.Succeeded);
        coarsened.PerformanceSatisfactory.Should().BeTrue();
        coarsened.MonthlyCostVarianceBucket.Should().Be("OverEstimate");
        coarsened.MigrationDaysBucket.Should().Be("6-15");
    }

    [Fact]
    public void From_NullOutcome_Throws()
    {
        var act = () => CoarsenedOutcome.From(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
