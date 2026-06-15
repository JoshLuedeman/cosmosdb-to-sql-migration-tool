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
