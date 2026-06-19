using System.Text.Json;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.DataFactory;
using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AlertRuleTemplateBuilderTests
{
    private static AlertRuleTemplateBuilder CreateBuilder() => new();

    private static IDictionary<string, object?> Resource(IDictionary<string, object?> template, int index) =>
        (IDictionary<string, object?>)((List<object?>)template["resources"]!)[index]!;

    private static IDictionary<string, object?> Properties(IDictionary<string, object?> resource) =>
        (IDictionary<string, object?>)resource["properties"]!;

    private static IDictionary<string, object?> FirstCriterion(IDictionary<string, object?> resource)
    {
        var criteria = (IDictionary<string, object?>)Properties(resource)["criteria"]!;
        return (IDictionary<string, object?>)((List<object?>)criteria["allOf"]!)[0]!;
    }

    [Fact]
    public void BuildMetricAlertsTemplate_ReturnsFullDeployableArmTemplate()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        template["$schema"].Should().Be("https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#");
        template["contentVersion"].Should().Be("1.0.0.0");
        template.Should().ContainKey("parameters");
        template.Should().ContainKey("resources");
    }

    [Fact]
    public void BuildMetricAlertsTemplate_SerializesToWellFormedJson()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        var json = AdfJsonSerializer.Serialize(template);

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void BuildMetricAlertsTemplate_DefaultOptions_EmitsErrorRateSpikeAndLowThroughput()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        var resources = (List<object?>)template["resources"]!;
        resources.Should().HaveCount(3);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_WithRequestUnitsAlert_AddsFourthResource()
    {
        var options = new AlertRuleOptions
        {
            IncludeRequestUnitsThresholdAlert = true,
            RequestUnitsThreshold = 10000,
        };

        var template = CreateBuilder().BuildMetricAlertsTemplate(options);

        var resources = (List<object?>)template["resources"]!;
        resources.Should().HaveCount(4);
        var ruCriterion = FirstCriterion(Resource(template, 3));
        ruCriterion["metricName"].Should().Be(MigrationMonitoringService.MetricRequestUnits);
        ruCriterion["threshold"].Should().Be(10000d);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_WithoutLowThroughput_OmitsThatResource()
    {
        var options = new AlertRuleOptions { IncludeLowThroughputAlert = false };

        var template = CreateBuilder().BuildMetricAlertsTemplate(options);

        var resources = (List<object?>)template["resources"]!;
        resources.Should().HaveCount(2);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_ResourcesUseCorrectTypeAndApiVersionAndGlobalLocation()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        foreach (var item in (List<object?>)template["resources"]!)
        {
            var resource = (IDictionary<string, object?>)item!;
            resource["type"].Should().Be(AlertRuleTemplateBuilder.MetricAlertResourceType);
            resource["apiVersion"].Should().Be(AlertRuleTemplateBuilder.MetricAlertApiVersion);
            resource["location"].Should().Be("global");
        }
    }

    [Fact]
    public void BuildMetricAlertsTemplate_ErrorRateUsesStaticCriteriaAndConfiguredThreshold()
    {
        var options = new AlertRuleOptions { ErrorRateThreshold = 0.1 };
        var template = CreateBuilder().BuildMetricAlertsTemplate(options);

        var errorRate = Resource(template, 0);
        var criteria = (IDictionary<string, object?>)Properties(errorRate)["criteria"]!;
        criteria["odata.type"].Should().Be(AlertRuleTemplateBuilder.StaticCriteriaODataType);

        var criterion = FirstCriterion(errorRate);
        criterion["metricName"].Should().Be(MigrationMonitoringService.MetricErrorRate);
        criterion["criterionType"].Should().Be("StaticThresholdCriterion");
        criterion["operator"].Should().Be("GreaterThan");
        criterion["threshold"].Should().Be(0.1);
        criterion["skipMetricValidation"].Should().Be(true);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_ErrorSpikeUsesDynamicMultiResourceCriteria()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        var errorSpike = Resource(template, 1);
        var criteria = (IDictionary<string, object?>)Properties(errorSpike)["criteria"]!;
        criteria["odata.type"].Should().Be(AlertRuleTemplateBuilder.DynamicCriteriaODataType);

        var criterion = FirstCriterion(errorSpike);
        criterion["metricName"].Should().Be(MigrationMonitoringService.MetricErrorCount);
        criterion["criterionType"].Should().Be("DynamicThresholdCriterion");
        criterion["alertSensitivity"].Should().Be("Medium");
        criterion.Should().ContainKey("failingPeriods");
        criterion["skipMetricValidation"].Should().Be(true);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_LowThroughputUsesRowsMigratedLessThanOrEqualZero()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());

        var lowThroughput = Resource(template, 2);
        var criterion = FirstCriterion(lowThroughput);
        criterion["metricName"].Should().Be(MigrationMonitoringService.MetricRowsMigrated);
        criterion["operator"].Should().Be("LessThanOrEqual");
        criterion["threshold"].Should().Be(0d);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_WiresScopeAndActionToParameters()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());
        var props = Properties(Resource(template, 0));

        var scopes = (List<object?>)props["scopes"]!;
        scopes.Should().ContainSingle().Which.Should().Be($"[parameters('{AlertRuleTemplateBuilder.ParamTargetResourceId}')]");

        var actions = (List<object?>)props["actions"]!;
        var action = (IDictionary<string, object?>)actions[0]!;
        action["actionGroupId"].Should().Be($"[parameters('{AlertRuleTemplateBuilder.ParamActionGroupResourceId}')]");

        props["autoMitigate"].Should().Be(true);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_DeclaresExpectedParameters()
    {
        var template = CreateBuilder().BuildMetricAlertsTemplate(new AlertRuleOptions());
        var parameters = (IDictionary<string, object?>)template["parameters"]!;

        parameters.Should().ContainKeys(
            AlertRuleTemplateBuilder.ParamAlertNamePrefix,
            AlertRuleTemplateBuilder.ParamTargetResourceId,
            AlertRuleTemplateBuilder.ParamActionGroupResourceId);
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_SerializesToWellFormedJson()
    {
        var template = CreateBuilder().BuildStalledPipelineLogAlertTemplate(new AlertRuleOptions());

        var json = AdfJsonSerializer.Serialize(template);

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_UsesScheduledQueryRuleTypeAndLogAlertKind()
    {
        var template = CreateBuilder().BuildStalledPipelineLogAlertTemplate(new AlertRuleOptions());
        var resource = Resource(template, 0);

        resource["type"].Should().Be(AlertRuleTemplateBuilder.ScheduledQueryRuleResourceType);
        resource["apiVersion"].Should().Be(AlertRuleTemplateBuilder.ScheduledQueryRuleApiVersion);
        resource["kind"].Should().Be("LogAlert");
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_ActionGroupsIsArrayOfStrings()
    {
        var template = CreateBuilder().BuildStalledPipelineLogAlertTemplate(new AlertRuleOptions());
        var props = Properties(Resource(template, 0));

        var actions = (IDictionary<string, object?>)props["actions"]!;
        var actionGroups = (List<object?>)actions["actionGroups"]!;
        actionGroups.Should().ContainSingle()
            .Which.Should().Be($"[parameters('{AlertRuleTemplateBuilder.ParamActionGroupResourceId}')]");
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_SetsSkipQueryValidationAndQueriesAdfTables()
    {
        var template = CreateBuilder().BuildStalledPipelineLogAlertTemplate(new AlertRuleOptions());
        var props = Properties(Resource(template, 0));

        props["skipQueryValidation"].Should().Be(true);

        var criteria = (IDictionary<string, object?>)props["criteria"]!;
        var criterion = (IDictionary<string, object?>)((List<object?>)criteria["allOf"]!)[0]!;
        var query = (string)criterion["query"]!;
        query.Should().Contain("ADFPipelineRun");
        query.Should().Contain("ADFActivityRun");
        query.Should().Contain("ago(15m)");
        criterion["operator"].Should().Be("GreaterThan");
        criterion["threshold"].Should().Be(0);
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_DeclaresWorkspaceParameter()
    {
        var template = CreateBuilder().BuildStalledPipelineLogAlertTemplate(new AlertRuleOptions());
        var parameters = (IDictionary<string, object?>)template["parameters"]!;

        parameters.Should().ContainKeys(
            AlertRuleTemplateBuilder.ParamAlertNamePrefix,
            AlertRuleTemplateBuilder.ParamWorkspaceResourceId,
            AlertRuleTemplateBuilder.ParamActionGroupResourceId);
    }

    [Fact]
    public void BuildMetricAlertsTemplate_NullOptions_Throws()
    {
        var act = () => CreateBuilder().BuildMetricAlertsTemplate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildStalledPipelineLogAlertTemplate_NullOptions_Throws()
    {
        var act = () => CreateBuilder().BuildStalledPipelineLogAlertTemplate(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
