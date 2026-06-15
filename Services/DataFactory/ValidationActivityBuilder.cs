using CosmosToSqlAssessment.Models;
using CosmosToSqlAssessment.Models.DataFactory;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the (LookupSource, LookupTarget, IfCondition) activity triplet that
/// performs row-count parity validation around a Copy activity (#145).
///
/// Pattern (per Microsoft Learn):
///   LookupSrc → Copy → LookupTgt → IfCondition(expression)
///                                      └ ifFalseActivities: [Fail]
///
/// Caller is responsible for chaining the Copy activity's <c>dependsOn</c> to
/// the LookupSource so it doesn't start before the baseline count is captured.
/// </summary>
public sealed class ValidationActivityBuilder
{
    public const string LookupType = "Lookup";
    public const string IfConditionType = "IfCondition";
    public const string FailType = "Fail";

    public const string CosmosSourceType = "CosmosDbSqlApiSource";
    public const string AzureSqlSourceType = "AzureSqlSource";

    public const string CountColumnName = "docCount";
    public const string ValidationErrorCode = "RowCountValidationFailed";

    public ValidationTriplet Build(
        ContainerMapping mapping,
        string sourceDatasetName,
        string sinkDatasetName,
        string copyActivityName,
        AdfNameRegistry registry,
        ValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatasetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sinkDatasetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyActivityName);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;

        var lookupSrcName = registry.Allocate(
            $"LookupSrc_{mapping.SourceContainer}",
            $"activity|lookupsrc|{mapping.SourceContainer}");
        var lookupTgtName = registry.Allocate(
            $"LookupTgt_{schema}_{mapping.TargetTable}",
            $"activity|lookuptgt|{schema}|{mapping.TargetTable}");
        var ifName = registry.Allocate(
            $"Validate_{mapping.SourceContainer}",
            $"activity|validate|{mapping.SourceContainer}|{schema}|{mapping.TargetTable}");
        var failName = registry.Allocate(
            $"FailValidate_{mapping.SourceContainer}",
            $"activity|failvalidate|{mapping.SourceContainer}|{schema}|{mapping.TargetTable}");

        var lookupSrc = BuildCosmosLookup(lookupSrcName, sourceDatasetName, mapping);
        var lookupTgt = BuildAzureSqlLookup(lookupTgtName, sinkDatasetName, mapping, schema, copyActivityName);
        var ifCondition = BuildIfCondition(ifName, failName, lookupSrcName, lookupTgtName, lookupTgtName, mapping, schema, options);

