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
