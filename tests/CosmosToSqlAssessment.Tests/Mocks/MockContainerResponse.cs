using System.Net;
using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Factory for mock <see cref="ContainerResponse"/> instances.
/// Production code reads <c>response.Resource</c> (a <see cref="ContainerProperties"/>)
/// to inspect the container's partition key path and indexing policy.
/// </summary>
public static class MockContainerResponse
{
    public static ContainerResponse Build(
        string containerId,
        string partitionKeyPath,
        IndexingPolicy? indexingPolicy = null)
    {
        var properties = new ContainerProperties(containerId, partitionKeyPath);
        if (indexingPolicy != null)
        {
            properties.IndexingPolicy = indexingPolicy;
        }

        var mock = new Mock<ContainerResponse>();
        mock.SetupGet(r => r.Resource).Returns(properties);
        mock.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.OK);
        mock.SetupGet(r => r.RequestCharge).Returns(1.0);
        mock.SetupGet(r => r.ActivityId).Returns(Guid.NewGuid().ToString());
        mock.SetupGet(r => r.ETag).Returns(Guid.NewGuid().ToString());
        return mock.Object;
    }
}
