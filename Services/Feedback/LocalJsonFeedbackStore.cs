using System.Runtime.CompilerServices;
using System.Text.Json;
using CosmosToSqlAssessment.Models;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Feedback;

/// <summary>
/// A local, append-only, JSON-lines (<c>.jsonl</c>) implementation of <see cref="IFeedbackStore"/>.
/// Each anonymized <see cref="MigrationOutcome"/> is serialized to a single line. This is the
/// default, fully-offline storage mechanism for the feedback loop.
/// </summary>
/// <remarks>
/// Writes are serialized with a process-local semaphore and use an append-mode
/// <see cref="FileStream"/> with <see cref="FileShare.Read"/>; reads open the file with
/// <see cref="FileShare.ReadWrite"/> and stream line-by-line without buffering the whole file.
/// Malformed or truncated lines (e.g., from an interrupted write) are skipped and logged
/// without echoing their raw content.
/// </remarks>
public sealed class LocalJsonFeedbackStore : IFeedbackStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<LocalJsonFeedbackStore> _logger;

    /// <inheritdoc />
    public string Location { get; }

    /// <summary>
    /// Creates a new <see cref="LocalJsonFeedbackStore"/>.
    /// </summary>
    /// <param name="options">Feedback options; <see cref="FeedbackOptions.StorePath"/> overrides the default path.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LocalJsonFeedbackStore(FeedbackOptions options, ILogger<LocalJsonFeedbackStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Location = string.IsNullOrWhiteSpace(options.StorePath) ? GetDefaultPath() : options.StorePath!;
    }

    /// <summary>
    /// Computes the default per-user store path under the local application-data folder.
    /// </summary>
    /// <returns>The absolute default store path.</returns>
    public static string GetDefaultPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(baseDir, "CosmosToSqlAssessment", "feedback", "migration-outcomes.jsonl");
    }

    /// <inheritdoc />
    public async Task AppendAsync(MigrationOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var directory = Path.GetDirectoryName(Location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serialized to a single physical line; JSON encodes any control characters, so the
        // record can never span multiple lines and corrupt the JSON-lines format.
        var line = JsonSerializer.Serialize(outcome, SerializerOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                Location, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MigrationOutcome> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Location))
        {
            yield break;
        }

        await using var stream = new FileStream(
            Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MigrationOutcome? outcome = null;
            try
            {
                outcome = JsonSerializer.Deserialize<MigrationOutcome>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Skipping a malformed feedback record in {Location}.", Location);
            }

            if (outcome is not null)
            {
                yield return outcome;
            }
        }
    }
}
