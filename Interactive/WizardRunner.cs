using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Orchestrates the interactive wizard flow, prompting the user step-by-step
/// to configure the assessment and returning a populated <see cref="CliOptions"/>.
/// </summary>
internal sealed class WizardRunner
{
    private readonly IWizardConsole _console;
    private readonly IConfigurationStore _configStore;

    public WizardRunner(IWizardConsole console, IConfigurationStore? configStore = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _configStore = configStore ?? new JsonConfigurationStore();
    }

    /// <summary>
    /// Runs the interactive wizard and returns configured <see cref="CliOptions"/>.
    /// </summary>
    public CliOptions Run(CancellationToken cancellationToken = default)
    {
        var options = new CliOptions { Interactive = true };

        // Step 1: Welcome
        _console.WriteLine();
        _console.WriteInfo("╔══════════════════════════════════════════════════════╗");
        _console.WriteInfo("║   Cosmos DB to SQL Migration Assessment - Wizard    ║");
        _console.WriteInfo("╚══════════════════════════════════════════════════════╝");
        _console.WriteLine();
        _console.WriteInfo("This wizard will guide you through configuring the assessment.");
        _console.WriteInfo("Press Enter to accept default values shown in [brackets].");
        _console.WriteLine();

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: Cosmos DB endpoint
        _console.WriteInfo("── Step 1: Cosmos DB Connection ──");
        options.AccountEndpoint = _console.PromptWithValidation(
            "Cosmos DB account endpoint (e.g. https://myaccount.documents.azure.com:443/)",
            InputValidators.ValidateEndpoint);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 3: Database selection
        _console.WriteLine();
        _console.WriteInfo("── Step 2: Database Selection ──");
        var dbMode = _console.Select("How would you like to select databases?",
            new[] { "Analyze all databases", "Specify a single database" });

        if (dbMode == "Analyze all databases")
        {
            options.AnalyzeAllDatabases = true;
        }
        else
        {
            options.DatabaseName = _console.PromptWithValidation(
                "Database name",
                InputValidators.ValidateDatabaseName);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: Log Analytics workspace (optional)
        _console.WriteLine();
        _console.WriteInfo("── Step 3: Azure Monitor (Optional) ──");
        var useMonitor = _console.Confirm("Include Azure Monitor performance metrics?", true);

        if (useMonitor)
        {
            options.WorkspaceId = _console.PromptWithValidation(
                "Log Analytics workspace ID",
                InputValidators.ValidateWorkspaceId);
            options.AutoDiscoverMonitoring = _console.Confirm("Auto-discover monitoring settings?", false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 5: Output directory
        _console.WriteLine();
        _console.WriteInfo("── Step 4: Output Configuration ──");
        options.OutputDirectory = _console.PromptWithValidation(
            "Output directory for reports",
            InputValidators.ValidateOutputDirectory,
            "./output");

        cancellationToken.ThrowIfCancellationRequested();

        // Step 6: Report type
        _console.WriteLine();
        _console.WriteInfo("── Step 5: Report Type ──");
        var reportType = _console.Select("What would you like to generate?",
            new[] { "Both assessment reports and SQL projects", "Assessment reports only", "SQL projects only" });

        switch (reportType)
        {
            case "Assessment reports only":
                options.AssessmentOnly = true;
                break;
            case "SQL projects only":
                options.ProjectOnly = true;
                break;
            // "Both" leaves both false (default behavior)
        }

        _console.WriteLine();

        cancellationToken.ThrowIfCancellationRequested();

        // Step 7: Summary confirmation
        DisplaySummary(options);

        if (!_console.Confirm("Proceed with this configuration?", true))
        {
            throw new OperationCanceledException("User declined to proceed after reviewing summary.");
        }

        // Offer to save configuration for reuse
        if (_console.Confirm("Save this configuration for future use?", false))
        {
            var savePath = _console.Prompt("Save path", "./wizard-config.json");
            var config = JsonConfigurationStore.FromCliOptions(options);
            _configStore.Save(config, savePath);
            _console.WriteInfo($"Configuration saved to: {savePath}");
        }

        _console.WriteLine();

        return options;
    }

    private void DisplaySummary(CliOptions options)
    {
        _console.WriteInfo("╔══════════════════════════════════════════════════════╗");
        _console.WriteInfo("║              Configuration Summary                  ║");
        _console.WriteInfo("╚══════════════════════════════════════════════════════╝");
        _console.WriteLine();
        _console.WriteInfo($"  Endpoint:       {options.AccountEndpoint ?? "(not set)"}");
        _console.WriteInfo($"  Database:       {(options.AnalyzeAllDatabases ? "All databases" : options.DatabaseName ?? "(not set)")}");
        _console.WriteInfo($"  Workspace ID:   {options.WorkspaceId ?? "(none)"}");
        _console.WriteInfo($"  Auto-discover:  {(options.AutoDiscoverMonitoring ? "Yes" : "No")}");
        _console.WriteInfo($"  Output dir:     {options.OutputDirectory ?? "(default)"}");

        var reportType = options.AssessmentOnly ? "Assessment only"
            : options.ProjectOnly ? "SQL projects only"
            : "Both (assessment + SQL projects)";
        _console.WriteInfo($"  Report type:    {reportType}");
        _console.WriteLine();
    }
}
