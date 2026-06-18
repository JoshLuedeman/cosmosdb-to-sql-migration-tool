using CosmosToSqlAssessment.Cli;

namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Orchestrates the interactive wizard flow, prompting the user step-by-step
/// to configure the assessment and returning a populated <see cref="CliOptions"/>.
/// </summary>
internal sealed class WizardRunner
{
    private readonly IWizardConsole _console;

    public WizardRunner(IWizardConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
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
        options.AccountEndpoint = _console.Prompt(
            "Cosmos DB account endpoint (e.g. https://myaccount.documents.azure.com:443/)");

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
            options.DatabaseName = _console.Prompt("Database name");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: Log Analytics workspace (optional)
        _console.WriteLine();
        _console.WriteInfo("── Step 3: Azure Monitor (Optional) ──");
        var useMonitor = _console.Confirm("Include Azure Monitor performance metrics?", true);

        if (useMonitor)
        {
            options.WorkspaceId = _console.Prompt("Log Analytics workspace ID");
            options.AutoDiscoverMonitoring = _console.Confirm("Auto-discover monitoring settings?", false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 5: Output directory
        _console.WriteLine();
        _console.WriteInfo("── Step 4: Output Configuration ──");
        options.OutputDirectory = _console.Prompt("Output directory for reports", "./output");

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

        return options;
    }
}
