using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services
{
    /// <summary>
    /// Generates post-migration validation artifacts (SQL + PowerShell + report
    /// templates) into the Scripts/PostMigration/ folder of a generated SQL
    /// Database Project.
    /// </summary>
    /// <remarks>
    /// Templates ship as embedded resources under
    /// <c>SqlProject/Templates/PostMigration/</c>. The generator substitutes
    /// per-table blocks built from the assessment.
    /// </remarks>
    public class ValidationScriptGeneratorService
    {
        private const string EmbeddedResourcePrefix =
            "CosmosToSqlAssessment.SqlProject.Templates.PostMigration.";

        private static readonly char[] InvalidIdentifierChars =
            new[] { '\'', '\"', '[', ']', '\r', '\n', ';' };

        private readonly ILogger<ValidationScriptGeneratorService> _logger;

        public ValidationScriptGeneratorService(ILogger<ValidationScriptGeneratorService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Generates all available post-migration validation scripts for the
        /// given assessment into <paramref name="projectRoot"/>/Scripts/PostMigration/.
        /// </summary>
        public async Task<ValidationScriptGenerationResult> GenerateAsync(
            AssessmentResult assessment,
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(assessment);
            ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

            var outputDir = Path.Combine(projectRoot, "Scripts", "PostMigration");
            Directory.CreateDirectory(outputDir);

            var result = new ValidationScriptGenerationResult
            {
                OutputDirectory = outputDir
            };

            result.GeneratedFiles.Add(
                await GenerateRowCountValidationAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            result.GeneratedFiles.Add(
                await GenerateChecksumValidationAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            _logger.LogInformation(
                "Generated {Count} post-migration validation script(s) into {OutputDir}",
                result.GeneratedFiles.Count, outputDir);

            return result;
        }

        // ------------------------------------------------------------------
        // 01-RowCountValidation.sql
        // ------------------------------------------------------------------

        internal async Task<string> GenerateRowCountValidationAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("01-RowCountValidation.sql");
            var tables = EnumerateMigratedTables(assessment).ToList();

            var rendered = template
                .Replace("{{ExpectedCountsSeed}}", BuildExpectedCountsSeed(tables))
                .Replace("{{TableRowsBlock}}", BuildRowCountChecksBlock(tables));

            var path = Path.Combine(outputDir, "01-RowCountValidation.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated row-count validation: {Path}", path);
            return path;
        }

        private static string BuildExpectedCountsSeed(IReadOnlyList<MigratedTable> tables)
        {
            if (tables.Count == 0)
            {
                return "    -- (no migrated tables in assessment)\n";
            }

            var sb = new StringBuilder();
            foreach (var t in tables)
            {
                if (t.EstimatedRowCount.HasValue)
                {
                    var schema = EscapeSqlLiteral(t.Schema);
                    var table = EscapeSqlLiteral(t.Table);
                    sb.AppendLine($"    MERGE dbo.ValidationExpectedCounts AS target");
                    sb.AppendLine($"    USING (VALUES (N'{schema}', N'{table}', N'Assessment', CAST({t.EstimatedRowCount.Value} AS BIGINT)))");
                    sb.AppendLine($"        AS src(SchemaName, TableName, Source, ExpectedRows)");
                    sb.AppendLine($"        ON target.SchemaName = src.SchemaName AND target.TableName = src.TableName AND target.Source = src.Source");
                    sb.AppendLine($"    WHEN MATCHED THEN UPDATE SET ExpectedRows = src.ExpectedRows, CapturedAt = SYSUTCDATETIME()");
                    sb.AppendLine($"    WHEN NOT MATCHED THEN INSERT (SchemaName, TableName, Source, ExpectedRows) VALUES (src.SchemaName, src.TableName, src.Source, src.ExpectedRows);");
                    sb.AppendLine();
                }
                else
                {
                    var schema = EscapeSqlLiteral(t.Schema);
                    var table = EscapeSqlLiteral(t.Table);
                    sb.AppendLine($"    -- No assessment-time baseline for [{schema}].[{table}] ({t.Origin}); supply via -ExpectedCountsCsv.");
                    sb.AppendLine($"    INSERT dbo.ValidationResults (RunId, Category, SchemaName, TableName, CheckName, Status, Details)");
                    sb.AppendLine($"    VALUES (@RunId, N'RowCount', N'{schema}', N'{table}', N'BaselineGap', N'INFO',");
                    sb.AppendLine($"            N'No assessment-time row count available for {t.Origin} table; provide via override CSV.');");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string BuildRowCountChecksBlock(IReadOnlyList<MigratedTable> tables)
        {
            if (tables.Count == 0)
            {
                return "    -- (no migrated tables to validate)\n";
            }

            var sb = new StringBuilder();
            foreach (var t in tables)
            {
                var schema = EscapeSqlLiteral(t.Schema);
                var table = EscapeSqlLiteral(t.Table);
                sb.AppendLine($"    EXEC dbo.sp_ValidationRowCountCheck");
                sb.AppendLine($"        @RunId            = @RunId,");
                sb.AppendLine($"        @SchemaName       = N'{schema}',");
                sb.AppendLine($"        @TableName        = N'{table}',");
                sb.AppendLine($"        @CanUseDmv        = @CanUseDmv,");
                sb.AppendLine($"        @TolerancePctPass = @TolerancePctPass,");
                sb.AppendLine($"        @TolerancePctWarn = @TolerancePctWarn;");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 02-DataIntegrityChecks.sql
        // ------------------------------------------------------------------

        internal async Task<string> GenerateChecksumValidationAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("02-DataIntegrityChecks.sql");
            var rendered = template.Replace("{{ChecksumChecksBlock}}", BuildChecksumChecksBlock(assessment));

            var path = Path.Combine(outputDir, "02-DataIntegrityChecks.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated checksum validation: {Path}", path);
            return path;
        }

        private static string BuildChecksumChecksBlock(AssessmentResult assessment)
        {
            var sb = new StringBuilder();
            var any = false;

            foreach (var db in assessment.SqlAssessment.DatabaseMappings)
            {
                foreach (var container in db.ContainerMappings)
                {
                    if (string.IsNullOrWhiteSpace(container.TargetTable))
                        continue;

                    ValidateIdentifier(container.TargetSchema, nameof(container.TargetSchema));
                    ValidateIdentifier(container.TargetTable, nameof(container.TargetTable));

                    var schema = string.IsNullOrWhiteSpace(container.TargetSchema) ? "dbo" : container.TargetSchema;
                    AppendChecksumEntry(sb, schema, container.TargetTable, container.FieldMappings, isChild: false, parentKeyColumn: null);
                    any = true;

                    foreach (var child in container.ChildTableMappings)
                    {
                        if (string.IsNullOrWhiteSpace(child.TargetTable))
                            continue;

                        ValidateIdentifier(child.TargetSchema, nameof(child.TargetSchema));
                        ValidateIdentifier(child.TargetTable, nameof(child.TargetTable));

                        var childSchema = string.IsNullOrWhiteSpace(child.TargetSchema) ? "dbo" : child.TargetSchema;
                        var parentKey = string.IsNullOrWhiteSpace(child.ParentKeyColumn) ? "ParentId" : child.ParentKeyColumn;
                        ValidateIdentifier(parentKey, nameof(child.ParentKeyColumn));

                        AppendChecksumEntry(sb, childSchema, child.TargetTable, child.FieldMappings, isChild: true, parentKeyColumn: parentKey);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                sb.AppendLine("    -- (no migrated tables to checksum)");
            }
            return sb.ToString();
        }

        private static void AppendChecksumEntry(
            StringBuilder sb,
            string schema,
            string table,
            IReadOnlyList<FieldMapping> fieldMappings,
            bool isChild,
            string? parentKeyColumn)
        {
            var schemaLit = EscapeSqlLiteral(schema);
            var tableLit = EscapeSqlLiteral(table);

            var columns = BuildOrderedColumnList(fieldMappings, isChild, parentKeyColumn);
            if (columns.Count == 0)
            {
                sb.AppendLine($"    INSERT dbo.ValidationResults (RunId, Category, SchemaName, TableName, CheckName, Status, Details)");
                sb.AppendLine($"    VALUES (@RunId, N'Checksum', N'{schemaLit}', N'{tableLit}', N'ColumnList', N'INFO',");
                sb.AppendLine($"            N'No field mappings recorded in assessment; checksum skipped.');");
                return;
            }

            var columnList = string.Join(", ", columns.Select(c => BuildColumnExpression(c)));
            var orderBy = string.Join(", ", BuildOrderByColumns(columns, isChild, parentKeyColumn).Select(c => $"[{c.TargetColumn}]"));
            var columnListLit = EscapeSqlLiteral(columnList);
            var orderByLit = EscapeSqlLiteral(orderBy);

            sb.AppendLine($"    EXEC dbo.sp_ValidationChecksum");
            sb.AppendLine($"        @RunId      = @RunId,");
            sb.AppendLine($"        @SchemaName = N'{schemaLit}',");
            sb.AppendLine($"        @TableName  = N'{tableLit}',");
            sb.AppendLine($"        @SampleRows = @SampleRows,");
            sb.AppendLine($"        @ColumnList = N'{columnListLit}',");
            sb.AppendLine($"        @OrderBy    = N'{orderByLit}';");
        }

        private static List<FieldMapping> BuildOrderedColumnList(
            IReadOnlyList<FieldMapping> fieldMappings,
            bool isChild,
            string? parentKeyColumn)
        {
            var ordered = new List<FieldMapping>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (isChild && !string.IsNullOrWhiteSpace(parentKeyColumn))
            {
                ordered.Add(new FieldMapping { TargetColumn = parentKeyColumn!, TargetType = "NVARCHAR(MAX)" });
                seen.Add(parentKeyColumn!);
            }

            foreach (var fm in fieldMappings)
            {
                if (string.IsNullOrWhiteSpace(fm.TargetColumn))
                    continue;
                ValidateIdentifier(fm.TargetColumn, nameof(fm.TargetColumn));
                if (seen.Add(fm.TargetColumn))
                    ordered.Add(fm);
            }
            return ordered;
        }

        private static IEnumerable<FieldMapping> BuildOrderByColumns(
            IReadOnlyList<FieldMapping> columns,
            bool isChild,
            string? parentKeyColumn)
        {
            var ordered = new List<FieldMapping>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(FieldMapping fm)
            {
                if (fm is null || string.IsNullOrWhiteSpace(fm.TargetColumn)) return;
                if (IsApproximateNumericType(fm.TargetType)) return;
                if (seen.Add(fm.TargetColumn))
                    ordered.Add(fm);
            }

            // Child tables: parent key first.
            if (isChild && !string.IsNullOrWhiteSpace(parentKeyColumn))
            {
                var pk = columns.FirstOrDefault(c => string.Equals(c.TargetColumn, parentKeyColumn, StringComparison.OrdinalIgnoreCase));
                if (pk != null) Add(pk);
            }

            // Explicit partition-key columns next.
            foreach (var c in columns.Where(c => c.IsPartitionKey))
                Add(c);

            // Common ID-like columns (only exact, well-known names).
            if (ordered.Count == 0)
            {
                foreach (var c in columns.Where(c =>
                    string.Equals(c.TargetColumn, "Id",    StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.TargetColumn, "RowId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.TargetColumn, "_id",   StringComparison.OrdinalIgnoreCase)))
                {
                    Add(c);
                    break;
                }
            }

            // Fallback: every column alphabetically, excluding float/real.
            if (ordered.Count == 0)
            {
                foreach (var c in columns.OrderBy(c => c.TargetColumn, StringComparer.OrdinalIgnoreCase))
                    Add(c);
            }

            return ordered.Count > 0 ? ordered : columns.Take(1);
        }

        private static bool IsApproximateNumericType(string targetType)
        {
            if (string.IsNullOrEmpty(targetType)) return false;
            var t = targetType.Trim().ToUpperInvariant();
            return t == "FLOAT" || t.StartsWith("FLOAT(") || t == "REAL";
        }

        /// <summary>
        /// Builds the per-column expression used inside CONCAT_WS:
        ///   ISNULL(CONVERT(VARCHAR(MAX), [col], &lt;style&gt;), '\N')
        /// </summary>
        internal static string BuildColumnExpression(FieldMapping fm)
        {
            var col = $"[{fm.TargetColumn}]";
            var style = ResolveConvertStyle(fm.TargetType);
            var convertExpr = style is null
                ? $"CONVERT(VARCHAR(MAX), {col})"
                : $"CONVERT(VARCHAR(MAX), {col}, {style})";
            return $"ISNULL({convertExpr}, ''\\N'')";
        }

        private static int? ResolveConvertStyle(string targetType)
        {
            if (string.IsNullOrEmpty(targetType)) return null;
            var t = targetType.Trim().ToUpperInvariant();

            if (t.StartsWith("DATETIME") || t.StartsWith("DATE") || t.StartsWith("TIME") || t.StartsWith("SMALLDATETIME"))
                return 121; // ODBC canonical (with milliseconds)
            if (t.StartsWith("VARBINARY") || t.StartsWith("BINARY") || t == "IMAGE")
                return 2;   // Hex-without-0x for varbinary
            if (t.StartsWith("UNIQUEIDENTIFIER"))
                return null; // direct cast produces canonical 8-4-4-4-12
            return null;
        }

        internal static IEnumerable<MigratedTable> EnumerateMigratedTables(AssessmentResult assessment)
        {
            foreach (var db in assessment.SqlAssessment.DatabaseMappings)
            {
                foreach (var container in db.ContainerMappings)
                {
                    if (string.IsNullOrWhiteSpace(container.TargetTable))
                        continue;

                    ValidateIdentifier(container.TargetSchema, nameof(container.TargetSchema));
                    ValidateIdentifier(container.TargetTable, nameof(container.TargetTable));

                    yield return new MigratedTable(
                        Schema: string.IsNullOrWhiteSpace(container.TargetSchema) ? "dbo" : container.TargetSchema,
                        Table: container.TargetTable,
                        EstimatedRowCount: container.EstimatedRowCount > 0 ? container.EstimatedRowCount : null,
                        Origin: "container");

                    foreach (var child in container.ChildTableMappings)
                    {
                        if (string.IsNullOrWhiteSpace(child.TargetTable))
                            continue;

                        ValidateIdentifier(child.TargetSchema, nameof(child.TargetSchema));
                        ValidateIdentifier(child.TargetTable, nameof(child.TargetTable));

                        yield return new MigratedTable(
                            Schema: string.IsNullOrWhiteSpace(child.TargetSchema) ? "dbo" : child.TargetSchema,
                            Table: child.TargetTable,
                            EstimatedRowCount: null,
                            Origin: "child");
                    }
                }
            }
        }

        private static void ValidateIdentifier(string identifier, string paramName)
        {
            if (string.IsNullOrEmpty(identifier))
                return;

            if (identifier.IndexOfAny(InvalidIdentifierChars) >= 0)
            {
                throw new InvalidOperationException(
                    $"Schema/table identifier '{identifier}' (from {paramName}) contains characters that are not safe to inject into generated T-SQL. " +
                    "Sanitize the assessment before generating validation scripts.");
            }
        }

        private static string EscapeSqlLiteral(string value)
            => (value ?? string.Empty).Replace("'", "''");

        internal static string LoadTemplate(string fileName)
        {
            var assembly = typeof(ValidationScriptGeneratorService).Assembly;
            var resourceName = EmbeddedResourcePrefix + fileName;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var available = string.Join(", ", assembly.GetManifestResourceNames());
                throw new FileNotFoundException(
                    $"Embedded validation template '{resourceName}' not found. Available resources: {available}");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        internal sealed record MigratedTable(string Schema, string Table, long? EstimatedRowCount, string Origin);
    }

    /// <summary>
    /// Result of generating post-migration validation artifacts.
    /// </summary>
    public class ValidationScriptGenerationResult
    {
        public string OutputDirectory { get; set; } = string.Empty;
        public List<string> GeneratedFiles { get; set; } = new();
    }
}
