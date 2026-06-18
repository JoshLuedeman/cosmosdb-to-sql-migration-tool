using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Orchestration;

namespace CosmosToSqlAssessment
{
    /// <summary>
    /// Cosmos DB to SQL Migration Assessment Tool entry point.
    /// Parses CLI args, builds configuration + DI, hands off to <see cref="AssessmentOrchestrator"/>.
    /// </summary>
    internal class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new();

        static async Task<int> Main(string[] args)
        {
            // Handle Ctrl+C gracefully
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _cancellationTokenSource.Cancel();
                Console.WriteLine("\nOperation cancelled by user.");
            };

            try
            {
                // Parse command line arguments
                var options = CliArgumentParser.Parse(args);
                if (options == null)
                {
                    return 1; // Help was displayed or invalid arguments
                }

                // Validate command line options
                if (!CliArgumentParser.Validate(options))
                {
                    return 1;
                }

                // Interactive wizard mode placeholder (implemented in #149+)
                if (options.Interactive)
                {
                    Console.WriteLine("Interactive wizard mode is not yet implemented. No assessment was run.");
                    return 0;
                }

                // Build configuration
                var configuration = BuildConfiguration(options);

                // Setup dependency injection
                var services = new ServiceCollection().AddCosmosAssessment(configuration);
                using var serviceProvider = services.BuildServiceProvider();

                // Resolve and run the orchestrator inside a fresh scope so scoped
                // services (including the orchestrator itself) get correct lifetimes.
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<AssessmentOrchestrator>();
                return await orchestrator.RunAsync(options, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Assessment cancelled.");
                return 130; // Standard exit code for SIGINT
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");

                // Log full exception details if logger is available
                try
                {
                    var configuration = BuildConfiguration();
                    var services = new ServiceCollection().AddCosmosAssessment(configuration);
                    using var serviceProvider = services.BuildServiceProvider();
                    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "Unhandled exception occurred during assessment");
                }
                catch
                {
                    // If logging setup fails, just write to console
                    Console.WriteLine($"Full error details: {ex}");
                }

                return 1;
            }
        }

        internal static IConfiguration BuildConfiguration(CliOptions? options = null)
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables();

            // Add command line overrides if provided
            if (options != null)
            {
                var commandLineConfig = new Dictionary<string, string?>();

                if (!string.IsNullOrEmpty(options.WorkspaceId))
                {
                    commandLineConfig["AzureMonitor:WorkspaceId"] = options.WorkspaceId;
                }

                configBuilder.AddInMemoryCollection(commandLineConfig);
            }

            return configBuilder.Build();
        }

    }
}