        return new ValidationTriplet(lookupSrc, lookupTgt, ifCondition, lookupSrcName, lookupTgtName, ifName);
    }

    private static PipelineActivity BuildCosmosLookup(string activityName, string sourceDatasetName, ContainerMapping mapping)
    {
        return new PipelineActivity
        {
            Name = activityName,
            Type = LookupType,
            TypeProperties =
            {
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = CosmosSourceType,
                    // Aliased projection — `firstRow.docCount` is reliably present.
                    ["query"] = $"SELECT COUNT(1) AS {CountColumnName} FROM c",
                },
                ["dataset"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = sourceDatasetName,
                    ["type"] = "DatasetReference",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamDatabaseName] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}",
                        [DatasetBuilder.DatasetParamCollectionName] = mapping.SourceContainer,
                    },
                },
                ["firstRowOnly"] = true,
            },
            Annotations = new List<string>
            {
                $"Pre-copy row count for container '{mapping.SourceContainer}' (#145 validation).",
            },
        };
    }

    private static PipelineActivity BuildAzureSqlLookup(
        string activityName,
        string sinkDatasetName,
        ContainerMapping mapping,
        string schema,
        string copyActivityName)
    {
        var query = $"SELECT COUNT_BIG(1) AS {CountColumnName} FROM {SqlIdentifierEscaper.TwoPart(schema, mapping.TargetTable)}";

        var activity = new PipelineActivity
        {
            Name = activityName,
            Type = LookupType,
            TypeProperties =
            {
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = AzureSqlSourceType,
                    ["sqlReaderQuery"] = query,
                },
                ["dataset"] = new Dictionary<string, object?>
                {
                    ["referenceName"] = sinkDatasetName,
                    ["type"] = "DatasetReference",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        [DatasetBuilder.DatasetParamSqlDatabaseName] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                        [DatasetBuilder.DatasetParamSchema] = schema,
                        [DatasetBuilder.DatasetParamTable] = mapping.TargetTable,
                    },
                },
                ["firstRowOnly"] = true,
            },
            Annotations = new List<string>
            {
                $"Post-copy row count for '{schema}.{mapping.TargetTable}' (#145 validation).",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["dependsOn"] = BuildDependsOn(copyActivityName, "Succeeded"),
            },
        };
        return activity;
    }

    private static PipelineActivity BuildIfCondition(
        string ifName,
        string failName,
        string lookupSrcName,
        string lookupTgtName,
        string lookupTgtDependency,
        ContainerMapping mapping,
        string schema,
        ValidationOptions options)
    {
        var expression = BuildValidationExpression(lookupSrcName, lookupTgtName, options);
        var failActivity = BuildFailActivity(failName, lookupSrcName, lookupTgtName, mapping, schema, options);

        return new PipelineActivity
        {
            Name = ifName,
            Type = IfConditionType,
            TypeProperties =
            {
                ["expression"] = new Dictionary<string, object?>
                {
                    ["value"] = expression,
                    ["type"] = "Expression",
                },
                ["ifFalseActivities"] = new List<object?> { failActivity },
            },
            Annotations = new List<string>
            {
                $"Validate row count parity for '{mapping.SourceContainer}' → '{schema}.{mapping.TargetTable}' (strategy: {options.Strategy}, tolerance: {options.Tolerance}).",
            },
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["dependsOn"] = BuildDependsOn(lookupTgtDependency, "Succeeded"),
            },
        };
    }

    /// <summary>
    /// Builds the validation expression body. <c>int()</c> coercion is applied so
    /// Cosmos's <c>Int32</c> count and SQL's <c>bigint COUNT_BIG</c> compare reliably.
    /// </summary>
    public static string BuildValidationExpression(string lookupSrcName, string lookupTgtName, ValidationOptions options)
    {
        var src = $"activity('{lookupSrcName}').output.firstRow.{CountColumnName}";
        var tgt = $"activity('{lookupTgtName}').output.firstRow.{CountColumnName}";

        return options.Strategy switch
        {
            ValidationStrategy.RowCountExact when options.Tolerance == 0 =>
                $"@equals(int({src}), int({tgt}))",
            ValidationStrategy.RowCountExact =>
                // |src - tgt| <= tolerance
                $"@lessOrEquals(int(abs(sub(int({src}), int({tgt})))), {options.Tolerance})",
            ValidationStrategy.RowCountAtLeast =>
                // tgt >= src - tolerance
                $"@greaterOrEquals(int({tgt}), sub(int({src}), {options.Tolerance}))",
            _ => throw new InvalidOperationException($"Unknown ValidationStrategy '{options.Strategy}'."),
        };
    }

    private static PipelineActivity BuildFailActivity(
        string failName,
        string lookupSrcName,
        string lookupTgtName,
        ContainerMapping mapping,
        string schema,
        ValidationOptions options)
    {
        // Embed user data via @concat with string() coercions, never bare interpolation
        // (raw @{...} inside a JSON string is not evaluated as an Expression).
        var src = $"string(activity('{lookupSrcName}').output.firstRow.{CountColumnName})";
        var tgt = $"string(activity('{lookupTgtName}').output.firstRow.{CountColumnName})";

        var prefix = $"'Row count validation failed for container ''{EscapeAdfStringLiteral(mapping.SourceContainer)}'' -> ''{EscapeAdfStringLiteral(schema)}.{EscapeAdfStringLiteral(mapping.TargetTable)}''. Source: '";
        var middle = $"', Target: '";
        var suffix = $"'. Strategy: {options.Strategy}, Tolerance: {options.Tolerance}.'";

        var messageExpression = $"@concat({prefix}, {src}, {middle}, {tgt}, {suffix})";

        return new PipelineActivity
        {
            Name = failName,
            Type = FailType,
            TypeProperties =
            {
                ["message"] = new Dictionary<string, object?>
                {
                    ["value"] = messageExpression,
                    ["type"] = "Expression",
                },
                ["errorCode"] = ValidationErrorCode,
            },
        };
    }

    /// <summary>
    /// Escapes a string for embedding inside a single-quoted ADF expression literal.
    /// ADF doubles single-quotes for escaping: <c>'</c> → <c>''</c>.
    /// </summary>
    public static string EscapeAdfStringLiteral(string raw)
    {
        return (raw ?? string.Empty).Replace("'", "''");
    }

    private static List<object?> BuildDependsOn(string upstreamActivityName, params string[] conditions)
    {
        return new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["activity"] = upstreamActivityName,
                ["dependencyConditions"] = conditions,
            },
        };
    }

    /// <summary>
    /// Triplet returned by <see cref="Build"/>. Caller adds these to the pipeline
    /// activity list and wires the Copy activity's <c>dependsOn</c> to <see cref="LookupSourceName"/>.
    /// Total activity contribution (including nested Fail inside IfCondition) is 4.
    /// </summary>
    public sealed record ValidationTriplet(
        PipelineActivity LookupSource,
        PipelineActivity LookupTarget,
        PipelineActivity IfCondition,
        string LookupSourceName,
        string LookupTargetName,
        string IfConditionName);
}
