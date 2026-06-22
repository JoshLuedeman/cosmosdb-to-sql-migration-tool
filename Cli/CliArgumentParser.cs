namespace CosmosToSqlAssessment.Cli;

using CosmosToSqlAssessment.Agents;

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
                case "migration":
                    // `migration` subcommands for live monitoring / alerting operations. Additive:
                    // leaves all existing flags/commands intact. Supported: status (#225),
                    // generate-alerts (#256), publish-metrics (#257).
                    {
                        var sub = i + 1 < args.Length ? args[i + 1] : null;
                        if (string.Equals(sub, "status", StringComparison.OrdinalIgnoreCase))
                        {
                            options.MigrationStatus = true;
                            i++; // consume the sub-token
                        }
                        else if (string.Equals(sub, "generate-alerts", StringComparison.OrdinalIgnoreCase))
                        {
                            options.GenerateAlerts = true;
                            i++;
                        }
                        else if (string.Equals(sub, "publish-metrics", StringComparison.OrdinalIgnoreCase))
                        {
                            options.PublishMetrics = true;
                            i++;
                        }
                        else
                        {
                            output.WriteLine("Unknown command: 'migration'. Did you mean 'migration status', 'migration generate-alerts', or 'migration publish-metrics'?");
                            DisplayHelp(output);
                            return null;
                        }
                    }
                    break;
                case "feedback":
                    // `feedback record` subcommand to import an anonymized migration outcome (#259).
                    if (i + 1 < args.Length && string.Equals(args[i + 1], "record", StringComparison.OrdinalIgnoreCase))
                    {
                        options.FeedbackRecord = true;
                        i++; // consume the "record" sub-token
                    }
                    else
                    {
                        output.WriteLine("Unknown command: 'feedback'. Did you mean 'feedback record'?");
                        DisplayHelp(output);
                        return null;
                    }
                    break;
                case "--agentic-mode":
                    // Scheduling mode for the agentic orchestration layer (#248). Restricted to the closed
                    // set of AgentExecutionMode names; numeric/garbage values are rejected explicitly.
                    if (i + 1 < args.Length &&
                        Enum.GetNames<AgentExecutionMode>().Any(n => string.Equals(n, args[i + 1], StringComparison.OrdinalIgnoreCase)))
                    {
                        options.AgenticMode = Enum.Parse<AgentExecutionMode>(args[++i], ignoreCase: true);
                        options.AgenticModeSpecified = true;
                    }
                    else
                    {
                        output.WriteLine("Invalid value for --agentic-mode: expected one of sequential, parallel, conditional.");
                        DisplayHelp(output);
                        return null;
                    }
                    break;
                case "--import-outcome":
                    if (i + 1 < args.Length)
                    {
                        options.ImportOutcomeFile = args[++i];
                    }
                    break;
                case "--watch":
                    options.Watch = true;
                    break;
                case "--poll-interval":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var pollSeconds) && pollSeconds > 0)
                    {
                        options.PollIntervalSeconds = pollSeconds;
                        i++;
                    }
                    else
                    {
                        output.WriteLine("Invalid value for --poll-interval: expected a positive integer (seconds).");
                        DisplayHelp(output);
                        return null;
                    }
                    break;
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
                case "--skip-auto-discovery":
                    options.SkipAutoDiscovery = true;
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
                case "--agentic":
                    options.Agentic = true;
                    break;
                case "--enable-feedback":
                    options.EnableFeedback = true;
                    break;
                case "--disable-feedback":
                    options.DisableFeedback = true;
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

        if (options.EnableFeedback && options.DisableFeedback)
        {
            output.WriteLine("❌ Error: Cannot specify both --enable-feedback and --disable-feedback flags.");
            output.WriteLine("   Use one or the other; feedback collection is opt-in and disabled by default.");
            return false;
        }

        // At most one operational subcommand may be requested per invocation. The parser sets these
        // independently, so guard against combinations like `migration status migration generate-alerts`
        // or pairing a subcommand with --test-connection (which would otherwise be silently ignored).
        var operationalCommands = new (bool Set, string Name)[]
        {
            (options.MigrationStatus, "migration status"),
            (options.GenerateAlerts, "migration generate-alerts"),
            (options.PublishMetrics, "migration publish-metrics"),
            (options.FeedbackRecord, "feedback record"),
            (options.TestConnection, "--test-connection"),
        };
        var requested = operationalCommands.Where(c => c.Set).Select(c => c.Name).ToList();
        if (requested.Count > 1)
        {
            output.WriteLine($"❌ Error: Cannot combine the commands: {string.Join(", ", requested)}.");
            output.WriteLine("   Run one operation at a time.");
            return false;
        }

        // --agentic-mode only has an effect on the agentic path; specifying it without --agentic is
        // almost certainly a mistake (the chosen mode would be silently ignored).
        if (options.AgenticModeSpecified && !options.Agentic)
        {
            output.WriteLine("❌ Error: --agentic-mode requires --agentic.");
            output.WriteLine("   Add --agentic to run through the multi-agent orchestration layer, or omit --agentic-mode.");
            return false;
        }

        // `migration generate-alerts` writes its templates to an explicit output directory.
        if (options.GenerateAlerts && string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            output.WriteLine("❌ Error: 'migration generate-alerts' requires --output <dir>.");
            output.WriteLine("   Specify the directory the alert-rule ARM templates should be written to.");
            return false;
        }

        // `feedback record` imports a serialized outcome file; the file argument is mandatory and only
        // meaningful together with the subcommand.
        if (options.FeedbackRecord && string.IsNullOrWhiteSpace(options.ImportOutcomeFile))
        {
            output.WriteLine("❌ Error: 'feedback record' requires --import-outcome <file.json>.");
            output.WriteLine("   Provide a JSON file holding the anonymized migration outcome to record.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ImportOutcomeFile) && !options.FeedbackRecord)
        {
            output.WriteLine("❌ Error: --import-outcome is only valid with the 'feedback record' command.");
            output.WriteLine("   Use: feedback record --import-outcome <file.json>.");
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
        output.WriteLine("  --skip-auto-discovery     Skip automatic Azure Monitor discovery (use manual config)");
        output.WriteLine("  --assessment-only         Generate assessment reports only (skip SQL project generation)");
        output.WriteLine("  --project-only            Generate SQL projects only (skip assessment reports)");
        output.WriteLine("  --test-connection         Test connectivity to Cosmos DB and Azure Monitor");
        output.WriteLine("  -i, --interactive         Launch interactive wizard mode for guided configuration");
        output.WriteLine("  --agentic                 Run the assessment via the multi-agent orchestration layer (equivalent output)");
        output.WriteLine("  --agentic-mode <mode>     Agentic scheduling mode: sequential (default), parallel, or conditional (requires --agentic)");
        output.WriteLine("  --enable-feedback         Opt in to anonymized continuous-learning feedback (default: off)");
        output.WriteLine("  --disable-feedback        Explicitly opt out of feedback collection (overrides config)");
        output.WriteLine("  -c, --config <path>       Load configuration from a saved JSON file");
        output.WriteLine("  --save-config <path>      Save wizard configuration to a JSON file for reuse");
        output.WriteLine("  --resume                  Resume an interrupted interactive wizard session");
        output.WriteLine();
        output.WriteLine("Subcommands:");
        output.WriteLine("  migration status          Report live migration progress (rows migrated, RU consumption, error rate)");
        output.WriteLine("    --watch                 Continuously poll and re-render progress until cancelled");
        output.WriteLine("    --poll-interval <sec>   Polling interval in seconds for --watch (default 10)");
        output.WriteLine("  migration generate-alerts Generate Azure Monitor alert-rule ARM templates");
        output.WriteLine("    --output <dir>          Directory the alert-rule templates are written to (required)");
        output.WriteLine("  migration publish-metrics Stream ADF activity progress as live Azure Monitor custom metrics");
        output.WriteLine("    --watch                 Continuously poll and publish until cancelled");
        output.WriteLine("    --poll-interval <sec>   Polling interval in seconds for --watch (default 10)");
        output.WriteLine("  feedback record           Import an anonymized migration outcome into the local feedback store (opt-in)");
        output.WriteLine("    --import-outcome <file> JSON file holding the anonymized migration outcome (required)");
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
        output.WriteLine("  CosmosToSqlAssessment migration status");
        output.WriteLine("  CosmosToSqlAssessment migration status --watch --poll-interval 5");
        output.WriteLine("  CosmosToSqlAssessment migration generate-alerts --output C:\\Reports");
        output.WriteLine("  CosmosToSqlAssessment migration publish-metrics --watch");
        output.WriteLine("  CosmosToSqlAssessment feedback record --import-outcome outcome.json --enable-feedback");
        output.WriteLine("  CosmosToSqlAssessment --agentic --agentic-mode parallel --database MyDatabase");
        output.WriteLine();
    }
}
