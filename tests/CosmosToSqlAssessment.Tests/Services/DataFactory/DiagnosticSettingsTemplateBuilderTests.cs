using System.Text.Json;
using CosmosToSqlAssessment.Services.DataFactory;

namespace CosmosToSqlAssessment.Tests.Services.DataFactory;

public class DiagnosticSettingsTemplateBuilderTests
{
    [Fact]
    public void Build_ReturnsFullDeployableArmTemplate()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());

        template["$schema"].Should().Be("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#");
        template["contentVersion"].Should().Be("1.0.0.0");
        template.Should().ContainKey("parameters");
        template.Should().ContainKey("resources");
    }

    [Fact]
    public void Build_ResourceUsesCorrectTypeAndApiVersion()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());

        var resource = (IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!;
        resource["type"].Should().Be(DiagnosticSettingsTemplateBuilder.ResourceType);
        resource["apiVersion"].Should().Be(DiagnosticSettingsTemplateBuilder.ApiVersion);
    }

    [Fact]
    public void Build_ScopeAndDependsOn_TargetTheFactoryResource()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());
        var resource = (IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!;

        var expected = $"[resourceId('Microsoft.DataFactory/factories', parameters('{ParameterCatalog.MonitoringParamDataFactoryName}'))]";
        resource["scope"].Should().Be(expected);
        var dependsOn = (List<object?>)resource["dependsOn"]!;
        dependsOn.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void Build_PropertiesSetDedicatedDestinationType_ForResourceSpecificTables()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());
        var props = (IDictionary<string, object?>)((IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!)["properties"]!;

        props["logAnalyticsDestinationType"].Should().Be("Dedicated");
        props["workspaceId"].Should().Be($"[parameters('{ParameterCatalog.MonitoringParamLogAnalyticsWorkspaceId}')]");
    }

    [Fact]
    public void Build_DefaultLogCategories_IncludePipelineActivityAndTriggerRuns()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());
        var props = (IDictionary<string, object?>)((IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!)["properties"]!;
        var logs = (List<object?>)props["logs"]!;

        logs.Cast<IDictionary<string, object?>>().Select(l => l["category"]).Should().BeEquivalentTo(
            new object?[] { "PipelineRuns", "ActivityRuns", "TriggerRuns" });
        logs.Cast<IDictionary<string, object?>>().Should().AllSatisfy(l => l["enabled"].Should().Be(true));
    }

    [Fact]
    public void Build_CustomLogCategories_AreEmittedInOrder()
    {
        var options = new MonitoringOptions
        {
            LogCategories = new[] { "PipelineRuns", "TriggerRuns" },
        };

        var template = new DiagnosticSettingsTemplateBuilder().Build(options);
        var props = (IDictionary<string, object?>)((IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!)["properties"]!;
        var logs = (List<object?>)props["logs"]!;

        logs.Cast<IDictionary<string, object?>>().Select(l => l["category"]).Should().BeEquivalentTo(
            new object?[] { "PipelineRuns", "TriggerRuns" },
            opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Build_UnknownLogCategory_Throws()
    {
        var options = new MonitoringOptions
        {
            LogCategories = new[] { "PipelineRuns", "BogusCategory" },
        };

        var act = () => new DiagnosticSettingsTemplateBuilder().Build(options);

        act.Should().Throw<ArgumentException>().WithMessage("*BogusCategory*");
    }

    [Fact]
    public void Build_EmitAllMetricsFalse_DropsMetricsArray()
    {
        var options = new MonitoringOptions { EmitAllMetrics = false };

        var template = new DiagnosticSettingsTemplateBuilder().Build(options);
        var props = (IDictionary<string, object?>)((IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!)["properties"]!;

        props.Should().NotContainKey("metrics");
    }

    [Fact]
    public void Build_EmitAllMetricsTrue_AddsAllMetricsCategory()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());
        var props = (IDictionary<string, object?>)((IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!)["properties"]!;
        var metrics = (List<object?>)props["metrics"]!;

        metrics.Should().ContainSingle();
        var entry = (IDictionary<string, object?>)metrics[0]!;
        entry["category"].Should().Be("AllMetrics");
        entry["enabled"].Should().Be(true);
    }

    [Fact]
    public void Build_DiagnosticSettingNameParameter_DefaultsToOptionsValue()
    {
        var options = new MonitoringOptions { DiagnosticSettingName = "custom-diag-name" };

        var template = new DiagnosticSettingsTemplateBuilder().Build(options);
        var parameters = (IDictionary<string, object?>)template["parameters"]!;
        var nameParam = (IDictionary<string, object?>)parameters[ParameterCatalog.MonitoringParamDiagnosticSettingName]!;

        nameParam["defaultValue"].Should().Be("custom-diag-name");
    }

    [Fact]
    public void Build_ResourceNameReferencesNameParameter()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());
        var resource = (IDictionary<string, object?>)((List<object?>)template["resources"]!)[0]!;

        resource["name"].Should().Be($"[parameters('{ParameterCatalog.MonitoringParamDiagnosticSettingName}')]");
    }

    [Fact]
    public void Build_SerializedTemplate_IsValidJson()
    {
        var template = new DiagnosticSettingsTemplateBuilder().Build(new MonitoringOptions());

        var json = AdfJsonSerializer.Serialize(template);

        // Round-trip parse: any non-JSON would throw JsonException here.
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("$schema").GetString().Should().Be("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#");
        doc.RootElement.GetProperty("resources")[0].GetProperty("type").GetString().Should().Be(DiagnosticSettingsTemplateBuilder.ResourceType);
    }

    [Fact]
    public void BuildKqlCheatsheet_ContainsDedicatedModeWarningAndKeyQueries()
    {
        var kql = new DiagnosticSettingsTemplateBuilder().BuildKqlCheatsheet();

        kql.Should().Contain("logAnalyticsDestinationType = \"Dedicated\"");
        kql.Should().Contain("ADFPipelineRun");
        kql.Should().Contain("ADFActivityRun");
        kql.Should().Contain("parse_json(UserProperties)");
    }
}
