namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Generates the idempotent T-SQL DDL for the per-mapping watermark table that
/// the #147 incremental copy pattern reads from / writes to. The same DDL is
/// emitted as a stand-alone <c>.sql</c> file under <c>ADF/SQL/</c> and (when
/// <see cref="IncrementalCopyOptions.EnsureWatermarkTableAtRuntime"/> is on)
/// embedded inside an ADF <c>Script</c> activity at the start of every per-db
/// pipeline so the pipeline self-bootstraps the table on first run.
///
/// Schema:
///   <c>[mappingKey] NVARCHAR(450) PRIMARY KEY</c> — composite key chosen to fit
///     SQL Server's 900-byte index limit (NVARCHAR(450) = 900 bytes).
///   <c>[lastTs] BIGINT NOT NULL DEFAULT 0</c> — Unix seconds; matches Cosmos
///     <c>_ts</c> domain.
///   <c>[updatedUtc] DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()</c> — audit
///     column; not used by the runtime but useful for ops.
/// </summary>
public sealed class WatermarkSchemaBuilder
{
    /// <summary>Maximum length of a single PK NVARCHAR column under SQL Server's 900-byte index limit.</summary>
    public const int MappingKeyMaxLength = 450;

    /// <summary>
    /// Builds the idempotent <c>IF OBJECT_ID(...) IS NULL CREATE TABLE ...</c>
    /// script for the watermark table.
    /// </summary>
    /// <param name="options">Incremental copy options. Schema and table names flow through <see cref="SqlIdentifierEscaper"/>.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">If the schema or table name is empty / whitespace.</exception>
    public string BuildCreateScript(IncrementalCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkSchemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkTableName);

        var twoPart = SqlIdentifierEscaper.TwoPart(options.WatermarkSchemaName, options.WatermarkTableName);
        // OBJECT_ID accepts the bracketed two-part name as a literal — the surrounding N''
        // is the standard pattern for Unicode object lookups.
        var objectIdLiteral = "N'" + twoPart.Replace("'", "''") + "'";

        return $"""
            IF OBJECT_ID({objectIdLiteral}, N'U') IS NULL
            BEGIN
                CREATE TABLE {twoPart} (
                    [mappingKey] NVARCHAR({MappingKeyMaxLength}) NOT NULL PRIMARY KEY,
                    [lastTs]     BIGINT                          NOT NULL CONSTRAINT DF_AdfWatermark_lastTs DEFAULT 0,
                    [updatedUtc] DATETIME2(3)                    NOT NULL CONSTRAINT DF_AdfWatermark_updatedUtc DEFAULT SYSUTCDATETIME()
                );
            END;
            """;
    }

    /// <summary>
    /// Builds the per-mapping MERGE script body that updates the watermark
    /// (incrementing-only — the <c>WHEN MATCHED AND S.[lastTs] &gt; T.[lastTs]</c>
    /// clause is the rubber-duck race-condition guard so a slower concurrent run
    /// cannot move the watermark backwards).
    /// </summary>
    /// <param name="options">Incremental copy options — schema/table names.</param>
    /// <param name="mappingKey">Composite mapping key value (already SQL-literal-safe; will be quote-escaped again here).</param>
    /// <param name="lastTsExpression">SQL expression that yields the new watermark value (typically an ADF expression-injected integer).</param>
    public string BuildUpdateScript(IncrementalCopyOptions options, string mappingKey, string lastTsExpression)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkSchemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkTableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastTsExpression);

        var twoPart = SqlIdentifierEscaper.TwoPart(options.WatermarkSchemaName, options.WatermarkTableName);
        var mappingKeyLiteral = SqlLiteralEscaper.Quote(mappingKey);

        return $"""
            MERGE {twoPart} AS T
            USING (SELECT {mappingKeyLiteral} AS [mappingKey], {lastTsExpression} AS [lastTs]) AS S
            ON T.[mappingKey] = S.[mappingKey]
            WHEN MATCHED AND S.[lastTs] > T.[lastTs] THEN
                UPDATE SET T.[lastTs] = S.[lastTs], T.[updatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([mappingKey], [lastTs], [updatedUtc]) VALUES (S.[mappingKey], S.[lastTs], SYSUTCDATETIME());
            """;
    }

    /// <summary>
    /// Builds the per-mapping SELECT for the <c>LookupWatermark</c> activity. Uses
    /// <c>ISNULL((SELECT TOP 1 …), &lt;initial&gt;)</c> so the first run (no row in
    /// the table) returns the operator-supplied bootstrap value instead of NULL.
    /// </summary>
    /// <param name="options">Incremental copy options — schema/table names.</param>
    /// <param name="mappingKey">Composite mapping key value.</param>
    /// <param name="initialExpression">SQL expression for the bootstrap watermark (typically the pipeline parameter literal).</param>
    public string BuildSelectScript(IncrementalCopyOptions options, string mappingKey, string initialExpression)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkSchemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WatermarkTableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialExpression);

        var twoPart = SqlIdentifierEscaper.TwoPart(options.WatermarkSchemaName, options.WatermarkTableName);
        var mappingKeyLiteral = SqlLiteralEscaper.Quote(mappingKey);

        return $"SELECT ISNULL((SELECT TOP 1 [lastTs] FROM {twoPart} WHERE [mappingKey] = {mappingKeyLiteral}), {initialExpression}) AS lastTs";
    }
}
