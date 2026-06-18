namespace CosmosToSqlAssessment.Interactive;

/// <summary>
/// Pure static validation functions for wizard inputs.
/// Each returns null if valid, or an error message string if invalid.
/// </summary>
internal static class InputValidators
{
    /// <summary>
    /// Validates a Cosmos DB account endpoint URL.
    /// Must be a valid absolute HTTPS URI.
    /// </summary>
    public static string? ValidateEndpoint(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Endpoint cannot be empty.";

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return "Invalid URL format. Please enter a valid absolute URL.";

        if (uri.Scheme != "https" && uri.Scheme != "http")
            return "Endpoint must use HTTPS (or HTTP for local emulator).";

        return null;
    }

    /// <summary>
    /// Validates a database name (must be non-empty).
    /// </summary>
    public static string? ValidateDatabaseName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Database name cannot be empty.";

        if (input.Length > 255)
            return "Database name is too long (max 255 characters).";

        return null;
    }

    /// <summary>
    /// Validates a Log Analytics workspace ID (should be a GUID, or empty to skip).
    /// </summary>
    public static string? ValidateWorkspaceId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Workspace ID cannot be empty.";

        if (!Guid.TryParse(input, out _))
            return "Workspace ID must be a valid GUID (e.g. 12345678-1234-1234-1234-123456789012).";

        return null;
    }

    /// <summary>
    /// Validates an output directory path.
    /// </summary>
    public static string? ValidateOutputDirectory(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null; // Empty is okay — will use default

        var invalidChars = Path.GetInvalidPathChars();
        if (input.IndexOfAny(invalidChars) >= 0)
            return "Path contains invalid characters.";

        return null;
    }
}
