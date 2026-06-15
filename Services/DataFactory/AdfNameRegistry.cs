using System.Security.Cryptography;
using System.Text;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Allocates collision-proof, ADF-compliant identifiers for activities, datasets, linked
/// services, pipelines, and the on-disk file names that mirror them. ADF allows up to
/// 260 chars for most resources but activity names are capped at 55. We use the tighter
/// budget for everything so a single sanitizer covers all artifacts. Case-insensitive
/// collisions get a stable 8-char hash suffix derived from the full source identity.
/// </summary>
public sealed class AdfNameRegistry
{
    /// <summary>ADF activity-name length cap. Other artifacts can be longer but we use this universally.</summary>
    public const int MaxNameLength = 55;

    private const int HashSuffixLength = 8;          // "_xxxxxxxx" -> 9 chars including the underscore
    private const int SuffixBudget = HashSuffixLength + 1;

    private readonly Dictionary<string, string> _taken =
        new(StringComparer.OrdinalIgnoreCase); // case-insensitive collision detection

    /// <summary>
    /// Reserves a unique name for the given <paramref name="desired"/> identifier.
    /// If <paramref name="desired"/> (after sanitization + truncation) collides with a
    /// previous allocation, an 8-character hash of <paramref name="collisionKey"/> is
    /// appended. The same <paramref name="collisionKey"/> always produces the same hash,
    /// so generation is deterministic across runs.
    /// </summary>
    /// <param name="desired">Human-readable identifier candidate (e.g. <c>"Cosmos_users"</c>).</param>
    /// <param name="collisionKey">Fully-qualified identity used to derive the hash suffix on collision.</param>
    public string Allocate(string desired, string collisionKey)
    {
        var sanitized = Sanitize(desired);
        var truncated = Truncate(sanitized, MaxNameLength);

        if (_taken.TryAdd(truncated, collisionKey))
        {
            return truncated;
        }

        // Collision (or case-insensitive match) → append deterministic hash suffix.
        var withSuffix = $"{Truncate(sanitized, MaxNameLength - SuffixBudget)}_{ShortHash(collisionKey)}";

        // Pathological case: even hashed name collides (different collisionKey but same hash prefix).
        // Bump with an incrementing tail until unique.
        var candidate = withSuffix;
        var counter = 1;
        while (!_taken.TryAdd(candidate, collisionKey))
        {
            var tail = $"_{counter}";
            candidate = Truncate(withSuffix, MaxNameLength - tail.Length) + tail;
            counter++;
        }
        return candidate;
    }

    /// <summary>Sanitizes to <c>[A-Za-z0-9_]</c>, prefixes <c>T_</c> if the result is empty or starts with a digit.</summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "T_";
        }

        var buffer = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            buffer.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var sanitized = buffer.ToString();
        if (sanitized.Length == 0 || char.IsDigit(sanitized[0]))
        {
            sanitized = "T_" + sanitized;
        }
        return sanitized;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(HashSuffixLength);
        for (var i = 0; i < HashSuffixLength / 2; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
