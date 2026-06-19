using CosmosToSqlAssessment.Models.Monitoring;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Publishes migration custom-metric points to a sink (Azure Monitor in production).
/// Implementations must be resilient: a publish failure should never abort the caller's
/// progress stream.
/// </summary>
public interface IMigrationMetricPublisher
{
    /// <summary>
    /// Publishes a batch of metric points. Implementations may group, batch, or no-op
    /// (e.g. when disabled). Must not throw on transient sink failures.
    /// </summary>
    /// <param name="metrics">The metric points to publish. An empty list is a no-op.</param>
    /// <param name="cancellationToken">Token used to cancel the publish.</param>
    Task PublishAsync(IReadOnlyList<MigrationMetricPoint> metrics, CancellationToken cancellationToken = default);
}
