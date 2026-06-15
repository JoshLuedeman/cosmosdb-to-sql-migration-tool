using System.Text;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds JSONPath expressions for ADF translator mappings. Bracket-notation is used
/// for every segment so field names containing dots, hyphens, spaces, or quotes work
/// without manual escaping by the operator.
/// </summary>
public static class JsonPathEscaper
{
    /// <summary>
    /// Returns a JSONPath of the form <c>$['segment1']['segment2']</c> for the given
    /// dotted source field path (e.g. <c>"address.city"</c>). A leading <c>/</c> or
    /// <c>$.</c> is tolerated and stripped before splitting.
    /// </summary>
    public static string ToJsonPath(string sourceField)
    {
        if (string.IsNullOrWhiteSpace(sourceField))
        {
            return "$";
        }

        var trimmed = sourceField.TrimStart('/', '$');
        if (trimmed.StartsWith('.'))
        {
            trimmed = trimmed[1..];
        }

        var segments = trimmed.Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "$";
        }

        var sb = new StringBuilder("$");
        foreach (var segment in segments)
        {
            sb.Append("['").Append(EscapeSegment(segment)).Append("']");
        }
        return sb.ToString();
    }

    private static string EscapeSegment(string segment) =>
        segment.Replace("\\", "\\\\").Replace("'", "\\'");
}
