using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Services.Feedback;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services;

/// <summary>
/// Strongly-typed configuration for the continuous-learning feedback loop, bound from the
/// <c>FeedbackLoop</c> configuration section. Feedback is <b>off by default</b>.
/// </summary>
public sealed class FeedbackOptions
{
    /// <summary>The configuration section name these options bind from.</summary>
    public const string SectionName = "FeedbackLoop";

    /// <summary>
    /// Environment variable that, when set to <c>1</c>/<c>true</c>, unconditionally disables
    /// feedback collection (an absolute opt-out, mirroring <c>DOTNET_CLI_TELEMETRY_OPTOUT</c>).
    /// </summary>
    public const string OptOutEnvironmentVariable = "COSMOS2SQL_FEEDBACK_OPTOUT";

    /// <summary>
    /// Environment variable that, when set to <c>1</c>/<c>true</c>, opts in to feedback collection
    /// — but only when neither the command line nor configuration has expressed a preference.
    /// </summary>
    public const string OptInEnvironmentVariable = "COSMOS2SQL_FEEDBACK_OPTIN";

    /// <summary>
    /// Whether feedback collection is enabled via configuration. <see langword="null"/> means
    /// "not configured" (so lower-precedence sources decide); the effective default is disabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Optional override for the local outcome store path. When null/empty the per-user default
    /// (<see cref="LocalJsonFeedbackStore.GetDefaultPath"/>) is used.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>
    /// Optional remote telemetry endpoint. When set (and consent is granted), a coarsened payload
    /// is POSTed to this URL in addition to the local record. When null/empty, nothing ever leaves
    /// the local machine.
    /// </summary>
    public string? TelemetryEndpoint { get; set; }
}

/// <summary>
/// Identifies which input decided the effective feedback consent, for transparent diagnostics.
/// </summary>
public enum FeedbackConsentSource
{
    /// <summary>No source expressed a preference; the privacy-preserving default (disabled) applies.</summary>
    Default = 0,

    /// <summary>Consent was decided by the <c>FeedbackLoop:Enabled</c> configuration value.</summary>
    Configuration = 1,

    /// <summary>Consent was granted by the opt-in environment variable.</summary>
    EnvironmentOptIn = 2,

    /// <summary>Consent was denied by the absolute opt-out environment variable.</summary>
    EnvironmentOptOut = 3,

    /// <summary>Consent was decided by an explicit command-line flag.</summary>
    CommandLine = 4
}

/// <summary>
/// The resolved feedback consent decision and the source that determined it.
/// </summary>
public sealed class FeedbackConsent
{
    /// <summary>Whether feedback collection (and any transmission) is permitted.</summary>
    public bool IsGranted { get; }

    /// <summary>The input that determined <see cref="IsGranted"/>.</summary>
    public FeedbackConsentSource Source { get; }

    /// <summary>
    /// Creates a new <see cref="FeedbackConsent"/>.
    /// </summary>
    /// <param name="isGranted">Whether consent is granted.</param>
    /// <param name="source">The deciding source.</param>
    public FeedbackConsent(bool isGranted, FeedbackConsentSource source)
    {
        IsGranted = isGranted;
        Source = source;
    }

    /// <summary>
    /// Resolves the effective consent from all inputs using a privacy-first precedence:
    /// environment opt-out (absolute) → command-line flag → explicit configuration → environment
    /// opt-in → default (disabled). Environment opt-in only applies when configuration is silent.
    /// </summary>
    /// <param name="commandLineOptIn">Explicit CLI preference, or null when no flag was supplied.</param>
    /// <param name="configEnabled">Explicit configuration preference, or null when not configured.</param>
    /// <param name="envOptOut">Whether the absolute opt-out environment variable is set.</param>
    /// <param name="envOptIn">Whether the opt-in environment variable is set.</param>
    /// <returns>The resolved consent decision.</returns>
    public static FeedbackConsent Resolve(bool? commandLineOptIn, bool? configEnabled, bool envOptOut, bool envOptIn)
    {
        if (envOptOut)
        {
            return new FeedbackConsent(false, FeedbackConsentSource.EnvironmentOptOut);
        }

        if (commandLineOptIn.HasValue)
        {
            return new FeedbackConsent(commandLineOptIn.Value, FeedbackConsentSource.CommandLine);
        }

        if (configEnabled.HasValue)
        {
            return new FeedbackConsent(configEnabled.Value, FeedbackConsentSource.Configuration);
        }

        if (envOptIn)
        {
            return new FeedbackConsent(true, FeedbackConsentSource.EnvironmentOptIn);
        }

        return new FeedbackConsent(false, FeedbackConsentSource.Default);
    }
}

/// <summary>
/// Coordinates the <b>opt-in</b> collection of anonymized migration outcomes. Collection and any
/// remote transmission are gated behind an explicit consent decision that defaults to disabled.
/// </summary>
/// <remarks>
/// <para>
/// Consent gates <em>writing</em> new outcomes and any telemetry transmission. Reading previously
/// stored local outcomes (<see cref="GetOutcomesAsync"/>) is not gated: it is the user's own,
/// already-stored, local data used to refine the user's own recommendations. To purge it, delete
/// the file at <see cref="StoreLocation"/>.
/// </para>
/// </remarks>
public sealed class FeedbackCollectionService
{
    private readonly IFeedbackStore _store;
    private readonly IFeedbackTelemetrySink _telemetrySink;
    private readonly FeedbackOptions _options;
    private readonly ILogger<FeedbackCollectionService> _logger;

