using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CosmosToSqlAssessment.Tests.DependencyInjection;

/// <summary>
/// Tests that <see cref="ServiceCollectionExtensions.AddCosmosAssessment"/>
/// (introduced by sub-issue #188 of parent #126) registers the expected
/// app-owned services with the expected lifetimes and that every registered
/// app type can be resolved from a built provider.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static IConfiguration MinimalConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // CosmosDbAnalysisService and DataQualityAnalysisService
                // validate the endpoint in their constructors and throw if
                // it's missing. Supplying a syntactically valid endpoint is
                // sufficient — services lazily construct CosmosClient and
                // don't network on ctor.
                ["CosmosDb:AccountEndpoint"] = "https://test.documents.azure.com:443/"
            })
            .Build();

    [Fact]
    public void AddCosmosAssessment_ReturnsSameServiceCollectionInstance_ForChaining()
    {
        var services = new ServiceCollection();
        var configuration = MinimalConfiguration();

        var returned = services.AddCosmosAssessment(configuration);

        returned.Should().BeSameAs(services);
    }

    [Fact]
    public void AddCosmosAssessment_RegistersConfigurationAsSingleton()
    {
        var services = new ServiceCollection();
        var configuration = MinimalConfiguration();

        services.AddCosmosAssessment(configuration);

        var descriptor = services.Single(s => s.ServiceType == typeof(IConfiguration));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationInstance.Should().BeSameAs(configuration);
    }

    [Theory]
    [InlineData(typeof(CosmosDbAnalysisService))]
    [InlineData(typeof(SqlMigrationAssessmentService))]
    [InlineData(typeof(DataFactoryEstimateService))]
    [InlineData(typeof(DataQualityAnalysisService))]
    [InlineData(typeof(ReportGenerationService))]
    [InlineData(typeof(SqlDatabaseProjectService))]
    [InlineData(typeof(SqlProjectIntegrationService))]
    [InlineData(typeof(SqlProjectGenerationService))]
    [InlineData(typeof(AssessmentOrchestrator))]
    public void AddCosmosAssessment_RegistersAppServicesAsScoped(Type serviceType)
    {
        var services = new ServiceCollection();

        services.AddCosmosAssessment(MinimalConfiguration());

        var descriptor = services.SingleOrDefault(s => s.ServiceType == serviceType);
        descriptor.Should().NotBeNull($"{serviceType.Name} should be registered");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Theory]
    [InlineData(typeof(CosmosDbAnalysisService))]
    [InlineData(typeof(SqlMigrationAssessmentService))]
    [InlineData(typeof(DataFactoryEstimateService))]
    [InlineData(typeof(DataQualityAnalysisService))]
    [InlineData(typeof(ReportGenerationService))]
    [InlineData(typeof(SqlDatabaseProjectService))]
    [InlineData(typeof(SqlProjectIntegrationService))]
    [InlineData(typeof(SqlProjectGenerationService))]
    [InlineData(typeof(AssessmentOrchestrator))]
    public void AddCosmosAssessment_AllRegisteredAppServicesResolveFromScope(Type serviceType)
    {
        var services = new ServiceCollection().AddCosmosAssessment(MinimalConfiguration());
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService(serviceType);

        resolved.Should().NotBeNull();
        resolved.Should().BeOfType(serviceType);
    }

    [Fact]
    public void AddCosmosAssessment_LoggingPipeline_ResolvesOrchestratorLogger()
    {
        // Don't pin the exact logging-stack descriptors (Microsoft.Extensions.Logging
        // internals are an implementation detail). Just verify that the AddLogging
        // call produced a working pipeline: ILogger<AssessmentOrchestrator> resolves.
        var services = new ServiceCollection().AddCosmosAssessment(MinimalConfiguration());
        using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<AssessmentOrchestrator>>();

        logger.Should().NotBeNull();
    }
}
