using CosmosToSqlAssessment.Models.Monitoring;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// An <see cref="IMigrationMetricPublisher"/> that discards every batch. Used by read-only
/// consumers such as the <c>migration status</c> command (#225) so that re-deriving
/// snapshots for display never re-publishes custom metrics to Azure Monitor.
/// </summary>
public sealed class NullMigrationMetricPublisher : IMigrationMetricPublisher
{
    /// <inheritdoc />
    public Task PublishAsync(IReadOnlyList<MigrationMetricPoint> metrics, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
