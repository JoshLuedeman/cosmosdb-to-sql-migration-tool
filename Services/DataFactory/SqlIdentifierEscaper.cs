namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Bracket-quotes T-SQL identifiers using SQL Server rules: <c>[name]</c>, with
/// any literal <c>]</c> doubled to <c>]]</c>. Centralised so #145 row-count
/// validation queries (and any future generated T-SQL) cannot be tricked by an
/// assessment-supplied identifier that contains <c>]</c>.
/// </summary>
public static class SqlIdentifierEscaper
{
    /// <summary>
    /// Wraps <paramref name="raw"/> in square brackets and doubles any embedded <c>]</c>
    /// characters so the result is a valid T-SQL bracket-quoted identifier.
    /// </summary>
    /// <param name="raw">The unescaped identifier (schema, table, or column name) from the assessment.</param>
    /// <returns>A bracket-quoted T-SQL identifier safe for embedding in generated SQL queries.</returns>
    public static string Bracket(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        return "[" + raw.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// Two-part name: <c>[schema].[table]</c>, each part bracket-escaped independently.
    /// </summary>
    public static string TwoPart(string schema, string table) =>
        Bracket(schema) + "." + Bracket(table);
}
