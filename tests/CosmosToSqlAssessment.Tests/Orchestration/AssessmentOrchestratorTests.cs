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

    [Fact]
    public async Task RunAsync_GenerateAlerts_ShortCircuitsAndWritesTemplatesWithoutEndpoint()
    {
        // #256: `migration generate-alerts` must short-circuit before GetUserInputsAsync, so it works with
        // no Cosmos endpoint configured anywhere. It writes the ARM templates + README and returns 0.
        var configuration = EmptyConfiguration();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        var tempDir = Path.Combine(Path.GetTempPath(), "alerts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = await orchestrator.RunAsync(
                new CliOptions { GenerateAlerts = true, OutputDirectory = tempDir },
                CancellationToken.None);

            exitCode.Should().Be(0);
            var alertsDir = Path.Combine(tempDir, "Monitoring", "AlertRules");
            File.Exists(Path.Combine(alertsDir, "metric-alerts.template.json")).Should().BeTrue();
            File.Exists(Path.Combine(alertsDir, "stalled-pipeline-log-alert.template.json")).Should().BeTrue();
            File.Exists(Path.Combine(alertsDir, "README.md")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_PublishMetrics_WhenDisabled_ShortCircuitsToNoOpWithoutEndpoint()
    {
        // #257: publishing is off by default (AzureMonitor:Metrics:Enabled = false). The command must
        // short-circuit before GetUserInputsAsync, print a clear message, and return 0 without streaming.
        var configuration = EmptyConfiguration();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        var exitCode = await orchestrator.RunAsync(
            new CliOptions { PublishMetrics = true }, CancellationToken.None);

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_PublishMetrics_WhenEnabledButMisconfigured_ReturnsZeroNoOp()
    {
        // #257: enabled but missing Region/ResourceId is a misconfiguration; degrade to a no-op (return 0)
        // rather than failing or attempting a live call.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMonitor:Metrics:Enabled"] = "true",
            })
            .Build();
        var services = new ServiceCollection().AddCosmosAssessment(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();

        var exitCode = await orchestrator.RunAsync(
            new CliOptions { PublishMetrics = true }, CancellationToken.None);

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunDatabaseAssessmentAsync_WhenCancelledMidPhase_PropagatesOperationCanceledException()
    {
        // Regression test for #237: the per-phase broad catch blocks must not swallow
        // OperationCanceledException (Ctrl+C) by wrapping it in InvalidOperationException -
        // otherwise Program.Main's OperationCanceledException -> exit-code-130 mapping never fires.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CosmosDb:AccountEndpoint"] = "https://fake.documents.azure.com:443/"
            })
            .Build();

        var services = new ServiceCollection().AddCosmosAssessment(configuration);

        // Replace the real Cosmos analysis service with one that observes cancellation and throws
        // OperationCanceledException, simulating Ctrl+C during Phase 1. The last registration wins.
        services.AddScoped<CosmosDbAnalysisService>(sp =>
            new CancellingCosmosDbAnalysisService(
                configuration,
                sp.GetRequiredService<ILogger<CosmosDbAnalysisService>>()));

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<AssessmentOrchestrator>>();
        var orchestrator = new AssessmentOrchestrator(provider, configuration, logger);

        var userInputs = new UserInputs
        {
            AccountEndpoint = "https://fake.documents.azure.com:443/",
            DatabaseNames = new List<string> { "db" }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => orchestrator.RunDatabaseAssessmentAsync(provider, userInputs, "db", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void BuildOverrideConfiguration_OverridesOuterAccountEndpoint_AndPreservesOtherKeys()
    {
        // Regression test for #238: the per-database in-memory endpoint override must win over the
        // outer configuration (it is added last). The previous ordering made the override a no-op.
        var outer = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CosmosDb:AccountEndpoint"] = "https://outer.documents.azure.com:443/",
                ["CosmosDb:DatabaseName"] = "OuterDb"
            })
            .Build();

        var services = new ServiceCollection().AddCosmosAssessment(outer);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<AssessmentOrchestrator>>();
        var orchestrator = new AssessmentOrchestrator(provider, outer, logger);

        var overrideConfig = orchestrator.BuildOverrideConfiguration("https://override.documents.azure.com:443/");

        overrideConfig["CosmosDb:AccountEndpoint"].Should().Be("https://override.documents.azure.com:443/");
        // Keys absent from the override still resolve from the outer configuration.
        overrideConfig["CosmosDb:DatabaseName"].Should().Be("OuterDb");
    }

    /// <summary>
    /// Test double whose <see cref="AnalyzeDatabaseAsync(string, CancellationToken)"/> override
    /// honours cancellation and throws <see cref="OperationCanceledException"/>, standing in for a
    /// Ctrl+C during the first assessment phase.
    /// </summary>
    private sealed class CancellingCosmosDbAnalysisService : CosmosDbAnalysisService
    {
        public CancellingCosmosDbAnalysisService(IConfiguration configuration, ILogger<CosmosDbAnalysisService> logger)
            : base(configuration, logger)
        {
        }

        public override Task<CosmosDbAnalysis> AnalyzeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }
    }
}
