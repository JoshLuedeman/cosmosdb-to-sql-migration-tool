using System.Text.RegularExpressions;

namespace CosmosToSqlAssessment.Services.Discovery;

/// <summary>
/// Parses Azure Cosmos DB endpoint URLs to extract the account name.
/// Supports public, Private Link, and sovereign cloud endpoint patterns.
/// </summary>
internal static partial class CosmosEndpointParser
{
    // Valid Cosmos DB host suffixes (public + sovereign clouds), with optional Private Link segment
    private static readonly string[] ValidSuffixes =
    [
        ".privatelink.documents.azure.com",
        ".privatelink.documents.azure.cn",
        ".privatelink.documents.azure.us",
        ".documents.azure.com",
        ".documents.azure.cn",
        ".documents.azure.us",
    ];

    // Cosmos account naming rules: 3-44 chars, lowercase alphanumeric + hyphen,
    // must start and end with alphanumeric
    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,42}[a-z0-9]$")]
    private static partial Regex AccountNamePattern();

    /// <summary>
    /// Attempts to extract the Cosmos DB account name from an endpoint URL.
    /// </summary>
    /// <param name="endpointUrl">The Cosmos DB endpoint URL (e.g., https://myaccount.documents.azure.com:443/).</param>
    /// <param name="accountName">The extracted account name, or null if parsing fails.</param>
    /// <returns>True if a valid account name was extracted; false otherwise.</returns>
    public static bool TryParseAccountName(string? endpointUrl, out string? accountName)
    {
        accountName = null;

        if (string.IsNullOrWhiteSpace(endpointUrl))
            return false;

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.ToLowerInvariant();

        // Find matching suffix and extract account name
        foreach (var suffix in ValidSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal))
            {
                var candidate = host[..^suffix.Length];

                if (string.IsNullOrEmpty(candidate))
                    return false;

                if (!AccountNamePattern().IsMatch(candidate))
                    return false;

                accountName = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the account name or returns a fallback value.
    /// Useful as a drop-in replacement for legacy loose parsing.
    /// </summary>
    public static string GetAccountNameOrDefault(string? endpointUrl, string fallback = "Unknown")
    {
        return TryParseAccountName(endpointUrl, out var name) ? name! : fallback;
    }
}
