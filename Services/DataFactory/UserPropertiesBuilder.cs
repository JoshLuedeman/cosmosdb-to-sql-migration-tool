using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the ADF activity-level <c>userProperties</c> list that flows into the
/// <c>UserProperties</c> custom-dimension column on the <c>ADFActivityRun</c>
/// Log Analytics table (when the diagnostic setting uses
/// <c>logAnalyticsDestinationType = "Dedicated"</c>). Centralising the shape means
/// every Copy activity in every pipeline emits the same monitoring contract.
/// </summary>
public sealed class UserPropertiesBuilder
{
    /// <summary><c>userProperties</c> entry name for the Cosmos DB source database name (resolved at run time from the pipeline parameter).</summary>
    public const string PropSourceDatabase = "SourceDatabase";
    /// <summary><c>userProperties</c> entry name for the Azure SQL target database name (resolved at run time from the pipeline parameter).</summary>
    public const string PropTargetDatabase = "TargetDatabase";
    /// <summary><c>userProperties</c> entry name for the Cosmos DB source container (collection) name.</summary>
    public const string PropSourceContainer = "SourceContainer";
    /// <summary><c>userProperties</c> entry name for the Azure SQL target schema name.</summary>
    public const string PropTargetSchema = "TargetSchema";
    /// <summary><c>userProperties</c> entry name for the Azure SQL target table name.</summary>
    public const string PropTargetTable = "TargetTable";
    /// <summary><c>userProperties</c> entry name that identifies the generating tool; always set to <see cref="MigrationToolMarker"/>.</summary>
    public const string PropMigrationTool = "MigrationTool";

    /// <summary>Literal value stamped into the <see cref="PropMigrationTool"/> user property so Log Analytics queries can filter activities by the tool that generated them.</summary>
    public const string MigrationToolMarker = "CosmosToSqlAssessment";

    /// <summary>
    /// Builds the <c>userProperties</c> list for a single Copy activity.
    /// Database names use ADF Expression objects so the monitoring custom dimension
    /// tracks the values resolved at run time (per-db pipeline parameters from #142),
    /// not assessment-frozen literals. Per-mapping schema / table / container values
    /// stay literal because they identify which copy ran, regardless of environment.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Build(ContainerMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var schema = string.IsNullOrWhiteSpace(mapping.TargetSchema) ? "dbo" : mapping.TargetSchema;
        return new List<Dictionary<string, object?>>
        {
            new()
            {
                ["name"] = PropSourceDatabase,
                ["value"] = new Dictionary<string, object?>
                {
                    ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamCosmosDatabaseName}",
                    ["type"] = "Expression",
                },
            },
            new()
            {
                ["name"] = PropTargetDatabase,
                ["value"] = new Dictionary<string, object?>
                {
                    ["value"] = $"@pipeline().parameters.{ParameterCatalog.PipelineParamSqlDatabaseName}",
                    ["type"] = "Expression",
                },
            },
            new()
            {
                ["name"] = PropSourceContainer,
                ["value"] = mapping.SourceContainer ?? string.Empty,
            },
            new()
            {
                ["name"] = PropTargetSchema,
                ["value"] = schema,
            },
            new()
            {
                ["name"] = PropTargetTable,
                ["value"] = mapping.TargetTable ?? string.Empty,
            },
            new()
            {
                ["name"] = PropMigrationTool,
                ["value"] = MigrationToolMarker,
            },
        };
    }
}
