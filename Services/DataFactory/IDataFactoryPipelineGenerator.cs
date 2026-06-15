using CosmosToSqlAssessment.Models;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Generates ready-to-deploy Azure Data Factory artifacts (linked services, datasets,
/// pipelines) from an <see cref="AssessmentResult"/>. Foundational sub-issue #141 of
/// parent #70: copy activities per container → table mapping. Later sub-issues extend
/// the output with parameters, retry policy, monitoring, validation, ARM templating,
/// and incremental-load support.
/// </summary>
public interface IDataFactoryPipelineGenerator
{
    Task<DataFactoryGenerationResult> GenerateAsync(
        AssessmentResult assessment,
        string outputDirectory,
        DataFactoryGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Behaviour toggles for the ADF generator. Defaults match the foundational #141 scope
/// extended by #142 (parameterised, environment-agnostic artifacts); follow-on
/// sub-issues add new properties (never replace existing).
/// </summary>
public sealed class DataFactoryGenerationOptions
{
    /// <summary>
    /// SQL sink write behaviour. <see cref="SinkWriteBehavior.Insert"/> for first-load
    /// migrations (#141 default). Upsert will be wired up once target-key metadata is
    /// flowed through, in #147 / parent #69.
    /// </summary>
    public SinkWriteBehavior WriteBehavior { get; init; } = SinkWriteBehavior.Insert;

    /// <summary>
    /// Maximum copy activities per pipeline file. ADF's documented per-pipeline activity
    /// ceiling is 40; we honour the same default to keep generated pipelines deployable.
    /// </summary>
    public int MaxActivitiesPerPipeline { get; init; } = 40;

    /// <summary>
    /// When <c>true</c> the Cosmos linked service uses System-Assigned Managed Identity
    /// (modern recommended shape with <c>accountEndpoint</c> + <c>database</c>) instead
    /// of a key-based connection string. Default <c>true</c>. Requires the Cosmos DB
    /// Built-in Data Contributor role on the factory MI.
    /// </summary>
    public bool UseManagedIdentityForCosmos { get; init; } = true;

    /// <summary>
    /// When <c>true</c> the Azure SQL linked service uses
    /// <c>authenticationType = SystemAssignedManagedIdentity</c> (modern recommended
    /// shape). Default <c>true</c>. Requires an AAD <c>EXTERNAL PROVIDER</c> user for
    /// the factory MI in the target database.
    /// </summary>
    public bool UseManagedIdentityForSql { get; init; } = true;

    /// <summary>
    /// Opt-in Key Vault wiring. When <c>true</c>, an <c>AzureKeyVault</c> linked
    /// service is emitted and any non-MI auth on Cosmos / SQL pulls secrets from it.
    /// Default <c>false</c>: with MI on by default, AKV adds noise and a deployment
    /// dependency the operator may not need.
    /// </summary>
    public bool UseAzureKeyVault { get; init; } = false;

    /// <summary>
    /// Per-Copy-activity ADF <c>policy</c> block (#143). When <see cref="CopyActivityPolicy.Retry"/>
    /// is <c>null</c> (default), the orchestrator derives a safe value from
    /// <see cref="WriteBehavior"/>: 0 for non-idempotent <c>Insert</c>, 3 for upsert.
    /// </summary>
    public CopyActivityPolicy CopyPolicy { get; init; } = new();

    /// <summary>
    /// Per-<c>ExecutePipeline</c> ADF <c>policy</c> block (#143). Master pipeline activities
    /// default to <c>Retry = 0</c> so we don't double-count copy-level retries.
    /// </summary>
    public ExecutePipelinePolicy ExecutePipelinePolicy { get; init; } = new();

    /// <summary>
    /// Copy-activity fault tolerance (#143). Default disabled — skipping incompatible rows
    /// silently can mask data-loss bugs in a migration. Opt in only when the operator has
    /// a log sink ready and accepts row drops.
    /// </summary>
    public FaultToleranceOptions FaultTolerance { get; init; } = new();

    /// <summary>
    /// When <c>true</c> the master pipeline gains a <c>Web</c> + <c>Fail</c> pair per
    /// <c>ExecutePipeline</c> activity, posting to <c>failureNotificationWebhookUrl</c> on
    /// failure and then re-throwing. Default <c>false</c>. The Web activity has
    /// <c>policy.secureInput/secureOutput</c> set so the webhook URL never enters run history.
    /// </summary>
    public bool EmitFailureNotification { get; init; } = false;

    /// <summary>
    /// When <c>true</c> (and <see cref="EmitFailureNotification"/> is also <c>true</c>) per-db
    /// pipelines also get a <c>Web</c> + <c>Fail</c> pair on every <c>Copy</c> failure.
    /// Default <c>false</c> — keeps the per-db pipeline activity count predictable.
    /// </summary>
    public bool PerCopyFailureNotification { get; init; } = false;

    /// <summary>
    /// Monitoring + Log Analytics output controls (#144). When enabled (default), every
    /// Copy activity gains a <c>userProperties</c> block and a stand-alone ARM template
    /// for <c>Microsoft.Insights/diagnosticSettings</c> is written under
    /// <c>ADF/Monitoring/</c>.
    /// </summary>
    public MonitoringOptions Monitoring { get; init; } = new();

    /// <summary>
    /// Row-count parity validation (#145). When enabled (default) every Copy activity
    /// is bracketed by a pre-copy <c>Lookup</c> on the source and a post-copy
    /// <c>Lookup</c> + <c>IfCondition</c> on the target; mismatches throw a <c>Fail</c>
    /// activity (with a useful message) that propagates up to the master pipeline so
    /// the #143 failure-notification channel fires.
    /// </summary>
    public ValidationOptions Validation { get; init; } = new();

    /// <summary>
    /// When <c>true</c> (default) the generator emits a deployable ARM template
    /// (<c>ADF/arm-template.json</c>) wrapping every linked service, dataset, and
    /// pipeline as <c>Microsoft.DataFactory/factories/{kind}</c> child resources (#146).
    /// The same <c>adf-parameters.template.json</c> file produced by the assessment
    /// works as the operator's <c>-TemplateParameterFile</c> input.
    /// </summary>
    public bool EmitArmTemplate { get; init; } = true;

    /// <summary>
    /// Incremental load configuration (#147). Default <see cref="IncrementalCopyOptions.Enabled"/>
    /// is <c>false</c> — the generator emits the same full-load artifacts as #146.
    /// When opted in, the orchestrator wraps each per-mapping Copy in a
    /// Lookup → SetVariable → Copy(query-override) → Script chain that maintains
    /// a <c>_ts</c> watermark per source-container → target-table mapping. The
    /// <see cref="IncrementalCopyMode"/> enum reserves a future
    /// <c>ChangeFeedDataFlow</c> slot for parent #69 to layer on a native
    /// change-feed Mapping Data Flow without breaking the option surface.
    /// </summary>
    public IncrementalCopyOptions IncrementalCopy { get; init; } = new();
}

/// <summary>
/// ADF <c>policy</c> shape applied to every emitted Copy activity (#143).
/// </summary>
public sealed record CopyActivityPolicy
{
    /// <summary>HH:MM:SS or DD.HH:MM:SS. Default <c>12:00:00</c> (12 hours).</summary>
    public string Timeout { get; init; } = "12:00:00";

    /// <summary>
    /// Retry count. When <c>null</c>, the orchestrator derives it from
    /// <see cref="DataFactoryGenerationOptions.WriteBehavior"/>: 0 for Insert (non-idempotent),
    /// 3 for Upsert.
    /// </summary>
    public int? Retry { get; init; } = null;

    public int RetryIntervalInSeconds { get; init; } = 30;
    public bool SecureInput { get; init; } = false;
    public bool SecureOutput { get; init; } = false;
}

/// <summary>
/// ADF <c>policy</c> shape applied to every emitted <c>ExecutePipeline</c> activity (#143).
/// </summary>
public sealed record ExecutePipelinePolicy
{
    /// <summary>Default <c>1.00:00:00</c> (1 day).</summary>
    public string Timeout { get; init; } = "1.00:00:00";
    public int Retry { get; init; } = 0;
    public int RetryIntervalInSeconds { get; init; } = 30;
    public bool SecureInput { get; init; } = false;
    public bool SecureOutput { get; init; } = false;
}

/// <summary>
/// Copy-activity fault tolerance options (#143).
/// </summary>
public sealed record FaultToleranceOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Literal name of a storage linked service the operator has already provisioned.
    /// When <c>null</c> and <see cref="Enabled"/> is <c>true</c>, <c>logSettings</c> is
    /// omitted and a warning is recorded.
    /// </summary>
    public string? LogStorageLinkedServiceName { get; init; } = null;

    public string LogLevel { get; init; } = "Warning";
}

public enum SinkWriteBehavior
{
    Insert,
    Upsert,
}

/// <summary>
/// Monitoring &amp; logging configuration (#144). Defaults are deployment-safe:
/// the diagnostic-settings ARM template is emitted, every Copy activity gets
/// monitoring-friendly <c>userProperties</c>, and the KQL cheat-sheet is written
/// out so operators can drop the queries into Log Analytics on day one.
/// </summary>
public sealed record MonitoringOptions
{
    /// <summary>
    /// When <c>true</c>, the generator emits
    /// <c>ADF/Monitoring/diagnostic-settings.template.json</c> (a deployable ARM
    /// template attaching <c>Microsoft.Insights/diagnosticSettings</c> to the
    /// factory) and seeds the corresponding parameter placeholders into the
    /// <c>adf-parameters.template.json</c> file.
    /// </summary>
    public bool EmitDiagnosticSettingsTemplate { get; init; } = true;

    /// <summary>
    /// Name of the emitted diagnostic setting (becomes the <c>diagnosticSettingName</c>
    /// parameter default in the template). Must be unique per factory.
    /// </summary>
    public string DiagnosticSettingName { get; init; } = "migration-diagnostics";

    /// <summary>
    /// Diagnostic log categories to enable. When <c>null</c> or empty, defaults to
    /// <see cref="DiagnosticSettingsTemplateBuilder.DefaultLogCategories"/>
    /// (<c>PipelineRuns</c>, <c>ActivityRuns</c>, <c>TriggerRuns</c>). Unknown
    /// category names are rejected at generation time.
    /// </summary>
    public IReadOnlyList<string>? LogCategories { get; init; } = null;

    /// <summary>
    /// When <c>true</c> the diagnostic setting also enables the <c>AllMetrics</c>
    /// category. Default <c>true</c>.
    /// </summary>
    public bool EmitAllMetrics { get; init; } = true;

    /// <summary>
    /// When <c>true</c> every Copy activity gets a <c>userProperties</c> block
    /// that surfaces source / target identifiers as Log Analytics custom dimensions.
    /// Default <c>true</c>.
    /// </summary>
    public bool EmitUserProperties { get; init; } = true;

    /// <summary>
    /// When <c>true</c> a KQL cheat-sheet (<c>ADF/Monitoring/monitoring-queries.kql</c>)
    /// is emitted alongside the diagnostic settings template. Default <c>true</c>.
    /// </summary>
    public bool EmitMonitoringQueriesCheatsheet { get; init; } = true;

    /// <summary>
    /// Extra ADF Studio annotations applied to every per-database pipeline and the
    /// master orchestrator pipeline (in addition to the existing migration annotations).
    /// Useful for tagging by environment, team or workload. Default empty.
    /// </summary>
    public IReadOnlyList<string>? ExtraAnnotations { get; init; } = null;
}

/// <summary>
/// Row-count validation configuration (#145). Defaults are conservative: validation
/// is enabled with strict equality and zero tolerance. Operators that expect drift
/// (e.g. mid-load Cosmos writes, soft-deleted rows) can opt down to
/// <see cref="ValidationStrategy.RowCountAtLeast"/> with a tolerance budget.
/// </summary>
public sealed record ValidationOptions
{
    /// <summary>When <c>false</c>, no Lookup/IfCondition/Fail activities are emitted.</summary>
    public bool Enabled { get; init; } = true;

    public ValidationStrategy Strategy { get; init; } = ValidationStrategy.RowCountExact;

    /// <summary>
    /// Allowed delta. For <see cref="ValidationStrategy.RowCountExact"/> it is the
    /// max <c>|source - target|</c>; for <see cref="ValidationStrategy.RowCountAtLeast"/>
    /// it is the max acceptable shortfall (<c>target &gt;= source - tolerance</c>).
    /// Must be ≥ 0.
    /// </summary>
    public long Tolerance { get; init; } = 0;

    /// <summary>
    /// When set, containers whose <c>EstimatedRowCount</c> exceeds this threshold are
    /// skipped: the validation triplet is omitted (Cosmos full-table <c>COUNT(1)</c> can
    /// cost a lot of RU) and a warning is recorded. <c>null</c> (default) = always validate.
    /// </summary>
    public long? SkipForContainerDocumentCountAbove { get; init; } = null;
}

/// <summary>
/// Row-count validation comparison strategy (#145).
/// </summary>
public enum ValidationStrategy
{
    /// <summary><c>|source - target| &lt;= tolerance</c>. Default.</summary>
    RowCountExact,

    /// <summary><c>target &gt;= source - tolerance</c>. Use when the source can grow during the load.</summary>
    RowCountAtLeast,
}

/// <summary>
/// Result returned by <see cref="IDataFactoryPipelineGenerator.GenerateAsync"/>.
/// Surfaces the list of files written and any warnings the operator should review
/// (e.g. transformed fields skipped, child tables deferred, pipeline chunking applied).
/// </summary>
public sealed class DataFactoryGenerationResult
{
    public List<string> GeneratedFiles { get; } = new();
    public List<string> Warnings { get; } = new();

    public int PipelineCount { get; set; }
    public int CopyActivityCount { get; set; }
    public int DatasetCount { get; set; }
    public int LinkedServiceCount { get; set; }
}

/// <summary>
/// Incremental copy configuration (#147). Default <see cref="Enabled"/> is <c>false</c>
/// so output is byte-identical to the post-#146 baseline unless the operator opts in.
///
/// When enabled, each per-mapping Copy is wrapped in a
/// <c>LookupWatermark → SetVariable_LastTs → SetVariable_NewTs → [LookupSrc] → Copy → ScriptUpdateWatermark</c>
/// chain. The Copy's <c>typeProperties.source.query</c> is overridden with a
/// <c>WHERE c._ts &gt; lastTs AND c._ts &lt;= newTs</c> predicate. The newTs is
/// clamped to <c>utcnow() - WatermarkSafetyLagSeconds</c> to avoid losing
/// documents committed within the boundary second (Cosmos <c>_ts</c> is second-granular).
///
/// Mapping Data Flow change-feed support (the second Microsoft-documented incremental
/// pattern, exposed as <c>enableChangeFeed</c> / <c>startFromBeginning</c> on the
/// data-flow source) is intentionally NOT in this sub-issue. The
/// <see cref="IncrementalCopyMode.ChangeFeedDataFlow"/> enum slot is reserved for
/// parent #69 to add later without breaking the option surface.
/// </summary>
public sealed record IncrementalCopyOptions
{
    /// <summary>When <c>false</c> (default) no incremental artifacts are emitted.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Incremental implementation strategy. Only <see cref="IncrementalCopyMode.TimestampWatermark"/> is implemented in #147.</summary>
    public IncrementalCopyMode Mode { get; init; } = IncrementalCopyMode.TimestampWatermark;

    /// <summary>SQL schema for the watermark table. Default <c>dbo</c>. Validated through <see cref="SqlIdentifierEscaper"/>.</summary>
    public string WatermarkSchemaName { get; init; } = "dbo";

    /// <summary>SQL table name for watermark storage. Default <c>__AdfWatermark</c>. Validated through <see cref="SqlIdentifierEscaper"/>.</summary>
    public string WatermarkTableName { get; init; } = "__AdfWatermark";

    /// <summary>Cosmos timestamp field. Default <c>_ts</c> (Unix seconds, server-set on insert/update). Changing this is reserved for future use.</summary>
    public string TimestampField { get; init; } = "_ts";

    /// <summary>Initial bootstrap watermark (Unix seconds) when no row exists in the watermark table. <c>0</c> (default) = from beginning.</summary>
    public long InitialWatermarkSeconds { get; init; } = 0;

    /// <summary>
    /// Safety lag in seconds subtracted from <c>utcnow()</c> when computing the new
    /// watermark, to absorb Cosmos <c>_ts</c> second-granularity, clock skew, and
    /// read-region replication delay (rubber-duck Blocker B2). Default <c>60</c>.
    /// At most this many seconds of recent data is invisible to a given run.
    /// </summary>
    public int WatermarkSafetyLagSeconds { get; init; } = 60;

    /// <summary>When <c>true</c> (default), emit <c>ADF/SQL/Create__AdfWatermark.sql</c> for external pre-deploy DDL.</summary>
    public bool EmitWatermarkSchemaScript { get; init; } = true;

    /// <summary>
    /// When <c>true</c> (default), every per-database pipeline starts with an
    /// <c>EnsureWatermarkTable</c> Script activity that runs the same idempotent
    /// DDL — so the pipeline is self-bootstrapping and the operator does not have
    /// to pre-run the DDL file before the first migration run (rubber-duck Blocker B6).
    /// </summary>
    public bool EnsureWatermarkTableAtRuntime { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, throws at generation time if
    /// <see cref="DataFactoryGenerationOptions.WriteBehavior"/> is
    /// <see cref="SinkWriteBehavior.Insert"/> while incremental is enabled — Insert
    /// is not idempotent and can duplicate rows on Script-update failure / Copy retry
    /// / boundary-second re-read (rubber-duck Blocker B3). Default <c>false</c>: a
    /// warning is emitted instead so operators using staging + MERGE workflows are
    /// not blocked.
    /// </summary>
    public bool RequireUpsertSink { get; init; } = false;

    /// <summary>
    /// Case-insensitive allow-list of source-container names that should be incremental.
    /// Empty (default) means all containers are incremental. When non-empty, only the
    /// listed containers get the incremental treatment; the rest stay full-load.
    /// </summary>
    public IReadOnlyList<string> ContainerAllowList { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Incremental copy implementation strategy (#147). Only <see cref="TimestampWatermark"/>
/// is implemented in this sub-issue. The <see cref="ChangeFeedDataFlow"/> slot is
/// reserved for parent #69 to add a Mapping-Data-Flow-based change-feed implementation
/// alongside, without breaking the public option surface.
/// </summary>
public enum IncrementalCopyMode
{
    /// <summary>
    /// Cosmos <c>_ts</c>-based watermarking via Copy Activity. Per the
    /// <see href="https://learn.microsoft.com/en-us/azure/data-factory/connector-azure-cosmos-db">Microsoft Learn ADF Cosmos DB connector docs</see>,
    /// <c>enableChangeFeed</c> / <c>startFromBeginning</c> are Mapping Data Flow only, so
    /// Copy Activity incremental relies on a <c>WHERE c._ts &gt; lastTs AND c._ts &lt;= newTs</c>
    /// query against a per-mapping watermark stored in Azure SQL.
    /// </summary>
    TimestampWatermark = 0,

    // Reserved for parent #69:
    // ChangeFeedDataFlow = 1,
}
