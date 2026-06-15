using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Orchestration;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.SqlProject;

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

            // SQL Project services
            services.AddScoped<SqlDatabaseProjectService>();
            services.AddScoped<SqlProjectIntegrationService>();
            services.AddScoped<SqlProjectGenerationService>();

            // Orchestration
            services.AddScoped<AssessmentOrchestrator>();

            return services;
        }
    }
}
