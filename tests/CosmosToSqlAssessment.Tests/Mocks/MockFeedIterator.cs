using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Factory helpers for building mock <see cref="FeedIterator{T}"/> instances.
/// Supports single-page, multi-page (paginated), empty, and faulted iterators.
/// </summary>
public static class MockFeedIterator
{
    /// <summary>
    /// Builds a <see cref="FeedIterator{T}"/> that returns all <paramref name="items"/>
    /// in a single page.
    /// </summary>
    public static FeedIterator<T> OfDocuments<T>(IEnumerable<T> items)
        => OfPages(new[] { items.ToList() });

    /// <summary>
    /// Builds a <see cref="FeedIterator{T}"/> that returns each element of
    /// <paramref name="pages"/> as a separate page (in order).
    /// </summary>
    public static FeedIterator<T> OfPages<T>(IEnumerable<IReadOnlyCollection<T>> pages)
    {
        var pageList = pages.Select(p => (IEnumerable<T>)p).ToList();
        var mock = new Mock<FeedIterator<T>>();
        var index = 0;

        mock.SetupGet(i => i.HasMoreResults).Returns(() => index < pageList.Count);
        mock.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (index >= pageList.Count)
                {
                    return new MockFeedResponse<T>(Array.Empty<T>());
                }
                var page = pageList[index];
                index++;
                return new MockFeedResponse<T>(page);
            });
        return mock.Object;
    }

    /// <summary>
    /// Builds an empty <see cref="FeedIterator{T}"/> (no pages, <c>HasMoreResults</c> false).
    /// </summary>
    public static FeedIterator<T> Empty<T>()
    {
        var mock = new Mock<FeedIterator<T>>();
        mock.SetupGet(i => i.HasMoreResults).Returns(false);
        mock.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MockFeedResponse<T>(Array.Empty<T>()));
        return mock.Object;
    }

    /// <summary>
    /// Builds a <see cref="FeedIterator{T}"/> whose first <c>ReadNextAsync</c> throws
    /// the provided exception. Useful for retry / transient-failure tests (#184).
    /// </summary>
    public static FeedIterator<T> ThrowsOnRead<T>(Exception exception)
    {
        var mock = new Mock<FeedIterator<T>>();
        mock.SetupGet(i => i.HasMoreResults).Returns(true);
        mock.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
        return mock.Object;
    }
}
