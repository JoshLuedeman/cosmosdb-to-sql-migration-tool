using System.Collections.Generic;
using CosmosToSqlAssessment.Models.DataFactory;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class FailureNotificationBuilderTests
{
    private static PipelineActivity FakeUpstream(string name = "Copy_users_to_dbo_Users") => new()
    {
        Name = name,
        Type = "Copy",
        TypeProperties = new Dictionary<string, object?>(),
    };

    [Fact]
    public void Build_EmitsWebAndFailPair_NamedAfterUpstream()
    {
        var registry = new AdfNameRegistry();
        var builder = new FailureNotificationBuilder();
        var upstream = FakeUpstream();

        var pair = builder.Build(upstream, "perCopy", registry);

        pair.Web.Name.Should().StartWith("Notify_Copy_users_to_dbo_Users");
        pair.Web.Type.Should().Be("WebActivity");
        pair.Fail.Name.Should().StartWith("Fail_Copy_users_to_dbo_Users");
        pair.Fail.Type.Should().Be("Fail");
    }

    [Fact]
    public void Build_WebActivity_PostsToWebhookParameter_WithJsonHeadersAndTimeout()
    {
        var registry = new AdfNameRegistry();
        var pair = new FailureNotificationBuilder().Build(FakeUpstream(), "perCopy", registry);

        pair.Web.TypeProperties["method"].Should().Be("POST");
        pair.Web.TypeProperties["url"].Should().BeOfType<string>()
            .Which.Should().Contain($"@pipeline().parameters.{ParameterCatalog.PipelineParamFailureNotificationWebhookUrl}");
        var headers = (IDictionary<string, object?>)pair.Web.TypeProperties["headers"]!;
        headers["Content-Type"].Should().Be("application/json");
        pair.Web.TypeProperties["httpRequestTimeout"].Should().Be("00:01:00");
    }

    [Fact]
    public void Build_WebActivity_HasSecureInputOutput_ToHideWebhookUrlFromRunHistory()
    {
        var registry = new AdfNameRegistry();
        var pair = new FailureNotificationBuilder().Build(FakeUpstream(), "perCopy", registry);

        var policy = (IDictionary<string, object?>)pair.Web.AdditionalProperties!["policy"]!;
        policy["secureInput"].Should().Be(true);
        policy["secureOutput"].Should().Be(true);
    }

    [Fact]
    public void Build_WebActivity_DependsOnUpstream_WithFailedCondition()
    {
        var registry = new AdfNameRegistry();
        var pair = new FailureNotificationBuilder().Build(FakeUpstream("CopyX"), "perCopy", registry);

        var dependsOn = (Dictionary<string, object?>[])pair.Web.AdditionalProperties!["dependsOn"]!;
        dependsOn.Should().ContainSingle();
        dependsOn[0]["activity"].Should().Be("CopyX");
        ((string[])dependsOn[0]["dependencyConditions"]!).Should().ContainSingle().Which.Should().Be("Failed");
    }

    [Fact]
    public void Build_FailActivity_DependsOnWebActivity_WithCompletedCondition_ToReThrow()
    {
        var registry = new AdfNameRegistry();
        var pair = new FailureNotificationBuilder().Build(FakeUpstream(), "perCopy", registry);

        var dependsOn = (Dictionary<string, object?>[])pair.Fail.AdditionalProperties!["dependsOn"]!;
        dependsOn.Should().ContainSingle();
        dependsOn[0]["activity"].Should().Be(pair.Web.Name);
        ((string[])dependsOn[0]["dependencyConditions"]!).Should().ContainSingle().Which.Should().Be("Completed");
    }

    [Fact]
    public void Build_FailActivity_MessageReferencesUpstreamErrorMessage()
    {
        var registry = new AdfNameRegistry();
        var pair = new FailureNotificationBuilder().Build(FakeUpstream("CopyY"), "perCopy", registry);

        var message = (IDictionary<string, object?>)pair.Fail.TypeProperties["message"]!;
        message["type"].Should().Be("Expression");
        message["value"].Should().BeOfType<string>()
            .Which.Should().Contain("activity('CopyY').error.message");
    }
}
