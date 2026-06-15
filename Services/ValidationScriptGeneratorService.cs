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

            result.GeneratedFiles.Add(
                await GenerateSampleDataComparisonAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            result.GeneratedFiles.Add(
                await GenerateForeignKeyValidationAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            result.GeneratedFiles.Add(
                await GenerateIndexValidationAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            result.GeneratedFiles.Add(
                await GeneratePerformanceBaselineAsync(assessment, outputDir, cancellationToken)
                    .ConfigureAwait(false));

            var reportTemplates = await GenerateValidationReportTemplatesAsync(outputDir, cancellationToken)
                .ConfigureAwait(false);
            result.GeneratedFiles.AddRange(reportTemplates);

            result.GeneratedFiles.Add(
                await GenerateOrchestratorAsync(outputDir, cancellationToken)
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

        // ------------------------------------------------------------------
        // 03-SampleDataComparison.sql
        // ------------------------------------------------------------------

        internal async Task<string> GenerateSampleDataComparisonAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("03-SampleDataComparison.sql");
            var rendered = template.Replace("{{SampleCaptureBlock}}", BuildSampleCaptureBlock(assessment));

            var path = Path.Combine(outputDir, "03-SampleDataComparison.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated sample-data comparison: {Path}", path);
            return path;
        }

        private static string BuildSampleCaptureBlock(AssessmentResult assessment)
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
                    AppendSampleEntry(sb, schema, container.TargetTable, container.FieldMappings, isChild: false, parentKeyColumn: null);
                    any = true;

                    foreach (var child in container.ChildTableMappings)
                    {
                        if (string.IsNullOrWhiteSpace(child.TargetTable))
                            continue;

                        ValidateIdentifier(child.TargetSchema, nameof(child.TargetSchema));
                        ValidateIdentifier(child.TargetTable, nameof(child.TargetTable));

                        var childSchema = string.IsNullOrWhiteSpace(child.TargetSchema) ? "dbo" : child.TargetSchema;
                        var childTable = EscapeSqlLiteral(child.TargetTable);
                        var childSchemaLit = EscapeSqlLiteral(childSchema);
                        sb.AppendLine($"    INSERT dbo.ValidationResults (RunId, Category, SchemaName, TableName, CheckName, Status, Details)");
                        sb.AppendLine($"    VALUES (@RunId, N'Sample', N'{childSchemaLit}', N'{childTable}', N'SampleCapture', N'INFO',");
                        sb.AppendLine($"            N'Child table sample comparison skipped (no unique sort key in mapping).');");
                        any = true;
                    }
                }
            }

            if (!any)
            {
                sb.AppendLine("    -- (no migrated tables to sample)");
            }
            return sb.ToString();
        }

        private static void AppendSampleEntry(
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
                sb.AppendLine($"    VALUES (@RunId, N'Sample', N'{schemaLit}', N'{tableLit}', N'ColumnList', N'INFO',");
                sb.AppendLine($"            N'No field mappings recorded in assessment; sample comparison skipped.');");
                return;
            }

            var orderCols = BuildOrderByColumns(columns, isChild, parentKeyColumn).ToList();
            if (orderCols.Count == 0)
            {
                sb.AppendLine($"    INSERT dbo.ValidationResults (RunId, Category, SchemaName, TableName, CheckName, Status, Details)");
                sb.AppendLine($"    VALUES (@RunId, N'Sample', N'{schemaLit}', N'{tableLit}', N'OrderBy', N'INFO',");
                sb.AppendLine($"            N'No stable order key inferable from assessment; sample comparison skipped.');");
                return;
            }

            var columnList = string.Join(", ", columns.Select(c => $"[{c.TargetColumn}]"));
            var orderByAsc = string.Join(", ", orderCols.Select(c => $"[{c.TargetColumn}] ASC"));
            var orderByDesc = string.Join(", ", orderCols.Select(c => $"[{c.TargetColumn}] DESC"));
            var keyExpr = BuildKeyExpression(orderCols);

            var columnListLit = EscapeSqlLiteral(columnList);
            var orderByAscLit = EscapeSqlLiteral(orderByAsc);
            var orderByDescLit = EscapeSqlLiteral(orderByDesc);
            var keyExprLit = EscapeSqlLiteral(keyExpr);

            sb.AppendLine($"    EXEC dbo.sp_ValidationSampleCapture");
            sb.AppendLine($"        @RunId       = @RunId,");
            sb.AppendLine($"        @SchemaName  = N'{schemaLit}',");
            sb.AppendLine($"        @TableName   = N'{tableLit}',");
            sb.AppendLine($"        @SampleRows  = @SampleRows,");
            sb.AppendLine($"        @ColumnList  = N'{columnListLit}',");
            sb.AppendLine($"        @OrderByAsc  = N'{orderByAscLit}',");
            sb.AppendLine($"        @OrderByDesc = N'{orderByDescLit}',");
            sb.AppendLine($"        @KeyExpr     = N'{keyExprLit}';");
        }

        /// <summary>
        /// Builds a CONCAT_WS expression over the ORDER BY columns suitable for
        /// per-row SHA2-256 hashing. NULLs are normalized to the '\N' sentinel
        /// so missing-vs-present matches the checksum script's contract.
        /// </summary>
        internal static string BuildKeyExpression(IReadOnlyList<FieldMapping> orderCols)
        {
            if (orderCols.Count == 0)
                return "''";

            var parts = orderCols.Select(c =>
            {
                var col = $"[{c.TargetColumn}]";
                var style = ResolveConvertStyle(c.TargetType);
                var convertExpr = style is null
                    ? $"CONVERT(VARCHAR(MAX), {col})"
                    : $"CONVERT(VARCHAR(MAX), {col}, {style})";
                // single-quoted backslash-N is the NULL sentinel; quotes are
                // already T-SQL-escaped (doubled) because the whole expression
                // will be EscapeSqlLiteral'd again before embedding.
                return $"ISNULL({convertExpr}, ''\\N'')";
            });

            return $"CONCAT_WS(''|'', {string.Join(", ", parts)})";
        }

        // ------------------------------------------------------------------
        // 06-ForeignKeyValidation.sql
        // ------------------------------------------------------------------

        internal async Task<string> GenerateForeignKeyValidationAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("06-ForeignKeyValidation.sql");
            var schemaIndex = BuildTableSchemaIndex(assessment);

            var rendered = template
                .Replace("{{ExpectedForeignKeysSeed}}", BuildExpectedForeignKeysSeed(assessment, schemaIndex))
                .Replace("{{FkScopeTablesSeed}}", BuildFkScopeTablesSeed(assessment));

            var path = Path.Combine(outputDir, "06-ForeignKeyValidation.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated foreign-key validation: {Path}", path);
            return path;
        }

        private static Dictionary<string, string> BuildTableSchemaIndex(AssessmentResult assessment)
        {
            // Last-write-wins is fine; tables with the same bare name in multiple
            // schemas would already be ambiguous in the assessment FK metadata,
            // and the runtime existence check surfaces it as FAIL.
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var db in assessment.SqlAssessment.DatabaseMappings)
            {
                foreach (var container in db.ContainerMappings)
                {
                    if (string.IsNullOrWhiteSpace(container.TargetTable)) continue;
                    var schema = string.IsNullOrWhiteSpace(container.TargetSchema) ? "dbo" : container.TargetSchema;
                    index[container.TargetTable] = schema;

                    foreach (var child in container.ChildTableMappings)
                    {
                        if (string.IsNullOrWhiteSpace(child.TargetTable)) continue;
                        var childSchema = string.IsNullOrWhiteSpace(child.TargetSchema) ? "dbo" : child.TargetSchema;
                        index[child.TargetTable] = childSchema;
                    }
                }
            }
            return index;
        }

        private static string BuildExpectedForeignKeysSeed(
            AssessmentResult assessment,
            IReadOnlyDictionary<string, string> schemaIndex)
        {
            var fks = assessment.SqlAssessment.ForeignKeyConstraints;
            if (fks == null || fks.Count == 0)
            {
                return "    -- (no foreign key constraints in assessment)\n";
            }

            var sb = new StringBuilder();
            foreach (var fk in fks)
            {
                if (string.IsNullOrWhiteSpace(fk.ConstraintName) ||
                    string.IsNullOrWhiteSpace(fk.ChildTable) ||
                    string.IsNullOrWhiteSpace(fk.ChildColumn) ||
                    string.IsNullOrWhiteSpace(fk.ParentTable) ||
                    string.IsNullOrWhiteSpace(fk.ParentColumn))
                {
                    continue;
                }

                ValidateIdentifier(fk.ConstraintName, nameof(fk.ConstraintName));
                ValidateIdentifier(fk.ChildTable, nameof(fk.ChildTable));
                ValidateIdentifier(fk.ChildColumn, nameof(fk.ChildColumn));
                ValidateIdentifier(fk.ParentTable, nameof(fk.ParentTable));
                ValidateIdentifier(fk.ParentColumn, nameof(fk.ParentColumn));

                var childSchema = ResolveSchema(fk.ChildTable, schemaIndex);
                var parentSchema = ResolveSchema(fk.ParentTable, schemaIndex);

                var fkLit = EscapeSqlLiteral(fk.ConstraintName);
                var childSchemaLit = EscapeSqlLiteral(childSchema);
                var childTableLit = EscapeSqlLiteral(fk.ChildTable);
                var childColumnLit = EscapeSqlLiteral(fk.ChildColumn);
                var parentSchemaLit = EscapeSqlLiteral(parentSchema);
                var parentTableLit = EscapeSqlLiteral(fk.ParentTable);
                var parentColumnLit = EscapeSqlLiteral(fk.ParentColumn);
                var onDelete = EscapeSqlLiteral(string.IsNullOrWhiteSpace(fk.OnDeleteAction) ? "NO ACTION" : fk.OnDeleteAction.ToUpperInvariant());
                var onUpdate = EscapeSqlLiteral(string.IsNullOrWhiteSpace(fk.OnUpdateAction) ? "NO ACTION" : fk.OnUpdateAction.ToUpperInvariant());

                sb.AppendLine($"    MERGE dbo.ValidationExpectedForeignKeys AS target");
                sb.AppendLine($"    USING (VALUES (N'{fkLit}', N'{childSchemaLit}', N'{childTableLit}', N'{childColumnLit}',");
                sb.AppendLine($"                   N'{parentSchemaLit}', N'{parentTableLit}', N'{parentColumnLit}',");
                sb.AppendLine($"                   N'{onDelete}', N'{onUpdate}'))");
                sb.AppendLine($"        AS src(FkName, ChildSchema, ChildTable, ChildColumn, ParentSchema, ParentTable, ParentColumn, OnDeleteAction, OnUpdateAction)");
                sb.AppendLine($"        ON target.FkName = src.FkName");
                sb.AppendLine($"    WHEN MATCHED THEN UPDATE SET");
                sb.AppendLine($"        ChildSchema = src.ChildSchema, ChildTable = src.ChildTable, ChildColumn = src.ChildColumn,");
                sb.AppendLine($"        ParentSchema = src.ParentSchema, ParentTable = src.ParentTable, ParentColumn = src.ParentColumn,");
                sb.AppendLine($"        OnDeleteAction = src.OnDeleteAction, OnUpdateAction = src.OnUpdateAction,");
                sb.AppendLine($"        CapturedAt = SYSUTCDATETIME()");
                sb.AppendLine($"    WHEN NOT MATCHED THEN INSERT");
                sb.AppendLine($"        (FkName, ChildSchema, ChildTable, ChildColumn, ParentSchema, ParentTable, ParentColumn, OnDeleteAction, OnUpdateAction)");
                sb.AppendLine($"        VALUES");
                sb.AppendLine($"        (src.FkName, src.ChildSchema, src.ChildTable, src.ChildColumn, src.ParentSchema, src.ParentTable, src.ParentColumn, src.OnDeleteAction, src.OnUpdateAction);");
                sb.AppendLine();
            }

            if (sb.Length == 0)
            {
                return "    -- (no foreign key constraints in assessment after validation)\n";
            }
            return sb.ToString();
        }

        private static string BuildFkScopeTablesSeed(AssessmentResult assessment)
        {
            var scoped = new HashSet<(string Schema, string Table)>();

            foreach (var db in assessment.SqlAssessment.DatabaseMappings)
            {
                foreach (var container in db.ContainerMappings)
                {
                    if (string.IsNullOrWhiteSpace(container.TargetTable)) continue;
                    var schema = string.IsNullOrWhiteSpace(container.TargetSchema) ? "dbo" : container.TargetSchema;
                    scoped.Add((schema, container.TargetTable));

                    foreach (var child in container.ChildTableMappings)
                    {
                        if (string.IsNullOrWhiteSpace(child.TargetTable)) continue;
                        var childSchema = string.IsNullOrWhiteSpace(child.TargetSchema) ? "dbo" : child.TargetSchema;
                        scoped.Add((childSchema, child.TargetTable));
                    }
                }
            }

            if (scoped.Count == 0)
            {
                return "    -- (no migrated tables in assessment; FK scope table is empty)\n";
            }

            var sb = new StringBuilder();
            foreach (var (schema, table) in scoped.OrderBy(x => x.Schema).ThenBy(x => x.Table))
            {
                ValidateIdentifier(schema, "schema");
                ValidateIdentifier(table, "table");
                var schemaLit = EscapeSqlLiteral(schema);
                var tableLit = EscapeSqlLiteral(table);
                sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM dbo.ValidationFkScopeTables WHERE SchemaName = N'{schemaLit}' AND TableName = N'{tableLit}')");
                sb.AppendLine($"        INSERT dbo.ValidationFkScopeTables (SchemaName, TableName) VALUES (N'{schemaLit}', N'{tableLit}');");
            }
            return sb.ToString();
        }

        private static string ResolveSchema(string tableName, IReadOnlyDictionary<string, string> schemaIndex)
            => schemaIndex.TryGetValue(tableName, out var schema) ? schema : "dbo";

        // ------------------------------------------------------------------
        // 05-IndexValidation.sql
        // ------------------------------------------------------------------

        internal async Task<string> GenerateIndexValidationAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("05-IndexValidation.sql");
            var schemaIndex = BuildTableSchemaIndex(assessment);

            var rendered = template
                .Replace("{{ExpectedIndexesSeed}}", BuildExpectedIndexesSeed(assessment, schemaIndex))
                .Replace("{{ScopeTablesSeed}}", BuildFkScopeTablesSeed(assessment));

            var path = Path.Combine(outputDir, "05-IndexValidation.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated index validation: {Path}", path);
            return path;
        }

        private static string BuildExpectedIndexesSeed(
            AssessmentResult assessment,
            IReadOnlyDictionary<string, string> schemaIndex)
        {
            var indexes = assessment.SqlAssessment.IndexRecommendations;
            if (indexes == null || indexes.Count == 0)
            {
                return "    -- (no index recommendations in assessment)\n";
            }

            var sb = new StringBuilder();
            foreach (var idx in indexes)
            {
                if (string.IsNullOrWhiteSpace(idx.IndexName) ||
                    string.IsNullOrWhiteSpace(idx.TableName) ||
                    idx.Columns is null || idx.Columns.Count == 0)
                {
                    continue;
                }

                ValidateIdentifier(idx.IndexName, nameof(idx.IndexName));
                ValidateIdentifier(idx.TableName, nameof(idx.TableName));
                foreach (var c in idx.Columns)
                    ValidateIdentifier(c, "IndexRecommendation.Columns");
                foreach (var c in idx.IncludedColumns ?? new List<string>())
                    ValidateIdentifier(c, "IndexRecommendation.IncludedColumns");

                var (expectedType, isUnique) = NormalizeIndexType(idx.IndexType);

                var schema = ResolveSchema(idx.TableName, schemaIndex);
                ValidateIdentifier(schema, "schema");

                var keyCols = string.Join(", ", idx.Columns);
                var includeCols = string.Join(", ", idx.IncludedColumns ?? new List<string>());

                var schemaLit = EscapeSqlLiteral(schema);
                var tableLit = EscapeSqlLiteral(idx.TableName);
                var nameLit = EscapeSqlLiteral(idx.IndexName);
                var typeLit = EscapeSqlLiteral(expectedType);
                var keyLit = EscapeSqlLiteral(keyCols);
                var incLit = EscapeSqlLiteral(includeCols);
                var uniqueLit = isUnique ? "1" : "0";

                sb.AppendLine($"    MERGE dbo.ValidationExpectedIndexes AS target");
                sb.AppendLine($"    USING (VALUES (N'{schemaLit}', N'{tableLit}', N'{nameLit}',");
                sb.AppendLine($"                   N'{typeLit}', {uniqueLit}, N'{keyLit}', N'{incLit}'))");
                sb.AppendLine($"        AS src(SchemaName, TableName, IndexName, ExpectedType, IsUnique, KeyColumns, IncludedColumns)");
                sb.AppendLine($"        ON target.SchemaName = src.SchemaName AND target.TableName = src.TableName AND target.IndexName = src.IndexName");
                sb.AppendLine($"    WHEN MATCHED THEN UPDATE SET");
                sb.AppendLine($"        ExpectedType = src.ExpectedType, IsUnique = src.IsUnique,");
                sb.AppendLine($"        KeyColumns = src.KeyColumns, IncludedColumns = src.IncludedColumns,");
                sb.AppendLine($"        CapturedAt = SYSUTCDATETIME()");
                sb.AppendLine($"    WHEN NOT MATCHED THEN INSERT");
                sb.AppendLine($"        (SchemaName, TableName, IndexName, ExpectedType, IsUnique, KeyColumns, IncludedColumns)");
                sb.AppendLine($"        VALUES");
                sb.AppendLine($"        (src.SchemaName, src.TableName, src.IndexName, src.ExpectedType, src.IsUnique, src.KeyColumns, src.IncludedColumns);");
                sb.AppendLine();
            }

            if (sb.Length == 0)
            {
                return "    -- (no index recommendations in assessment after validation)\n";
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps the assessment's free-form IndexType string to (ExpectedType, IsUnique)
        /// for the seed table. See plan in PR #228.
        /// </summary>
        public static (string ExpectedType, bool IsUnique) NormalizeIndexType(string indexType)
        {
            if (string.IsNullOrWhiteSpace(indexType))
                return ("NONCLUSTERED", false);

            return indexType.Trim().ToUpperInvariant() switch
            {
                "CLUSTERED"            => ("CLUSTERED", false),
                "NONCLUSTERED"         => ("NONCLUSTERED", false),
                "UNIQUE"               => ("NONCLUSTERED", true),
                "COLUMNSTORE"          => ("NONCLUSTERED COLUMNSTORE", false),
                "CLUSTEREDCOLUMNSTORE" => ("CLUSTERED COLUMNSTORE", false),
                _                      => (indexType.Trim().ToUpperInvariant(), false)
            };
        }

        // ------------------------------------------------------------------
        // 04-PerformanceBaseline.sql
        // ------------------------------------------------------------------

        internal async Task<string> GeneratePerformanceBaselineAsync(
            AssessmentResult assessment,
            string outputDir,
            CancellationToken cancellationToken)
        {
            var template = LoadTemplate("04-PerformanceBaseline.sql");

            var rendered = template
                .Replace("{{ScopeTablesSeed}}", BuildFkScopeTablesSeed(assessment));

            var path = Path.Combine(outputDir, "04-PerformanceBaseline.sql");
            await File.WriteAllTextAsync(path, rendered, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Generated performance baseline: {Path}", path);
            return path;
        }

        // ------------------------------------------------------------------
        // ValidationReport.md.template + ValidationReport.html.template
        // ------------------------------------------------------------------

        internal async Task<List<string>> GenerateValidationReportTemplatesAsync(
            string outputDir,
            CancellationToken cancellationToken)
        {
            var generated = new List<string>(2);
            foreach (var name in new[] { "ValidationReport.md.template", "ValidationReport.html.template" })
            {
                var content = LoadTemplate(name);
                var path = Path.Combine(outputDir, name);
                await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("Generated report template: {Path}", path);
                generated.Add(path);
            }
            return generated;
        }

        // ------------------------------------------------------------------
        // RunAllValidations.ps1 (orchestrator)
        // ------------------------------------------------------------------

        internal async Task<string> GenerateOrchestratorAsync(
            string outputDir,
            CancellationToken cancellationToken)
        {
            var content = LoadTemplate("RunAllValidations.ps1");
            var path = Path.Combine(outputDir, "RunAllValidations.ps1");
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug("Generated orchestrator: {Path}", path);
            return path;
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

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
