using System.Collections.Generic;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class ActivityPolicyBuilderTests
{
    [Fact]
    public void ForCopyActivity_NullRetry_DefaultsToZeroForInsert()
    {
        var policy = new CopyActivityPolicy();

        var emitted = ActivityPolicyBuilder.ForCopyActivity(policy, SinkWriteBehavior.Insert);

        emitted["retry"].Should().Be(0);
        emitted["retryIntervalInSeconds"].Should().Be(30);
        emitted["timeout"].Should().Be("12:00:00");
        emitted["secureInput"].Should().Be(false);
        emitted["secureOutput"].Should().Be(false);
    }

    [Fact]
    public void ForCopyActivity_NullRetry_DefaultsToThreeForUpsert()
    {
        var policy = new CopyActivityPolicy();

        var emitted = ActivityPolicyBuilder.ForCopyActivity(policy, SinkWriteBehavior.Upsert);

        emitted["retry"].Should().Be(3);
    }

    [Fact]
    public void ForCopyActivity_ExplicitRetry_TakesPrecedenceOverWriteBehaviorDerivation()
    {
        var policy = new CopyActivityPolicy { Retry = 7, Timeout = "01:30:00", RetryIntervalInSeconds = 120 };

        var emitted = ActivityPolicyBuilder.ForCopyActivity(policy, SinkWriteBehavior.Insert);

        emitted["retry"].Should().Be(7);
        emitted["timeout"].Should().Be("01:30:00");
        emitted["retryIntervalInSeconds"].Should().Be(120);
    }

    [Fact]
    public void ForCopyActivity_NegativeRetry_Throws()
    {
        var policy = new CopyActivityPolicy { Retry = -1 };

        var act = () => ActivityPolicyBuilder.ForCopyActivity(policy, SinkWriteBehavior.Insert);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ForExecutePipeline_AppliesDefaults()
    {
        var emitted = ActivityPolicyBuilder.ForExecutePipeline(new ExecutePipelinePolicy());

        emitted["timeout"].Should().Be("1.00:00:00");
        emitted["retry"].Should().Be(0);
    }

    [Fact]
    public void ForWebActivity_DefaultsAreSecure()
    {
        var emitted = ActivityPolicyBuilder.ForWebActivity();

        emitted["secureInput"].Should().Be(true);
        emitted["secureOutput"].Should().Be(true);
    }
}
