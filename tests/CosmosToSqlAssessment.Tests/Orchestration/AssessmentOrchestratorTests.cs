using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CosmosToSqlAssessment.Tests.Orchestration;

/// <summary>
/// Narrow-contract unit tests for <see cref="AssessmentOrchestrator"/>.
///
/// <para>
/// The orchestrator's full <see cref="AssessmentOrchestrator.RunAsync"/> flow
/// is dominated by Azure SDK calls (CosmosClient + LogsQueryClient with
/// <c>DefaultAzureCredential</c>) and interactive <see cref="Console.ReadLine"/>
/// prompts. Behavioral coverage of that flow lives in the Wave-1 #125 end-to-end
/// harness under <c>tests/.../EndToEnd/</c>.
/// </para>
///
/// <para>
/// These unit tests focus on the parts that are safely testable in-process:
/// construction, DI wiring, and the early no-I/O return path triggered when
/// neither CLI options nor configuration supply a Cosmos DB account endpoint.
/// </para>
/// </summary>
public class AssessmentOrchestratorTests
{
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

    [Fact]
    public void Ctor_WithValidDependencies_DoesNotThrow()
    {
        var configuration = EmptyConfiguration();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<AssessmentOrchestrator>>();

        Action act = () => _ = new AssessmentOrchestrator(provider, configuration, logger);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCosmosAssessment_RegistersAssessmentOrchestrator_AndResolvesFromScope()
    {
        var configuration = EmptyConfiguration();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        orchestrator.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_WithNoEndpointConfiguredAnywhere_ReturnsOneWithoutInvokingAzure()
    {
        // GetUserInputsAsync short-circuits (returns null) before any
        // Console.ReadLine or Azure SDK call when both
        // options.AccountEndpoint and configuration["CosmosDb:AccountEndpoint"]
        // are empty. RunAsync then returns 1 cleanly. This locks in that
        // early-failure contract.
        var configuration = EmptyConfiguration();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        var exitCode = await orchestrator.RunAsync(new CliOptions(), CancellationToken.None);

        exitCode.Should().Be(1);
    }
}