    /// <summary>
    /// Creates a new <see cref="FeedbackCollectionService"/>.
    /// </summary>
    /// <param name="store">The local outcome store.</param>
    /// <param name="telemetrySink">The optional remote telemetry sink.</param>
    /// <param name="options">The feedback options.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public FeedbackCollectionService(
        IFeedbackStore store,
        IFeedbackTelemetrySink telemetrySink,
        FeedbackOptions options,
        ILogger<FeedbackCollectionService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _telemetrySink = telemetrySink ?? throw new ArgumentNullException(nameof(telemetrySink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The location where outcomes are stored (for display in consent notices).</summary>
    public string StoreLocation => _store.Location;

    /// <summary>Whether a remote telemetry endpoint is configured.</summary>
    public bool HasTelemetryEndpoint => !string.IsNullOrWhiteSpace(_options.TelemetryEndpoint);

    /// <summary>
    /// Resolves the effective feedback consent, combining the optional command-line preference with
    /// configuration and environment variables.
    /// </summary>
    /// <param name="commandLineOptIn">Explicit CLI preference, or null when no flag was supplied.</param>
    /// <returns>The resolved consent decision.</returns>
    public FeedbackConsent ResolveConsent(bool? commandLineOptIn = null)
    {
        var envOptOut = IsEnvironmentFlagSet(FeedbackOptions.OptOutEnvironmentVariable);
        var envOptIn = IsEnvironmentFlagSet(FeedbackOptions.OptInEnvironmentVariable);
        return FeedbackConsent.Resolve(commandLineOptIn, _options.Enabled, envOptOut, envOptIn);
    }

    /// <summary>
    /// Records an anonymized migration outcome, but only when consent is granted. When disabled,
    /// this is a no-op that returns <see langword="false"/> and persists nothing.
    /// </summary>
    /// <param name="outcome">The anonymized outcome to record.</param>
    /// <param name="commandLineOptIn">Explicit CLI preference, or null when no flag was supplied.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns><see langword="true"/> if the outcome was recorded; otherwise <see langword="false"/>.</returns>
    public async Task<bool> RecordOutcomeAsync(
        MigrationOutcome outcome,
        bool? commandLineOptIn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var consent = ResolveConsent(commandLineOptIn);
        if (!consent.IsGranted)
        {
            _logger.LogInformation(
                "Feedback collection is disabled (consent source: {Source}); outcome not recorded.",
                consent.Source);
            return false;
        }

        await _store.AppendAsync(outcome, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Recorded an anonymized migration outcome to {Location}.", _store.Location);

        if (HasTelemetryEndpoint)
        {
            var coarsened = CoarsenedOutcome.From(outcome);
            await _telemetrySink.SendAsync(coarsened, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Streams previously recorded local outcomes (the user's own data) for use in recommendation
    /// refinement. Not gated on consent; see the class remarks.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>An asynchronous stream of stored outcomes.</returns>
    public IAsyncEnumerable<MigrationOutcome> GetOutcomesAsync(CancellationToken cancellationToken = default) =>
        _store.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Writes a transparent consent notice describing exactly what is and isn't collected, where it
    /// is stored, and how to opt out.
    /// </summary>
    /// <param name="output">The writer to emit the notice to.</param>
    /// <param name="consent">The resolved consent decision to describe.</param>
    public void WriteConsentNotice(TextWriter output, FeedbackConsent consent)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(consent);

        output.WriteLine();
        output.WriteLine("🔒 Continuous-learning feedback");
        if (consent.IsGranted)
        {
            output.WriteLine($"   • Status: ENABLED (source: {consent.Source})");
            output.WriteLine($"   • Stored locally at: {StoreLocation}");
            output.WriteLine(HasTelemetryEndpoint
                ? "   • A coarsened (bucketed) summary is also sent to the configured telemetry endpoint."
                : "   • Data stays on this machine (no telemetry endpoint configured).");
            output.WriteLine("   • Collected: anonymized, aggregate metrics only (sizes, counts, complexity,");
            output.WriteLine("     recommended/deployed platform & tier, success/cost/performance outcomes).");
            output.WriteLine("   • NOT collected: account/database/container names, documents, credentials, or any PII.");
            output.WriteLine($"   • Opt out anytime: set {FeedbackOptions.OptOutEnvironmentVariable}=1 or pass --disable-feedback.");
        }
        else
        {
            output.WriteLine($"   • Status: DISABLED (source: {consent.Source}) — nothing is collected.");
            output.WriteLine("   • Opt in with --enable-feedback or FeedbackLoop:Enabled=true to help improve recommendations.");
        }
        output.WriteLine();
    }

    private static bool IsEnvironmentFlagSet(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
