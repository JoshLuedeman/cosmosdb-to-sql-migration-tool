using System.Text.Json;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AlertRuleTemplateGenerationServiceTests : IDisposable
{
    private readonly string _outputDir;

    public AlertRuleTemplateGenerationServiceTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "alert-rule-gen-" + Guid.NewGuid().ToString("N"));
    }

    private static AlertRuleTemplateGenerationService CreateService(AlertRuleOptions options) =>
        new(new AlertRuleTemplateBuilder(), options, Mock.Of<ILogger<AlertRuleTemplateGenerationService>>());

    [Fact]
    public async Task GenerateAsync_WritesTemplatesAndReadme()
    {
        var service = CreateService(new AlertRuleOptions());

        var result = await service.GenerateAsync(_outputDir);

        File.Exists(result.MetricAlertsTemplatePath).Should().BeTrue();
        File.Exists(result.StalledPipelineTemplatePath).Should().BeTrue();
        File.Exists(result.ReadmePath).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_WritesUnderMonitoringAlertRulesFolder()
    {
        var service = CreateService(new AlertRuleOptions());

        var result = await service.GenerateAsync(_outputDir);

        var expectedDir = Path.Combine(
            _outputDir,
            AlertRuleTemplateGenerationService.AlertRulesFolder.Replace('/', Path.DirectorySeparatorChar));
        Path.GetDirectoryName(result.MetricAlertsTemplatePath).Should().Be(expectedDir);
        Path.GetFileName(result.MetricAlertsTemplatePath)
            .Should().Be(AlertRuleTemplateGenerationService.MetricAlertsTemplateFileName);
        Path.GetFileName(result.StalledPipelineTemplatePath)
            .Should().Be(AlertRuleTemplateGenerationService.StalledPipelineTemplateFileName);
    }

    [Fact]
    public async Task GenerateAsync_EmittedTemplatesAreValidJson()
    {
        var service = CreateService(new AlertRuleOptions());

        var result = await service.GenerateAsync(_outputDir);

        var metricsJson = await File.ReadAllTextAsync(result.MetricAlertsTemplatePath);
        var stalledJson = await File.ReadAllTextAsync(result.StalledPipelineTemplatePath);

        var parseMetrics = () => JsonDocument.Parse(metricsJson);
        var parseStalled = () => JsonDocument.Parse(stalledJson);
        parseMetrics.Should().NotThrow();
        parseStalled.Should().NotThrow();
    }

    [Fact]
    public async Task GenerateAsync_NoWarnings_ForValidDefaults()
    {
        var service = CreateService(new AlertRuleOptions());

        var result = await service.GenerateAsync(_outputDir);

        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_WarnsWhenRequestUnitsAlertEnabledWithNonPositiveThreshold()
    {
        var service = CreateService(new AlertRuleOptions
        {
            IncludeRequestUnitsThresholdAlert = true,
            RequestUnitsThreshold = 0,
        });

        var result = await service.GenerateAsync(_outputDir);

        result.Warnings.Should().ContainSingle().Which.Should().Contain("RequestUnitsThreshold");
    }

    [Fact]
    public async Task GenerateAsync_NullOrEmptyOutputDirectory_Throws()
    {
        var service = CreateService(new AlertRuleOptions());

        var act = async () => await service.GenerateAsync("  ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var builder = new AlertRuleTemplateBuilder();
        var options = new AlertRuleOptions();
        var logger = Mock.Of<ILogger<AlertRuleTemplateGenerationService>>();

        ((Func<AlertRuleTemplateGenerationService>)(() => new AlertRuleTemplateGenerationService(null!, options, logger)))
            .Should().Throw<ArgumentNullException>();
        ((Func<AlertRuleTemplateGenerationService>)(() => new AlertRuleTemplateGenerationService(builder, null!, logger)))
            .Should().Throw<ArgumentNullException>();
        ((Func<AlertRuleTemplateGenerationService>)(() => new AlertRuleTemplateGenerationService(builder, options, null!)))
            .Should().Throw<ArgumentNullException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
