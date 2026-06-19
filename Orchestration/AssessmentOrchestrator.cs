using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using CosmosToSqlAssessment.Agents;
using CosmosToSqlAssessment.Cli;
using CosmosToSqlAssessment.DependencyInjection;
using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Reporting;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.DataFactory;
using CosmosToSqlAssessment.Services.Discovery;
using CosmosToSqlAssessment.Services.Monitoring;
using CosmosToSqlAssessment.SqlProject;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Orchestration;

/// <summary>
/// Owns the entire ""happy-path orchestration"" for the Cosmos DB to SQL
/// Migration Assessment Tool: argument-driven configuration, per-database
/// analysis loop, SQL migration assessment, data-quality analysis, Data Factory
/// estimation, report + SQL-project generation, and the user-facing console
/// flow (welcome / completion messages).
///
/// <para>
/// Extracted from <c>Program.cs</c> as part of issue #187 / parent #126. The
/// behavior is intentionally preserved 1-for-1 with the previous in-line
/// implementation. <c>Program.Main</c> retains only argument parsing,
/// configuration / DI setup, signal handling, and exit-code translation.
/// </para>
///
/// <para>
/// Three pre-existing bugs were observed during the extraction and preserved
/// intentionally (filed as follow-up issues for separate fixes):
/// </para>
/// <list type="bullet">
///   <item>Per-database override-configuration provider ordering reverses the intended endpoint override (later providers win).</item>
///   <item>Broad <c>catch (Exception)</c> blocks in <see cref="RunAssessmentAsync"/> swallow <see cref="OperationCanceledException"/> as <c>InvalidOperationException</c>, defeating Main's Ctrl+C exit-code 130 mapping.</item>
///   <item>Override <see cref="ServiceProvider"/> isn't disposed inside a <c>try/finally</c>, so mid-loop failures leak it.</item>
/// </list>
/// </summary>
internal sealed class AssessmentOrchestrator
{
    private const string AzureMonitorTestQuery = "AzureDiagnostics | where ResourceProvider == 'MICROSOFT.DOCUMENTDB' | take 1";
    private const int AzureMonitorTestQueryDays = 1;

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AssessmentOrchestrator> _logger;

    public AssessmentOrchestrator(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<AssessmentOrchestrator> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full assessment flow for the supplied CLI options and returns
    /// the process exit code (0 = success, 1 = failure / user-error).
    /// When <c>options.TestConnection</c> is set, short-circuits to a
    /// connection-only smoke test.
    /// </summary>
    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.MigrationStatus)
        {
            _logger.LogInformation("Running migration status report");
            return await RunMigrationStatusAsync(options, cancellationToken);
        }

        if (options.TestConnection)
        {
            _logger.LogInformation("Running connection test");
            return await TestConnectionAsync(options);
        }

        _logger.LogInformation("Starting Cosmos DB to SQL Migration Assessment Tool");
        DisplayWelcomeMessage();

        var userInputs = await GetUserInputsAsync(options);
        if (userInputs == null)
        {
            return 1;
        }

        if (!ValidateEffectiveConfiguration(userInputs))
        {
            return 1;
        }

        var assessmentResult = await RunAssessmentAsync(userInputs, cancellationToken);
        await GenerateOutputsAsync(assessmentResult, userInputs.OutputDirectory, options, cancellationToken);
        await GenerateSqlProjectAsync(assessmentResult, userInputs.OutputDirectory, cancellationToken);
        if (!options.AssessmentOnly)
        {
            await GenerateDataFactoryArtifactsAsync(assessmentResult, userInputs.OutputDirectory, cancellationToken);
        }
        DisplayCompletionMessage(assessmentResult, options);

