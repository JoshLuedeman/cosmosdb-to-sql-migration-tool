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
        // Helpers
        // ------------------------------------------------------------------

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
