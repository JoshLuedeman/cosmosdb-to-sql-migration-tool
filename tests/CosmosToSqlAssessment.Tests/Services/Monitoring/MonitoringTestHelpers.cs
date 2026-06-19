using System.Runtime.CompilerServices;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

/// <summary>
/// Test helpers shared by the monitoring test suite: an in-memory metric publisher and
/// an <see cref="IAsyncEnumerable{T}"/> adapter for plain collections.
/// </summary>
internal static class MonitoringTestHelpers
{
    /// <summary>Wraps a synchronous sequence as an <see cref="IAsyncEnumerable{T}"/>.</summary>
    public static async IAsyncEnumerable<T> ToAsync<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }
}

/// <summary>Captures every batch of metric points published, for assertions.</summary>
internal sealed class RecordingMetricPublisher : IMigrationMetricPublisher
{
    public List<IReadOnlyList<MigrationMetricPoint>> Batches { get; } = new();
    public List<MigrationMetricPoint> AllPoints { get; } = new();

    public Task PublishAsync(IReadOnlyList<MigrationMetricPoint> metrics, CancellationToken cancellationToken = default)
    {
        Batches.Add(metrics);
        AllPoints.AddRange(metrics);
        return Task.CompletedTask;
    }
}

/// <summary>A publisher that always throws — used to prove the stream stays alive.</summary>
internal sealed class ThrowingMetricPublisher : IMigrationMetricPublisher
{
    public int Calls { get; private set; }

    public Task PublishAsync(IReadOnlyList<MigrationMetricPoint> metrics, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException("boom");
    }
}

/// <summary>An <see cref="IMigrationStatusSource"/> that replays a fixed set of samples.</summary>
internal sealed class FakeMigrationStatusSource : IMigrationStatusSource
{
    private readonly IReadOnlyList<MigrationProgressSample> _samples;
    private readonly bool _loopForeverWhenWatching;

    public FakeMigrationStatusSource(
        IReadOnlyList<MigrationProgressSample> samples,
        bool loopForeverWhenWatching = false)
    {
        _samples = samples;
        _loopForeverWhenWatching = loopForeverWhenWatching;
    }

    public async IAsyncEnumerable<MigrationProgressSample> ReadSamplesAsync(
        MigrationStatusReportOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var sample in _samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return sample;
        }

        if (options.Watch && _loopForeverWhenWatching)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
