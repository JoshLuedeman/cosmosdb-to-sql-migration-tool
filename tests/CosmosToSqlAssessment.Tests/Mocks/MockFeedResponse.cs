using System.Collections;
using System.Net;
using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Concrete <see cref="FeedResponse{T}"/> implementation backed by an in-memory list.
/// Returns a <b>fresh</b> enumerator on every call to <see cref="GetEnumerator"/> so
/// callers can safely enumerate the same instance multiple times (production code
/// frequently does <c>response.Count()</c> followed by <c>foreach (var x in response)</c>).
/// </summary>
public sealed class MockFeedResponse<T> : FeedResponse<T>
{
    private readonly IReadOnlyList<T> _items;

    public MockFeedResponse(IEnumerable<T> items, string? continuationToken = null)
    {
        _items = items?.ToList() ?? new List<T>();
        ContinuationToken = continuationToken;
    }

    public override string? ContinuationToken { get; }
    public override int Count => _items.Count;
    public override string IndexMetrics => string.Empty;
    public override Headers Headers { get; } = new();
    public override IEnumerable<T> Resource => _items;
    public override HttpStatusCode StatusCode => HttpStatusCode.OK;
    public override double RequestCharge => 1.0;
    public override string ActivityId => Guid.NewGuid().ToString();
    public override CosmosDiagnostics? Diagnostics => null;

    public override IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
}
