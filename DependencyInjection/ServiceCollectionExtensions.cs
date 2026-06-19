using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.ResourceManager;
using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Orchestration;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.DataFactory;
using CosmosToSqlAssessment.Services.Discovery;
using CosmosToSqlAssessment.Services.Feedback;
using CosmosToSqlAssessment.Services.Monitoring;
using CosmosToSqlAssessment.Services.Migration;
using CosmosToSqlAssessment.SqlProject;
using System.Net.Http;

namespace CosmosToSqlAssessment.DependencyInjection
{
    /// <summary>
    /// Extension methods that register the Cosmos-to-SQL assessment tool's service graph
    /// on an <see cref="IServiceCollection"/>.
    /// </summary>
    internal static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers configuration, logging, application services, SQL project services,
        /// and the <see cref="AssessmentOrchestrator"/> on the supplied service collection.
        /// </summary>
        /// <param name="services">The service collection to add registrations to.</param>
        /// <param name="configuration">The application <see cref="IConfiguration"/> to bind.</param>
        /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
        internal static IServiceCollection AddCosmosAssessment(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // Configuration
            services.AddSingleton(configuration);

            // Logging
            services.AddLogging(builder =>
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddConsole();
                builder.AddDebug();
            });

            // Application services
            services.AddScoped<CosmosDbAnalysisService>();
            services.AddScoped<SqlMigrationAssessmentService>();
            services.AddScoped<DataFactoryEstimateService>();
            services.AddScoped<DataQualityAnalysisService>();
            services.AddScoped<ReportGenerationService>();

            // Incremental change-feed migration analysis (parent #69). Pure services over the collected
            // Cosmos analysis; consumed by the orchestrator after the core assessment.
            services.AddScoped<ChangeFeedAvailabilityAnalyzer>();

            // SQL Project services
            services.AddScoped<SqlDatabaseProjectService>();
            services.AddScoped<SqlProjectIntegrationService>();
            services.AddScoped<SqlProjectGenerationService>();

            // Data Factory generation services (parent #70)
            services.AddScoped<LinkedServiceBuilder>();
            services.AddScoped<DatasetBuilder>();
            services.AddScoped<CopyActivityBuilder>();
            services.AddScoped<IDataFactoryPipelineGenerator, DataFactoryPipelineGenerationService>();

            // Azure Monitor auto-discovery services (parent #76)
            services.AddSingleton(sp => new ArmClient(new DefaultAzureCredential()));
            services.AddSingleton<IResourceGraphQueryClient, ArmResourceGraphQueryClient>();
            services.AddSingleton<IDiagnosticSettingsClient, ArmDiagnosticSettingsClient>();
            services.AddScoped<IResourceGraphDiscoveryService, ResourceGraphDiscoveryService>();
            services.AddScoped<IDiagnosticSettingsDiscoveryService, DiagnosticSettingsDiscoveryService>();
            services.AddSingleton<IAutoDiscoveryService, AutoDiscoveryService>();

            // Real-time monitoring &amp; alerting services (parent #133).
            services.AddSingleton(_ =>
                configuration.GetSection(AzureMonitorMetricOptions.SectionName).Get<AzureMonitorMetricOptions>()
                ?? new AzureMonitorMetricOptions());
            services.AddSingleton<AzureMonitorMetricPayloadBuilder>();
            services.AddSingleton<IMigrationMetricPublisher, AzureMonitorMetricPublisher>();
            services.AddScoped<MigrationMonitoringService>();
            services.AddSingleton(_ =>
                configuration.GetSection(AlertRuleOptions.SectionName).Get<AlertRuleOptions>()
                ?? new AlertRuleOptions());
            services.AddSingleton<AlertRuleTemplateBuilder>();
            services.AddScoped<AlertRuleTemplateGenerationService>();
            services.AddScoped<IMigrationStatusSource, AzureMonitorMigrationStatusSource>();
            services.AddSingleton(_ =>
                configuration.GetSection(AnomalyDetectionOptions.SectionName).Get<AnomalyDetectionOptions>()
                ?? new AnomalyDetectionOptions());
            services.AddScoped<AnomalyDetectionService>();
            services.AddScoped<MigrationStatusService>();

            // Continuous-learning feedback loop (parent #132). Opt-in only; default OFF. Each
            // registration is independent of the other parents' registrations — keep them all.
            var feedbackOptions = configuration.GetSection(FeedbackOptions.SectionName).Get<FeedbackOptions>()
                ?? new FeedbackOptions();
            services.AddSingleton(feedbackOptions);
            services.AddSingleton<IFeedbackStore, LocalJsonFeedbackStore>();
            if (!string.IsNullOrWhiteSpace(feedbackOptions.TelemetryEndpoint))
            {
                services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
                services.AddSingleton<IFeedbackTelemetrySink, HttpFeedbackTelemetrySink>();
            }
            else
            {
                services.AddSingleton<IFeedbackTelemetrySink, NullFeedbackTelemetrySink>();
            }
            services.AddScoped<FeedbackCollectionService>();
            services.AddScoped<RecommendationRefinementService>();

            // Orchestration
            services.AddScoped<AssessmentOrchestrator>();

            // Multi-agent orchestration layer (parent #131). Each agent wraps an existing service and
            // communicates through the shared assessment context; the orchestrator coordinates them.
            services.AddScoped<IAssessmentAgent, CosmosAnalyzerAgent>();
            services.AddScoped<IAssessmentAgent, SqlPlannerAgent>();
            services.AddScoped<IAssessmentAgent, DataQualityAgent>();
            services.AddScoped<IAssessmentAgent, DataFactoryEstimatorAgent>();
            services.AddScoped<IAssessmentAgent, ValidatorAgent>();
            services.AddScoped<AgentOrchestrator>();

            return services;
        }
    }
}
