namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Escapes T-SQL string literals by doubling single quotes (<c>'</c> → <c>''</c>).
/// Used by #147 incremental queries that bake the mapping key as a literal into
/// a <c>sqlReaderQuery</c> / Script body (the alternative would be ADF Lookup
/// parameter binding, which Azure SQL Lookup does not support without switching
/// to <c>SqlServerStoredProcedure</c>).
/// </summary>
public static class SqlLiteralEscaper
{
    /// <summary>
    /// Returns the input wrapped in single quotes with any embedded single quotes
    /// doubled — <c>O'Brien</c> becomes <c>'O''Brien'</c>. Null is rejected.
    /// </summary>
    public static string Quote(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return "'" + raw.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Returns the input with any embedded single quotes doubled, without
    /// surrounding quotes — useful when the literal is being concatenated into
    /// an already-quoted ADF expression.
    /// </summary>
    public static string Escape(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.Replace("'", "''");
    }
}
