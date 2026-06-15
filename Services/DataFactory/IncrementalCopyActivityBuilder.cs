using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the per-mapping incremental activity group (#147). For each container
/// → table mapping selected for incremental copy, the orchestrator inserts:
///
/// <list type="number">
///   <item><c>LookupWatermark_&lt;m&gt;</c> — Azure SQL Lookup with a literal-mapping-key SELECT against the watermark table.</item>
///   <item><c>SetVariable_LastTs_&lt;m&gt;</c> — Reads the Lookup output into <c>lastWatermark_&lt;m&gt;</c>.</item>
///   <item><c>SetVariable_NewTs_&lt;m&gt;</c> — Computes <c>newWatermark_&lt;m&gt; = max(lastWatermark, unix(utcnow()) - safetyLag)</c>. The clamp prevents safety-lag arithmetic from producing a backwards window.</item>
///   <item>(when validation on) <c>LookupSrc_&lt;c&gt;</c> with incremental WHERE.</item>
///   <item>The Copy activity (passed in pre-built; this builder rewrites <c>typeProperties.source.query</c>).</item>
///   <item><c>ScriptUpdateWatermark_&lt;m&gt;</c> — Script (NonQuery) running the MERGE with backwards-protection guard, depends on Copy Succeeded.</item>
/// </list>
///
/// Mapping key format: <c>&lt;srcDb&gt;::&lt;srcContainer&gt;-&gt;[&lt;tgtDb&gt;].[&lt;schema&gt;].[&lt;table&gt;]</c>.
/// This composite key prevents same-named containers across different source
/// databases (or fan-out to multiple sink tables) from corrupting each other's
/// watermark — see rubber-duck Blocker B5.
/// </summary>
public sealed class IncrementalCopyActivityBuilder
{
    public const string LookupType = "Lookup";
    public const string SetVariableType = "SetVariable";
    public const string ScriptType = "Script";
    public const string AzureSqlSourceType = "AzureSqlSource";

    /// <summary>Watermark column name returned by the Lookup. Stable so the SetVariable expression can reference it.</summary>
    public const string LookupOutputColumn = "lastTs";

    private readonly WatermarkSchemaBuilder _schemaBuilder;

    public IncrementalCopyActivityBuilder(WatermarkSchemaBuilder? schemaBuilder = null)
    {
        _schemaBuilder = schemaBuilder ?? new WatermarkSchemaBuilder();
    }

    /// <summary>
    /// Builds the activity group around <paramref name="copy"/>. Returns names and
    /// variables the orchestrator needs to wire up at the pipeline level.
    /// </summary>
    public IncrementalGroup BuildGroup(
        ContainerMapping mapping,
        string sourceDatabaseName,
        string targetDatabaseName,
        string azureSqlLinkedServiceName,
        string watermarkDatasetName,
        PipelineActivity copy,
        AdfNameRegistry registry,
        IncrementalCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(azureSqlLinkedServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermarkDatasetName);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;
        var mappingKey = BuildMappingKey(sourceDatabaseName, mapping.SourceContainer, targetDatabaseName, schema, mapping.TargetTable);
        var sanitisedMappingKey = AdfNameRegistry.Sanitize(mappingKey);

        // Pipeline parameter, variable, activity names — all derived from the same sanitised key.
        var initialWatermarkParam = $"incrementalInitialWatermark_{sanitisedMappingKey}";
        var lastWatermarkVar = $"lastWatermark_{sanitisedMappingKey}";
        var newWatermarkVar = $"newWatermark_{sanitisedMappingKey}";

        var collisionStem = $"incremental|{mappingKey}";
        var lookupName = registry.Allocate($"LookupWatermark_{sanitisedMappingKey}", $"{collisionStem}|lookup");
        var setLastTsName = registry.Allocate($"SetLastTs_{sanitisedMappingKey}", $"{collisionStem}|setlast");
        var setNewTsName = registry.Allocate($"SetNewTs_{sanitisedMappingKey}", $"{collisionStem}|setnew");
        var scriptUpdateName = registry.Allocate($"ScriptUpdateWatermark_{sanitisedMappingKey}", $"{collisionStem}|script");

        // The Cosmos source query uses ADF interpolation against the two variables;
        // both are strings, so we wrap with int(...) so Cosmos's `_ts > ...` compares
        // numbers rather than strings (lexicographic comparison would fail at 10-digit
        // vs 11-digit timestamp boundaries in 2286).
        var incrementalQuery = BuildIncrementalCosmosQuery(options.TimestampField, lastWatermarkVar, newWatermarkVar);
        OverrideCopySourceQuery(copy, incrementalQuery);

        // Wire dependsOn chain: SetLastTs ← LookupWatermark, SetNewTs ← SetLastTs,
        // Copy ← SetNewTs (merged into Copy.AdditionalProperties so #143 policy +
        // #144 userProperties + any #145 dependsOn already there are preserved),
        // ScriptUpdate ← Copy.
        var lookup = BuildLookupWatermarkActivity(
            lookupName, mapping, watermarkDatasetName, mappingKey, initialWatermarkParam, options);

        var setLastTs = BuildSetVariable(
            setLastTsName,
            lastWatermarkVar,
            // string() coercion so SetVariable's String variable accepts the int value.
            // The ADF Lookup returns its single row in `firstRow.lastTs`.
            $"@string(activity('{lookupName}').output.firstRow.{LookupOutputColumn})",
            dependsOnActivity: lookupName);

        var setNewTs = BuildSetVariable(
            setNewTsName,
            newWatermarkVar,
            BuildNewWatermarkExpression(lastWatermarkVar, options.WatermarkSafetyLagSeconds),
            dependsOnActivity: setLastTsName);

        MergeDependsOn(copy, setNewTsName);

        var scriptUpdate = BuildScriptUpdateActivity(
            scriptUpdateName, azureSqlLinkedServiceName,
            mappingKey, newWatermarkVar, options, dependsOnActivity: copy.Name);

        return new IncrementalGroup(
            lookup, setLastTs, setNewTs, scriptUpdate,
            mappingKey, sanitisedMappingKey,
            initialWatermarkParam, lastWatermarkVar, newWatermarkVar,
            lookupName, setLastTsName, setNewTsName, scriptUpdateName);
    }

    /// <summary>
    /// Builds the composite mapping key used as the watermark table's PK and
    /// baked into every per-mapping Lookup / Script as a literal. Sanitisation is
    /// applied to component values that could otherwise carry separator characters.
    /// </summary>
    public static string BuildMappingKey(string sourceDatabase, string sourceContainer, string targetDatabase, string targetSchema, string targetTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContainer);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTable);

        // Use a recognisable, deterministic, human-readable shape; truncate component
        // values to keep the total length within NVARCHAR(450) under extreme inputs.
        static string Trim(string s) => s.Length > 80 ? s[..80] : s;

        return $"{Trim(sourceDatabase)}::{Trim(sourceContainer)}->[{Trim(targetDatabase)}].[{Trim(targetSchema)}].[{Trim(targetTable)}]";
    }

    /// <summary>
    /// Builds the Cosmos source query for an incremental copy. Both bounds are
    /// integer-coerced so Cosmos compares numbers (avoids lexicographic compare
    /// bugs at digit-count boundaries).
    /// </summary>
    public static string BuildIncrementalCosmosQuery(string timestampField, string lastWatermarkVariable, string newWatermarkVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampField);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastWatermarkVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(newWatermarkVariable);

        // ADF expression interpolation: @{int(variables('X'))} renders the integer
        // value into the source query string at activity-run time. We use `_ts` (or
        // operator-overridden) as a system field so the query is `c.<field>`.
        return
            $"SELECT * FROM c WHERE c.{timestampField} > @{{int(variables('{lastWatermarkVariable}'))}} " +
            $"AND c.{timestampField} <= @{{int(variables('{newWatermarkVariable}'))}}";
    }

    /// <summary>
    /// Builds the source-count Lookup query (#145 validation interaction). Same
    /// WHERE clause as the Copy so the count reflects only the delta window.
    /// </summary>
    public static string BuildIncrementalCountQuery(string timestampField, string lastWatermarkVariable, string newWatermarkVariable, string countColumnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampField);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastWatermarkVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(newWatermarkVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(countColumnName);

        return
            $"SELECT COUNT(1) AS {countColumnName} FROM c WHERE c.{timestampField} > @{{int(variables('{lastWatermarkVariable}'))}} " +
            $"AND c.{timestampField} <= @{{int(variables('{newWatermarkVariable}'))}}";
    }

    /// <summary>
    /// Builds the EnsureWatermarkTable Script activity for the start of a per-db
    /// pipeline (run once at pipeline.activities position 0). Caller wires every
    /// incremental group's <c>LookupWatermark</c> activity to <c>dependsOn</c> it.
    /// </summary>
    public PipelineActivity BuildEnsureTableActivity(
        string azureSqlLinkedServiceName,
        AdfNameRegistry registry,
        IncrementalCopyOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(azureSqlLinkedServiceName);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var name = registry.Allocate("EnsureWatermarkTable", "incremental|ensure|watermark");
        var ddl = _schemaBuilder.BuildCreateScript(options);

        return new PipelineActivity
        {
            Name = name,
            Type = ScriptType,
            TypeProperties =
            {
                ["scripts"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "NonQuery",
                        ["text"] = ddl,
                    },
                },
            },
            Annotations = new List<string>
            {
                "Self-bootstrap the watermark table on first run (#147).",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["linkedServiceName"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = azureSqlLinkedServiceName,
                    ["type"] = "LinkedServiceReference",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamSqlDatabaseName] =
                            $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                    },
                },
            },
        };
    }

    private static PipelineActivity BuildLookupWatermarkActivity(
        string activityName,
        ContainerMapping mapping,
        string watermarkDatasetName,
        string mappingKey,
        string initialWatermarkParam,
        IncrementalCopyOptions options)
    {
        // The Lookup uses the shared watermark dataset (built by DatasetBuilder.BuildWatermarkDataset);
        // sqlReaderQuery overrides the dataset's table reference with the literal-mapping-key
        // SELECT. The bootstrap value is interpolated from the pipeline parameter at run time so
        // operators can override per environment without re-generating SQL.
        var schemaBuilder = new WatermarkSchemaBuilder();
        var selectScriptBody = schemaBuilder.BuildSelectScript(
            options,
            mappingKey,
            // The ADF expression segment that yields the initial bootstrap.
            // We concat with the SELECT later via @concat so the param value is
            // interpolated.
            "INITIAL_PLACEHOLDER");

        // Replace the placeholder with a string-concat segment so the final expression
        // is: '<sql prefix>' + pipeline().parameters.<param> + '<sql suffix>'.
        // This keeps the literal-mapping-key part baked at generation time while letting
        // the bootstrap value flow from the parameter file.
        var prefix = selectScriptBody[..selectScriptBody.IndexOf("INITIAL_PLACEHOLDER", StringComparison.Ordinal)];
        var suffix = selectScriptBody[(selectScriptBody.IndexOf("INITIAL_PLACEHOLDER", StringComparison.Ordinal) + "INITIAL_PLACEHOLDER".Length)..];
        var prefixEscaped = SqlLiteralEscaper.Escape(prefix);
        var suffixEscaped = SqlLiteralEscaper.Escape(suffix);
        var concatExpr = $"@concat('{prefixEscaped}', string(pipeline().parameters.{initialWatermarkParam}), '{suffixEscaped}')";

        return new PipelineActivity
        {
            Name = activityName,
            Type = LookupType,
            TypeProperties =
            {
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = AzureSqlSourceType,
                    ["sqlReaderQuery"] = new Dictionary<string, object?>
                    {
                        ["value"] = concatExpr,
                        ["type"] = "Expression",
                    },
                },
                ["dataset"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = watermarkDatasetName,
                    ["type"] = "DatasetReference",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamSqlDatabaseName] =
                            $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                    },
                },
                ["firstRowOnly"] = true,
            },
            Annotations = new List<string>
            {
                $"Read current watermark for mapping '{mapping.SourceContainer}' → '{mapping.TargetSchema}.{mapping.TargetTable}' (#147).",
            },
        };
    }

    private static PipelineActivity BuildSetVariable(string activityName, string variableName, string valueExpression, string dependsOnActivity)
    {
        return new PipelineActivity
        {
            Name = activityName,
            Type = SetVariableType,
            TypeProperties =
            {
                ["variableName"] = variableName,
                ["value"] = new Dictionary<string, object?>
                {
                    ["value"] = valueExpression,
                    ["type"] = "Expression",
                },
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["dependsOn"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["activity"] = dependsOnActivity,
                        ["dependencyConditions"] = new[] { "Succeeded" },
                    },
                },
            },
        };
    }

    private PipelineActivity BuildScriptUpdateActivity(
        string activityName,
        string azureSqlLinkedServiceName,
        string mappingKey,
        string newWatermarkVariable,
        IncrementalCopyOptions options,
        string dependsOnActivity)
    {
        // MERGE body with backwards-protection guard. The `<NEW_WATERMARK>` placeholder
        // is replaced with the integer-coerced ADF expression so the BIGINT column is
        // assigned correctly (Cosmos `_ts` is seconds; we always pass it as an int).
        var scriptBody = _schemaBuilder.BuildUpdateScript(
            options,
            mappingKey,
            $"CAST(@{{int(variables('{newWatermarkVariable}'))}} AS BIGINT)");

        return new PipelineActivity
        {
            Name = activityName,
            Type = ScriptType,
            TypeProperties =
            {
                ["scripts"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "NonQuery",
                        ["text"] = new Dictionary<string, object?>
                        {
                            ["value"] = scriptBody,
                            ["type"] = "Expression",
                        },
                    },
                },
            },
            Annotations = new List<string>
            {
                $"Advance watermark for mapping key '{mappingKey}' (#147).",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["linkedServiceName"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = azureSqlLinkedServiceName,
                    ["type"] = "LinkedServiceReference",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamSqlDatabaseName] =
                            $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                    },
                },
                ["dependsOn"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["activity"] = dependsOnActivity,
                        ["dependencyConditions"] = new[] { "Succeeded" },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Computes the new watermark with two protections:
    /// (a) <c>safety lag</c> — subtract the lag from <c>utcnow()</c> so boundary-second
    /// writes are picked up by the next run rather than skipped.
    /// (b) clamp to <c>lastWatermark</c> so safety-lag arithmetic can never push
    /// <c>newWatermark</c> below <c>lastWatermark</c> (which would skip data forever).
    /// </summary>
    public static string BuildNewWatermarkExpression(string lastWatermarkVariable, int safetyLagSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastWatermarkVariable);
        if (safetyLagSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safetyLagSeconds), "Safety lag must be ≥ 0 seconds.");
        }

        // ADF expression building blocks:
        //   unix-now = div(sub(ticks(utcnow()), ticks('1970-01-01')), 10000000)
        //   candidate = sub(unix-now, lag)
        //   clamped = max(candidate, int(variables('lastWatermark')))
        // SetVariable expects a string-typed variable, so we wrap with string().
        var unixNow = "div(sub(ticks(utcnow()), ticks('1970-01-01')), 10000000)";
        var candidate = $"sub({unixNow}, {safetyLagSeconds})";
        var clamped = $"max({candidate}, int(variables('{lastWatermarkVariable}')))";
        return $"@string({clamped})";
    }

    /// <summary>
    /// Rewrites <c>copy.TypeProperties.source.query</c> to <paramref name="query"/>.
    /// Idempotent — repeated calls just overwrite the value. Used so the orchestrator
    /// can build a normal Copy via <see cref="CopyActivityBuilder"/> and have us layer
    /// incremental semantics on top without forking the Copy code path.
    /// </summary>
    public static void OverrideCopySourceQuery(PipelineActivity copy, string query)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (!copy.TypeProperties.TryGetValue("source", out var sourceObj)
            || sourceObj is not Dictionary<string, object?> source)
        {
            throw new InvalidOperationException(
                $"Copy activity '{copy.Name}' has no `source` typeProperty — cannot override its query.");
        }

        source["query"] = query;
    }

    /// <summary>
    /// Merges <c>dependsOn</c> into <paramref name="activity"/>'s extension bag so
    /// #143 policy / #144 userProperties / #145 dependsOn entries already present
    /// are preserved (rubber-duck on #145 caught the equivalent bug for validation).
    /// </summary>
    public static void MergeDependsOn(PipelineActivity activity, string dependsOnActivity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependsOnActivity);

        activity.AdditionalProperties ??= new Dictionary<string, object?>();
        var entry = new Dictionary<string, object?>
        {
            ["activity"] = dependsOnActivity,
            ["dependencyConditions"] = new[] { "Succeeded" },
        };

        if (activity.AdditionalProperties.TryGetValue("dependsOn", out var existing)
            && existing is IEnumerable<object?> existingList)
        {
            var merged = new List<object?>(existingList) { entry };
            activity.AdditionalProperties["dependsOn"] = merged;
        }
        else
        {
            activity.AdditionalProperties["dependsOn"] = new List<object?> { entry };
        }
    }
}

/// <summary>
/// Per-mapping activity group produced by <see cref="IncrementalCopyActivityBuilder.BuildGroup"/>.
/// Names + variables surface to the orchestrator so the per-db pipeline can declare
/// matching parameters, variables, and (optionally) a parameter-template entry.
/// </summary>
public sealed record IncrementalGroup(
    PipelineActivity LookupWatermark,
    PipelineActivity SetLastTs,
    PipelineActivity SetNewTs,
    PipelineActivity ScriptUpdateWatermark,
    string MappingKey,
    string SanitisedMappingKey,
    string InitialWatermarkParameterName,
    string LastWatermarkVariableName,
    string NewWatermarkVariableName,
    string LookupActivityName,
    string SetLastTsActivityName,
    string SetNewTsActivityName,
    string ScriptUpdateActivityName);
