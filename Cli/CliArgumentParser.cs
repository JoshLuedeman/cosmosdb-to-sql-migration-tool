namespace CosmosToSqlAssessment.Cli;

/// <summary>
/// Parses raw <c>string[]</c> command-line arguments into a <see cref="CliOptions"/>
/// instance and emits help / validation messages.
///
/// <para>
/// All output is routed through a caller-supplied <see cref="TextWriter"/> (defaulting
/// to <see cref="Console.Out"/>) so the parser is fully unit-testable without
/// capturing console output.
/// </para>
///
/// <para>
/// Behavior is intentionally preserved 1-for-1 with the previous in-line parser
/// in <c>Program.cs</c> (extracted as part of issue #186 / parent #126). Pre-existing
/// quirks — culture-sensitive <c>ToLower()</c> and silent acceptance of options with
/// missing values — are deliberately retained; fixing them is out of scope here.
/// </para>
/// </summary>
internal static class CliArgumentParser
{
    /// <summary>
    /// Parses <paramref name="args"/> into a <see cref="CliOptions"/>.
    /// Returns <c>null</c> when the caller should exit with a non-success code:
    /// either help was displayed (<c>--help</c>/<c>-h</c>) or an unknown argument was encountered.
    /// </summary>
    public static CliOptions? Parse(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--help":
                case "-h":
                    DisplayHelp(output);
                    return null;
                case "--all-databases":
                case "-a":
                    options.AnalyzeAllDatabases = true;
                    break;
                case "--database":
                case "-d":
                    if (i + 1 < args.Length)
                    {
                        options.DatabaseName = args[++i];
                    }
                    break;
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        options.OutputDirectory = args[++i];
                    }
                    break;
                case "--auto-discover":
                    options.AutoDiscoverMonitoring = true;
                    break;
                case "--endpoint":
                case "-e":
                    if (i + 1 < args.Length)
                    {
                        options.AccountEndpoint = args[++i];
                    }
                    break;
                case "--workspace-id":
                case "-w":
                    if (i + 1 < args.Length)
                    {
                        options.WorkspaceId = args[++i];
                    }
                    break;
                case "--assessment-only":
                    options.AssessmentOnly = true;
                    break;
                case "--project-only":
                    options.ProjectOnly = true;
                    break;
                case "--test-connection":
                    options.TestConnection = true;
                    break;
                case "--interactive":
                case "-i":
                    options.Interactive = true;
                    break;
                case "--config":
                case "-c":
                    if (i + 1 < args.Length)
                    {
                        options.ConfigFile = args[++i];
                    }
                    break;
                case "--save-config":
                    if (i + 1 < args.Length)
                    {
                        options.SaveConfigFile = args[++i];
                    }
                    break;
                case "--resume":
                    options.ResumeSession = true;
                    break;
                default:
                    output.WriteLine($"Unknown argument: {args[i]}");
                    DisplayHelp(output);
                    return null;
            }
        }

        return options;
    }

    /// <summary>
    /// Validates cross-flag invariants that <see cref="Parse"/> can't detect locally.
    /// Returns <c>false</c> if the caller should exit with a non-success code.
    /// </summary>
    public static bool Validate(CliOptions options, TextWriter? output = null)
    {
        output ??= Console.Out;

        if (options.AssessmentOnly && options.ProjectOnly)
        {
            output.WriteLine("❌ Error: Cannot specify both --assessment-only and --project-only flags.");
            output.WriteLine("   Use one or the other, or omit both for default behavior (generate both).");
            return false;
        }

        if (options.Interactive && options.TestConnection)
        {
            output.WriteLine("❌ Error: Cannot specify both --interactive and --test-connection flags.");
            output.WriteLine("   Use --test-connection for a quick connectivity diagnostic, or --interactive for guided wizard mode.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the help/usage block to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    public static void DisplayHelp(TextWriter? output = null)
    {
        output ??= Console.Out;

        output.WriteLine();
        output.WriteLine("Cosmos DB to SQL Migration Assessment Tool");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  CosmosToSqlAssessment [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  -h, --help                Show this help message");
        output.WriteLine("  -a, --all-databases       Analyze all databases in the Cosmos DB account");
        output.WriteLine("  -d, --database <name>     Analyze specific database (overrides config)");
        output.WriteLine("  -e, --endpoint <url>      Cosmos DB account endpoint (overrides config)");
        output.WriteLine("  -w, --workspace-id <id>   Log Analytics workspace ID for performance metrics");
        output.WriteLine("  -o, --output <path>       Output directory for reports (will prompt if not specified)");
        output.WriteLine("  --auto-discover           Automatically discover Azure Monitor settings");
        output.WriteLine("  --assessment-only         Generate assessment reports only (skip SQL project generation)");
        output.WriteLine("  --project-only            Generate SQL projects only (skip assessment reports)");
        output.WriteLine("  --test-connection         Test connectivity to Cosmos DB and Azure Monitor");
        output.WriteLine("  -i, --interactive         Launch interactive wizard mode for guided configuration");
        output.WriteLine("  -c, --config <path>       Load configuration from a saved JSON file");
        output.WriteLine("  --save-config <path>      Save wizard configuration to a JSON file for reuse");
        output.WriteLine("  --resume                  Resume an interrupted interactive wizard session");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  CosmosToSqlAssessment --all-databases");
        output.WriteLine("  CosmosToSqlAssessment --database MyDatabase --output C:\\Reports");
        output.WriteLine("  CosmosToSqlAssessment --endpoint https://myaccount.documents.azure.com:443/");
        output.WriteLine("  CosmosToSqlAssessment --endpoint https://myaccount.documents.azure.com:443/ --all-databases");
        output.WriteLine("  CosmosToSqlAssessment --workspace-id 12345678-1234-1234-1234-123456789012 --all-databases");
        output.WriteLine("  CosmosToSqlAssessment --auto-discover");
        output.WriteLine("  CosmosToSqlAssessment --assessment-only --database MyDatabase");
        output.WriteLine("  CosmosToSqlAssessment --project-only --all-databases");
        output.WriteLine("  CosmosToSqlAssessment --test-connection");
        output.WriteLine();
    }
}