        _logger.LogInformation("Assessment completed successfully");
        return 0;
    }

    private static void DisplayWelcomeMessage()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("    Cosmos DB to SQL Migration Assessment Tool");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("This tool will analyze your Cosmos DB database and provide:");
        Console.WriteLine("• Comprehensive performance and schema analysis");
        Console.WriteLine("• Pre-migration data quality checks");
        Console.WriteLine("• SQL migration recommendations and mapping");
        Console.WriteLine("• Azure Data Factory migration estimates");
        Console.WriteLine("• Detailed Excel and Word reports");
        Console.WriteLine("• SQL Database Project for deployment to Azure SQL");
        Console.WriteLine("• Ready-to-deploy SQL Database projects (.sqlproj)");
        Console.WriteLine();
        Console.WriteLine("Features:");
        Console.WriteLine("• 6-month performance metrics analysis");
        Console.WriteLine("• Data quality analysis (nulls, duplicates, types, outliers)");
        Console.WriteLine("• Index recommendations based on usage patterns");
        Console.WriteLine("• Azure SQL platform recommendations");
        Console.WriteLine("• Migration effort and cost estimates");
        Console.WriteLine("• SSDT-compatible SQL projects for deployment");
        Console.WriteLine();
    }

    private bool ValidateEffectiveConfiguration(UserInputs userInputs)
    {
        var isValid = true;
        var validationErrors = new List<string>();

        if (string.IsNullOrEmpty(userInputs.AccountEndpoint))
        {
            validationErrors.Add("Cosmos DB account endpoint is required");
            isValid = false;
        }

        if (!userInputs.DatabaseNames.Any())
        {
            validationErrors.Add("At least one database name is required");
            isValid = false;
        }

        if (string.IsNullOrEmpty(userInputs.OutputDirectory))
        {
            validationErrors.Add("Output directory is required");
            isValid = false;
        }

        if (!isValid)
        {
            Console.WriteLine("❌ Configuration validation failed:");
            foreach (var error in validationErrors)
            {
                Console.WriteLine($"   • {error}");
            }
            Console.WriteLine();
            return false;
        }

        Console.WriteLine("✅ Configuration validation passed");
        Console.WriteLine($"   • Cosmos DB Endpoint: {userInputs.AccountEndpoint}");
        Console.WriteLine($"   • Database(s): {string.Join(", ", userInputs.DatabaseNames)}");
        Console.WriteLine($"   • Output Directory: {userInputs.OutputDirectory}");

        if (userInputs.MonitoringConfig?.WorkspaceId != null)
        {
            Console.WriteLine($"   • Azure Monitor: Configured");
        }
        else
        {
            Console.WriteLine("⚠️  Warning: Azure Monitor not configured - performance analysis will be limited");
        }

        Console.WriteLine();
        return true;
    }

    private async Task<AssessmentResult> RunAssessmentAsync(UserInputs userInputs, CancellationToken cancellationToken)
    {
        var assessmentResults = new List<AssessmentResult>();

        Console.WriteLine("🔍 Starting assessment...");
        Console.WriteLine();

        foreach (var databaseName in userInputs.DatabaseNames)
        {
            Console.WriteLine($"📊 Analyzing database: {databaseName}");

            IServiceProvider activeServiceProvider = _services;
            ServiceProvider? overrideServiceProvider = null;

            if (!string.IsNullOrEmpty(userInputs.AccountEndpoint) &&
                userInputs.AccountEndpoint != _configuration["CosmosDb:AccountEndpoint"])
            {
                var configDict = new Dictionary<string, string>
                {
                    ["CosmosDb:AccountEndpoint"] = userInputs.AccountEndpoint
                };
                var overrideConfig = new ConfigurationBuilder()
                    .AddInMemoryCollection(configDict!)
                    .AddConfiguration(_configuration)
                    .Build();

                var overrideServices = new ServiceCollection().AddCosmosAssessment(overrideConfig);
                overrideServiceProvider = overrideServices.BuildServiceProvider();
                activeServiceProvider = overrideServiceProvider;
            }

            var assessmentResult = await RunDatabaseAssessmentAsync(activeServiceProvider, userInputs, databaseName, cancellationToken);
            assessmentResults.Add(assessmentResult);

            Console.WriteLine($"✅ Completed assessment for database: {databaseName}");
            Console.WriteLine();

            overrideServiceProvider?.Dispose();
        }

        if (assessmentResults.Count == 1)
        {
            return assessmentResults[0];
        }

        foreach (var result in assessmentResults)
        {
            result.GenerateSeparateExcel = true;
        }

        var combinedResult = new AssessmentResult
        {
            CosmosAccountName = assessmentResults[0].CosmosAccountName,
            DatabaseName = $"Multiple Databases ({assessmentResults.Count})",
            CosmosAnalysis = CombineCosmosAnalyses(assessmentResults.Select(r => r.CosmosAnalysis).ToList()),
            SqlAssessment = CombineSqlAssessments(assessmentResults.Select(r => r.SqlAssessment).ToList()),
            DataFactoryEstimate = CombineDataFactoryEstimates(assessmentResults.Select(r => r.DataFactoryEstimate).ToList()),
            IndividualDatabaseResults = assessmentResults.ToList()
        };

        return combinedResult;
    }

    private async Task<AssessmentResult> RunDatabaseAssessmentAsync(
        IServiceProvider activeServiceProvider,
        UserInputs userInputs,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var assessmentResult = new AssessmentResult
        {
            CosmosAccountName = CosmosEndpointParser.GetAccountNameOrDefault(userInputs.AccountEndpoint),
            DatabaseName = databaseName
        };

        if (userInputs.UseAgentic)
        {
            return await RunDatabaseAssessmentAgenticAsync(
                activeServiceProvider, assessmentResult, databaseName, cancellationToken);
        }

        // Step 1: Cosmos DB Analysis
        Console.WriteLine("📊 Phase 1: Analyzing Cosmos DB database...");
        var cosmosService = activeServiceProvider.GetRequiredService<CosmosDbAnalysisService>();

        try
        {
            assessmentResult.CosmosAnalysis = await cosmosService.AnalyzeDatabaseAsync(databaseName, cancellationToken);
            Console.WriteLine($"   ✅ Analyzed {assessmentResult.CosmosAnalysis.Containers.Count} containers");

            if (assessmentResult.CosmosAnalysis.MonitoringLimitations.Any())
            {
                Console.WriteLine($"   ⚠️  {assessmentResult.CosmosAnalysis.MonitoringLimitations.Count} monitoring limitations detected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze Cosmos DB");
            throw new InvalidOperationException($"Cosmos DB analysis failed for database {databaseName}: {ex.Message}", ex);
        }

        // Step 2: SQL Migration Assessment
        Console.WriteLine();
        Console.WriteLine("🎯 Phase 2: Generating SQL migration assessment...");
        var sqlService = activeServiceProvider.GetRequiredService<SqlMigrationAssessmentService>();

        try
        {
            assessmentResult.SqlAssessment = await sqlService.AssessMigrationAsync(assessmentResult.CosmosAnalysis, databaseName, cancellationToken);
            Console.WriteLine($"   ✅ Recommended platform: {assessmentResult.SqlAssessment.RecommendedPlatform}");
            Console.WriteLine($"   ✅ Generated {assessmentResult.SqlAssessment.IndexRecommendations.Count} index recommendations");
            Console.WriteLine($"   ✅ Migration complexity: {assessmentResult.SqlAssessment.Complexity.OverallComplexity}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assess SQL migration");
            throw new InvalidOperationException($"SQL migration assessment failed for database {databaseName}: {ex.Message}", ex);
        }

        // Step 3: Data Quality Analysis
        Console.WriteLine();
        Console.WriteLine("🔍 Phase 3: Analyzing data quality...");
        var dataQualityService = activeServiceProvider.GetRequiredService<DataQualityAnalysisService>();

        try
        {
            assessmentResult.DataQualityAnalysis = await dataQualityService.AnalyzeDataQualityAsync(
                assessmentResult.CosmosAnalysis,
                databaseName,
                cancellationToken);
            Console.WriteLine($"   ✅ Analyzed {assessmentResult.DataQualityAnalysis.TotalDocumentsAnalyzed} documents");
            Console.WriteLine($"   ✅ Found {assessmentResult.DataQualityAnalysis.CriticalIssuesCount} critical issues");
            Console.WriteLine($"   ✅ Found {assessmentResult.DataQualityAnalysis.WarningIssuesCount} warnings");
            Console.WriteLine($"   ✅ Quality score: {assessmentResult.DataQualityAnalysis.Summary.OverallQualityScore:F1}/100 ({assessmentResult.DataQualityAnalysis.Summary.QualityRating})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform data quality analysis - continuing without it");
            Console.WriteLine($"   ⚠️  Data quality analysis skipped due to error: {ex.Message}");
        }

        // Step 4: Data Factory Estimates
        Console.WriteLine();
        Console.WriteLine("⏱️  Phase 4: Calculating migration estimates...");
        var dataFactoryService = activeServiceProvider.GetRequiredService<DataFactoryEstimateService>();

        try
        {
            assessmentResult.DataFactoryEstimate = await dataFactoryService.EstimateMigrationAsync(
                assessmentResult.CosmosAnalysis,
                assessmentResult.SqlAssessment,
                cancellationToken);
            Console.WriteLine($"   ✅ Estimated migration time: {assessmentResult.DataFactoryEstimate.EstimatedDuration:hh\\:mm\\:ss}");
            Console.WriteLine($"   ✅ Estimated cost: ${assessmentResult.DataFactoryEstimate.EstimatedCostUSD:F2}");
            Console.WriteLine($"   ✅ Recommended DIUs: {assessmentResult.DataFactoryEstimate.RecommendedDIUs}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate Data Factory estimates");
            throw new InvalidOperationException($"Data Factory estimation failed for database {databaseName}: {ex.Message}", ex);
        }

        return assessmentResult;
    }

    /// <summary>
    /// Computes the per-database assessment through the multi-agent orchestration layer
    /// (<c>--agentic</c>). Produces output equivalent to the single-pass
    /// <see cref="RunDatabaseAssessmentAsync"/> path, then feeds the same unchanged report /
    /// SQL-project generation.
    /// </summary>
    /// <remarks>
    /// A child DI scope is created per database so scoped agents do not leak state across a
    /// multi-database run and are disposed deterministically. Failure parity with single-pass is
    /// preserved by gating the fatal path on <em>completeness</em> (the required Cosmos analysis, SQL
    /// assessment, and Data Factory estimate are all present) rather than full acceptability — a
    /// consistency-only finding (e.g. an unmapped container) stays non-fatal, and an absent/failed
    /// optional data-quality analysis is likewise non-fatal, exactly as in the single-pass flow.
    /// </remarks>
    private async Task<AssessmentResult> RunDatabaseAssessmentAgenticAsync(
        IServiceProvider activeServiceProvider,
        AssessmentResult assessmentResult,
        string databaseName,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("🤖 Agentic mode: coordinating specialist agents (Cosmos → SQL → data quality → Data Factory → validation)...");

        using var scope = activeServiceProvider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();

        var run = await orchestrator.RunAsync(
            databaseName,
            assessmentResult.CosmosAccountName,
            new AgentOrchestrationOptions { Mode = AgentExecutionMode.Sequential },
            cancellationToken);

        var projected = run.AssessmentResult;

        // Fatal only when a REQUIRED output is missing (parity with the single-pass flow, which throws
        // on Cosmos / SQL / Data Factory failure but not on data-quality failure or a consistency-only
        // validator finding). ToAssessmentResult() default-constructs missing required outputs, so
        // completeness is read from the validator's report (IsComplete), not from null checks. A missing
        // report (validator never emitted one) is treated as fatal since the result can't be trusted.
        var report = run.Validation;
        if (report is not { IsComplete: true })
        {
            var missing = report is { MissingRequiredOutputs.Count: > 0 }
                ? string.Join(", ", report.MissingRequiredOutputs)
                : "Cosmos analysis, SQL assessment, or Data Factory estimate";
            _logger.LogError("Agentic assessment incomplete for database {DatabaseName}: missing {Missing}", databaseName, missing);
            throw new InvalidOperationException(
                $"Agentic assessment failed for database {databaseName}: missing required output(s): {missing}.");
        }

        assessmentResult.CosmosAnalysis = projected.CosmosAnalysis;
        assessmentResult.SqlAssessment = projected.SqlAssessment;
        assessmentResult.DataQualityAnalysis = projected.DataQualityAnalysis;
        assessmentResult.DataFactoryEstimate = projected.DataFactoryEstimate;

        Console.WriteLine($"   ✅ Analyzed {assessmentResult.CosmosAnalysis.Containers.Count} containers");
        Console.WriteLine($"   ✅ Recommended platform: {assessmentResult.SqlAssessment.RecommendedPlatform}");
        if (assessmentResult.DataQualityAnalysis is not null)
        {
            Console.WriteLine($"   ✅ Quality score: {assessmentResult.DataQualityAnalysis.Summary.OverallQualityScore:F1}/100 ({assessmentResult.DataQualityAnalysis.Summary.QualityRating})");
        }
        else
        {
            Console.WriteLine("   ⚠️  Data quality analysis unavailable (optional) - continuing");
        }
        Console.WriteLine($"   ✅ Estimated migration time: {assessmentResult.DataFactoryEstimate.EstimatedDuration:hh\\:mm\\:ss}");
        Console.WriteLine($"   {(run.IsAcceptable ? "✅ Validation: acceptable" : "⚠️  Validation: complete but with consistency warnings")}");

        return assessmentResult;
    }

    private async Task GenerateOutputsAsync(
        AssessmentResult assessmentResult,
        string outputDirectory,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();

        if (!options.ProjectOnly)
        {
            Console.WriteLine("📄 Phase 5a: Generating assessment reports...");

            var reportingService = _services.GetRequiredService<ReportGenerationService>();

            try
            {
                var (excelPaths, wordPath, analysisFolderPath) = await reportingService.GenerateAssessmentReportAsync(assessmentResult, outputDirectory, cancellationToken);

                foreach (var excelPath in excelPaths)
                {
                    Console.WriteLine($"   ✅ Excel report: {excelPath}");
                }
                Console.WriteLine($"   ✅ Word summary: {wordPath}");

                assessmentResult.AnalysisFolderPath = analysisFolderPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate assessment reports");
                Console.WriteLine($"   ❌ Assessment report generation failed: {ex.Message}");
                throw;
            }
        }

        if (!options.AssessmentOnly)
        {
            Console.WriteLine("🏗️  Phase 5b: Generating SQL database projects...");

            var sqlProjectService = _services.GetRequiredService<SqlProjectGenerationService>();

            try
            {
                var analysisFolderPath = !string.IsNullOrEmpty(assessmentResult.AnalysisFolderPath)
                    ? assessmentResult.AnalysisFolderPath
                    : outputDirectory;

                await sqlProjectService.GenerateSqlProjectsAsync(assessmentResult, analysisFolderPath, cancellationToken);

                foreach (var databaseMapping in assessmentResult.SqlAssessment.DatabaseMappings)
                {
                    var projectName = $"{SanitizeName(databaseMapping.TargetDatabase)}.Database";
                    var projectPath = Path.Combine(analysisFolderPath, "sql-projects", projectName);
                    Console.WriteLine($"   ✅ SQL project: {projectPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SQL projects");
                Console.WriteLine($"   ❌ SQL project generation failed: {ex.Message}");
                throw;
            }
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Database";

        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '-', '.' }).ToArray();
        var sanitized = name;

        foreach (var invalidChar in invalidChars)
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        if (!char.IsLetter(sanitized[0]) && sanitized[0] != '_')
        {
            sanitized = "DB_" + sanitized;
        }

        return sanitized;
    }

    private static CosmosDbAnalysis CombineCosmosAnalyses(List<CosmosDbAnalysis> analyses)
    {
        var combined = new CosmosDbAnalysis
        {
            Containers = new List<ContainerAnalysis>(),
            MonitoringLimitations = new List<string>(),
            DatabaseMetrics = new DatabaseMetrics()
        };

        foreach (var analysis in analyses)
        {
            combined.Containers.AddRange(analysis.Containers);
            combined.MonitoringLimitations.AddRange(analysis.MonitoringLimitations);
        }

        return combined;
    }

    private static SqlMigrationAssessment CombineSqlAssessments(List<SqlMigrationAssessment> assessments)
    {
        var combined = new SqlMigrationAssessment
        {
            RecommendedPlatform = assessments.FirstOrDefault()?.RecommendedPlatform ?? "Azure SQL Database",
            IndexRecommendations = new List<IndexRecommendation>(),
            Complexity = new MigrationComplexity(),
            DatabaseMappings = new List<DatabaseMapping>()
        };

        foreach (var assessment in assessments)
        {
            combined.IndexRecommendations.AddRange(assessment.IndexRecommendations);
            combined.DatabaseMappings.AddRange(assessment.DatabaseMappings);
        }

        var complexities = assessments.Select(a => a.Complexity.OverallComplexity).ToList();
        if (complexities.Contains("High"))
            combined.Complexity.OverallComplexity = "High";
        else if (complexities.Contains("Medium"))
            combined.Complexity.OverallComplexity = "Medium";
        else
            combined.Complexity.OverallComplexity = "Low";

        return combined;
    }

    private static DataFactoryEstimate CombineDataFactoryEstimates(List<DataFactoryEstimate> estimates)
    {
        return new DataFactoryEstimate
        {
            EstimatedDuration = TimeSpan.FromMilliseconds(estimates.Sum(e => e.EstimatedDuration.TotalMilliseconds)),
            EstimatedCostUSD = estimates.Sum(e => e.EstimatedCostUSD),
            RecommendedDIUs = estimates.Max(e => e.RecommendedDIUs),
            RecommendedParallelCopies = estimates.Max(e => e.RecommendedParallelCopies),
            TotalDataSizeGB = estimates.Sum(e => e.TotalDataSizeGB),
            PipelineEstimates = estimates.SelectMany(e => e.PipelineEstimates).ToList(),
            Prerequisites = estimates.SelectMany(e => e.Prerequisites).Distinct().ToList(),
            Recommendations = estimates.SelectMany(e => e.Recommendations).Distinct().ToList()
        };
    }

    private static void DisplayCompletionMessage(AssessmentResult assessment, CliOptions options)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("                Assessment Complete!");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("📋 Assessment Summary:");
        Console.WriteLine($"   • Database: {assessment.DatabaseName}");
        Console.WriteLine($"   • Containers: {assessment.CosmosAnalysis.Containers.Count}");
        Console.WriteLine($"   • Total Documents: {assessment.CosmosAnalysis.Containers.Sum(c => c.DocumentCount):N0}");
        Console.WriteLine($"   • Total Size: {assessment.CosmosAnalysis.Containers.Sum(c => c.SizeBytes) / (1024.0 * 1024.0 * 1024.0):F2} GB");
        Console.WriteLine();
        Console.WriteLine("🎯 Migration Recommendations:");
        Console.WriteLine($"   • Recommended Platform: {assessment.SqlAssessment.RecommendedPlatform}");
        Console.WriteLine($"   • Recommended Tier: {assessment.SqlAssessment.RecommendedTier}");
        Console.WriteLine($"   • Complexity: {assessment.SqlAssessment.Complexity.OverallComplexity}");
        Console.WriteLine($"   • Estimated Migration Days: {assessment.SqlAssessment.Complexity.EstimatedMigrationDays}");
        Console.WriteLine();
        Console.WriteLine("🏭 Data Factory Estimates:");
        Console.WriteLine($"   • Migration Duration: {assessment.DataFactoryEstimate.EstimatedDuration.TotalHours:F1} hours");
        Console.WriteLine($"   • Estimated Cost: ${assessment.DataFactoryEstimate.EstimatedCostUSD:F2}");
        Console.WriteLine($"   • Recommended DIUs: {assessment.DataFactoryEstimate.RecommendedDIUs}");
        Console.WriteLine();

        Console.WriteLine("📊 Generated Outputs:");
        if (!options.ProjectOnly)
        {
            Console.WriteLine("   • Excel reports: Detailed analysis and recommendations");
            Console.WriteLine("   • Word summary: Executive overview for stakeholders");
        }
        if (!options.AssessmentOnly)
        {
            Console.WriteLine("   • SQL Database projects: Ready for SSDT/SqlPackage deployment");
            Console.WriteLine($"   • {assessment.SqlAssessment.DatabaseMappings.Count} project(s) in sql-projects/ directory");
        }
        Console.WriteLine();

        Console.WriteLine("📊 Next Steps:");
        if (!options.ProjectOnly)
        {
            Console.WriteLine("   1. Review the generated Excel report for detailed analysis");
            Console.WriteLine("   2. Share the Word document with stakeholders");
        }
        if (!options.AssessmentOnly)
        {
            Console.WriteLine("   3. Deploy SQL projects using Visual Studio or SqlPackage.exe");
            Console.WriteLine("   4. Test schema with sample data");
        }
        Console.WriteLine("   5. Plan migration based on complexity assessment");
        Console.WriteLine("   6. Provision target Azure SQL infrastructure");
        Console.WriteLine("   7. Execute proof-of-concept migration if complexity is high");
        Console.WriteLine();
    }

    private async Task<UserInputs?> GetUserInputsAsync(CliOptions options)
    {
        var inputs = new UserInputs();

        try
        {
            inputs.UseAgentic = options.Agentic;

            inputs.AccountEndpoint = !string.IsNullOrEmpty(options.AccountEndpoint)
                ? options.AccountEndpoint
                : _configuration["CosmosDb:AccountEndpoint"] ?? string.Empty;

            if (string.IsNullOrEmpty(inputs.AccountEndpoint))
            {
                Console.WriteLine("❌ Cosmos DB account endpoint not specified.");
                Console.WriteLine("   Use --endpoint <url> or configure in appsettings.json");
                Console.WriteLine();
                CliArgumentParser.DisplayHelp();
                return null;
            }

            Console.WriteLine($"🔗 Using Cosmos DB endpoint: {inputs.AccountEndpoint}");

            if (options.AnalyzeAllDatabases)
            {
                Console.WriteLine("🔍 Discovering all databases in Cosmos DB account...");
                inputs.DatabaseNames = await DiscoverAllDatabasesAsync(inputs.AccountEndpoint);

                if (!inputs.DatabaseNames.Any())
                {
                    Console.WriteLine("❌ No databases found in the Cosmos DB account.");
                    return null;
                }

                Console.WriteLine($"✅ Found {inputs.DatabaseNames.Count} databases:");
                foreach (var db in inputs.DatabaseNames)
                {
                    Console.WriteLine($"   • {db}");
                }
                Console.WriteLine();
            }
            else if (!string.IsNullOrEmpty(options.DatabaseName))
            {
                inputs.DatabaseNames = new List<string> { options.DatabaseName };
            }
            else
            {
                var configuredDatabase = _configuration["CosmosDb:DatabaseName"];
                if (!string.IsNullOrEmpty(configuredDatabase))
                {
                    inputs.DatabaseNames = new List<string> { configuredDatabase };
                }
                else
                {
                    Console.WriteLine("❌ No database specified. Use --database <name> or configure in appsettings.json");
                    return null;
                }
            }

            var outputDir = GetOutputDirectoryAsync(options);
            if (string.IsNullOrEmpty(outputDir))
            {
                Console.WriteLine("❌ No output directory specified.");
                return null;
            }
            inputs.OutputDirectory = outputDir;

            if (options.AutoDiscoverMonitoring || _configuration.GetValue<bool>("AzureMonitor:AutoDiscover"))
            {
                Console.WriteLine("🔍 Auto-discovering Azure Monitor settings...");
                inputs.MonitoringConfig = await AutoDiscoverMonitoringAsync();
            }

            return inputs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user inputs");
            Console.WriteLine($"❌ Error getting user inputs: {ex.Message}");
            return null;
        }
    }

    private async Task<List<string>> DiscoverAllDatabasesAsync(string cosmosEndpoint)
    {
        var databases = new List<string>();

        try
        {
            var credential = new DefaultAzureCredential();

            if (string.IsNullOrEmpty(cosmosEndpoint))
            {
                throw new ArgumentException("Cosmos DB account endpoint not configured");
            }

            using var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

            var iterator = cosmosClient.GetDatabaseQueryIterator<Microsoft.Azure.Cosmos.DatabaseProperties>();

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var db in response)
                {
                    databases.Add(db.Id);
                }
            }

            _logger.LogInformation("Discovered {DatabaseCount} databases", databases.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover databases");
            throw;
        }

        return databases;
    }

    private string? GetOutputDirectoryAsync(CliOptions options)
    {
        if (!string.IsNullOrEmpty(options.OutputDirectory))
        {
            return Path.GetFullPath(options.OutputDirectory);
        }

        var configDirectory = _configuration["Reports:OutputDirectory"];
        var promptForDirectory = _configuration.GetValue<bool>("Reports:PromptForOutputDirectory");

        if (!string.IsNullOrEmpty(configDirectory) && !promptForDirectory)
        {
            return Path.GetFullPath(configDirectory);
        }

        Console.WriteLine("📁 Please specify the output directory for reports:");
        Console.WriteLine("   (Press Enter to use default: ./CosmosAssessment_{timestamp})");
        Console.Write("   Output Directory: ");

        var userInput = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(userInput))
        {
            var pattern = _configuration["Reports:DefaultDirectoryPattern"] ?? "CosmosAssessment_{DateTime:yyyyMMdd_HHmmss}";
            var defaultDir = pattern.Replace("{DateTime:yyyyMMdd_HHmmss}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            userInput = Path.Combine(Environment.CurrentDirectory, defaultDir);
        }

        var fullPath = Path.GetFullPath(userInput);

        try
        {
            Directory.CreateDirectory(fullPath);
            Console.WriteLine($"✅ Output directory: {fullPath}");
            return fullPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to create directory '{fullPath}': {ex.Message}");
            return null;
        }
    }

    private Task<MonitoringConfiguration?> AutoDiscoverMonitoringAsync()
    {
        try
        {
            _logger.LogInformation("Auto-discovery of monitoring settings is not fully implemented yet");
            Console.WriteLine("⚠️  Auto-discovery of Azure Monitor settings is not yet implemented.");
            Console.WriteLine("   Using configuration file or default settings.");

            return Task.FromResult<MonitoringConfiguration?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-discover monitoring settings");
            return Task.FromResult<MonitoringConfiguration?>(null);
        }
    }

    /// <summary>
    /// Tests connectivity to Cosmos DB and Azure Monitor (if configured).
    /// </summary>
    private async Task<int> TestConnectionAsync(CliOptions options)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("          Connection Test");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine();

        var allTestsPassed = true;

        Console.WriteLine("🔍 Testing Cosmos DB connectivity...");

        var cosmosEndpoint = !string.IsNullOrEmpty(options.AccountEndpoint)
            ? options.AccountEndpoint
            : _configuration["CosmosDb:AccountEndpoint"];

        if (string.IsNullOrEmpty(cosmosEndpoint))
        {
            Console.WriteLine("❌ Cosmos DB endpoint not configured in appsettings.json");
            Console.WriteLine("   Configure CosmosDb:AccountEndpoint or use --endpoint parameter");
            allTestsPassed = false;
        }
        else
        {
            try
            {
                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ExcludeEnvironmentCredential = false,
                    ExcludeWorkloadIdentityCredential = false,
                    ExcludeManagedIdentityCredential = false,
                    ExcludeInteractiveBrowserCredential = false,
                    ExcludeAzureCliCredential = false,
                    ExcludeAzurePowerShellCredential = false,
                    ExcludeAzureDeveloperCliCredential = false
                });

                using var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

                var iterator = cosmosClient.GetDatabaseQueryIterator<Microsoft.Azure.Cosmos.DatabaseProperties>();
                var databaseCount = 0;

                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    databaseCount += response.Count;
                }

                Console.WriteLine($"✅ Cosmos DB connection successful");
                Console.WriteLine($"   • Endpoint: {cosmosEndpoint}");
                Console.WriteLine($"   • Databases found: {databaseCount}");
                _logger.LogInformation("Cosmos DB connection test passed. Found {DatabaseCount} databases", databaseCount);
            }
            catch (CosmosException ex)
            {
                Console.WriteLine($"❌ Cosmos DB connection failed (Cosmos error): {ex.Message}");
                _logger.LogError(ex, "Cosmos DB connection test failed with a CosmosException");
                allTestsPassed = false;

                Console.WriteLine();
                Console.WriteLine("   Troubleshooting tips:");
                Console.WriteLine("   • Verify the endpoint URL is correct");
                Console.WriteLine("   • Ensure you have the appropriate Azure credentials and access to the Cosmos DB account");
                Console.WriteLine("   • Check that the Cosmos DB account is reachable from your network");
                Console.WriteLine("   • Confirm you have read permissions on the Cosmos DB account");
            }
            catch (AuthenticationFailedException ex)
            {
                Console.WriteLine($"❌ Cosmos DB connection failed (authentication error): {ex.Message}");
                _logger.LogError(ex, "Cosmos DB connection test failed due to authentication error");
                allTestsPassed = false;

                Console.WriteLine();
                Console.WriteLine("   Troubleshooting tips:");
                Console.WriteLine("   • Ensure you have the appropriate Azure credentials");
                Console.WriteLine("   • If using managed identity, verify it is enabled and has access to the Cosmos DB account");
                Console.WriteLine("   • Try running 'az login' if using Azure CLI authentication");
                Console.WriteLine("   • Verify that your credentials have not expired or been revoked");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Cosmos DB connection failed due to an unexpected error: {ex.Message}");
                _logger.LogError(ex, "Cosmos DB connection test failed with an unexpected error");
                allTestsPassed = false;

                Console.WriteLine();
                Console.WriteLine("   Troubleshooting tips:");
                Console.WriteLine("   • Check the full error details in the logs for more information");
                Console.WriteLine("   • Verify the endpoint URL and Azure credentials");
                Console.WriteLine("   • Ensure network connectivity to the Cosmos DB endpoint");
            }
        }

        Console.WriteLine();

        Console.WriteLine("🔍 Testing Azure Monitor connectivity...");

        var workspaceId = !string.IsNullOrEmpty(options.WorkspaceId)
            ? options.WorkspaceId
            : _configuration["AzureMonitor:WorkspaceId"];

        if (string.IsNullOrEmpty(workspaceId))
        {
            Console.WriteLine("⚠️  Azure Monitor workspace ID not configured");
            Console.WriteLine("   Performance metrics will be limited without Azure Monitor");
            Console.WriteLine("   Configure AzureMonitor:WorkspaceId in appsettings.json or use --workspace-id parameter");
        }
        else
        {
            try
            {
                var credential = new DefaultAzureCredential();
                var logsQueryClient = new LogsQueryClient(credential);

                var queryResult = await logsQueryClient.QueryWorkspaceAsync(
                    workspaceId,
                    AzureMonitorTestQuery,
                    new QueryTimeRange(TimeSpan.FromDays(AzureMonitorTestQueryDays)));

                if (queryResult.Value.Status == LogsQueryResultStatus.Success)
                {
                    Console.WriteLine($"✅ Azure Monitor connection successful");
                    Console.WriteLine($"   • Workspace ID: {workspaceId}");
                    _logger.LogInformation("Azure Monitor connection test passed for workspace {WorkspaceId}", workspaceId);
                }
                else
                {
                    Console.WriteLine($"⚠️  Azure Monitor query returned status: {queryResult.Value.Status}");
                    Console.WriteLine("   Connection successful but query may need adjustment");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Azure Monitor connection failed: {ex.Message}");
                _logger.LogError(ex, "Azure Monitor connection test failed");
                allTestsPassed = false;

                Console.WriteLine();
                Console.WriteLine("   Troubleshooting tips:");
                Console.WriteLine("   • Verify the workspace ID is correct (should be a GUID)");
                Console.WriteLine("   • Ensure you have Log Analytics Reader permissions");
                Console.WriteLine("   • Check that diagnostic logs are enabled for Cosmos DB");
                Console.WriteLine("   • Try running 'az login' if using Azure CLI authentication");
            }
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");

        if (allTestsPassed)
        {
            Console.WriteLine("✅ All connection tests passed!");
            Console.WriteLine();
            Console.WriteLine("You can now run the assessment tool with your configured settings.");
            _logger.LogInformation("All connection tests passed successfully");
            return 0;
        }
        else
        {
            Console.WriteLine("❌ Some connection tests failed");
            Console.WriteLine();
            Console.WriteLine("Please review the errors above and fix the configuration before running the assessment.");
            _logger.LogWarning("Some connection tests failed");
            return 1;
        }
    }

    /// <summary>
    /// Runs the <c>migration status</c> subcommand (#225): resolves the
    /// <see cref="MigrationStatusService"/> and renders live progress to the console.
    /// </summary>
    private async Task<int> RunMigrationStatusAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var statusService = _services.GetRequiredService<MigrationStatusService>();
        var reportOptions = new MigrationStatusReportOptions
        {
            Watch = options.Watch,
            PollIntervalSeconds = options.PollIntervalSeconds,
        };

        return await statusService.RunAsync(reportOptions, Console.Out, cancellationToken);
    }

    /// <summary>
    /// Generates SQL Database Project from assessment results.
    /// </summary>
    private async Task GenerateSqlProjectAsync(
        AssessmentResult assessmentResult,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("🏗️ Generating SQL Database Project...");

            var sqlProjectService = _services.GetRequiredService<SqlProjectIntegrationService>();

            var projectOptions = SqlProjectOptions.CreateDefault();
            projectOptions.OutputPath = Path.Combine(outputDirectory, "SqlDatabaseProject");
            projectOptions.ProjectName = $"{assessmentResult.DatabaseName}_Migration";

            var sqlProjectResult = await sqlProjectService.GenerateSqlProjectAsync(
                assessmentResult.SqlAssessment,
                projectOptions,
                cancellationToken);

            if (sqlProjectResult.Success)
            {
                Console.WriteLine("✅ SQL Database Project generated successfully!");
                Console.WriteLine($"   📁 Location: {sqlProjectResult.Project?.OutputPath}");
                Console.WriteLine($"   📊 Files created: {sqlProjectResult.Project?.TotalFileCount}");
                Console.WriteLine($"   ⏱️ Generation time: {sqlProjectResult.Duration.TotalSeconds:F2} seconds");

                if (sqlProjectResult.Warnings.Any())
                {
                    Console.WriteLine();
                    Console.WriteLine("⚠️ Warnings:");
                    foreach (var warning in sqlProjectResult.Warnings)
                    {
                        Console.WriteLine($"   • {warning}");
                    }
                }

                if (sqlProjectResult.Project?.Metadata.ManualInterventionRequired.Any() == true)
                {
                    Console.WriteLine();
                    Console.WriteLine("📝 Manual intervention required:");
                    foreach (var note in sqlProjectResult.Project.Metadata.ManualInterventionRequired)
                    {
                        Console.WriteLine($"   • {note}");
                    }
                }

                _logger.LogInformation("SQL Database Project generated successfully: {ProjectPath}",
                    sqlProjectResult.Project?.ProjectFilePath);
            }
            else
            {
                Console.WriteLine($"❌ SQL Database Project generation failed: {sqlProjectResult.Error}");
                _logger.LogError("SQL Database Project generation failed: {Error}", sqlProjectResult.Error);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error generating SQL Database Project: {ex.Message}");
            _logger.LogError(ex, "Unexpected error during SQL Database Project generation");
        }
    }

    /// <summary>
    /// Generates ready-to-deploy Azure Data Factory artifacts (linked services, datasets,
    /// pipelines) for the assessed migration. Foundational sub-issue #141 of parent #70.
    /// </summary>
    private async Task GenerateDataFactoryArtifactsAsync(
        AssessmentResult assessmentResult,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("🏭 Generating Azure Data Factory pipeline artifacts...");

            var adfGenerator = _services.GetRequiredService<IDataFactoryPipelineGenerator>();
            var adfOutputRoot = !string.IsNullOrEmpty(assessmentResult.AnalysisFolderPath)
                ? assessmentResult.AnalysisFolderPath
                : outputDirectory;

            var result = await adfGenerator.GenerateAsync(
                assessmentResult,
                adfOutputRoot,
                options: null,
                cancellationToken);

            Console.WriteLine($"   ✅ Pipelines: {result.PipelineCount} | Copy activities: {result.CopyActivityCount} | Datasets: {result.DatasetCount} | Linked services: {result.LinkedServiceCount}");
            Console.WriteLine($"   📁 Location: {Path.Combine(adfOutputRoot, "ADF")}");

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("⚠️  ADF generation warnings:");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"   • {warning}");
                }
            }

            _logger.LogInformation(
                "ADF pipeline artifacts generated: {Pipelines} pipeline(s), {CopyActivities} copy activity(ies), {Datasets} dataset(s).",
                result.PipelineCount, result.CopyActivityCount, result.DatasetCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error generating ADF pipeline artifacts: {ex.Message}");
            _logger.LogError(ex, "Unexpected error during ADF pipeline artifact generation");
        }
    }
}
