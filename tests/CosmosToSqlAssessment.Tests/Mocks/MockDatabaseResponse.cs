using System.Net;
using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Factory for mock <see cref="DatabaseResponse"/> instances.
/// Production reads <c>response.Resource.Id</c> when logging discovered databases.
/// </summary>
public static class MockDatabaseResponse
{
    public static DatabaseResponse Build(string databaseId)
    {
        var properties = new DatabaseProperties(databaseId);

        var mock = new Mock<DatabaseResponse>();
        mock.SetupGet(r => r.Resource).Returns(properties);
        mock.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.OK);
        mock.SetupGet(r => r.RequestCharge).Returns(1.0);
        mock.SetupGet(r => r.ActivityId).Returns(Guid.NewGuid().ToString());
        mock.SetupGet(r => r.ETag).Returns(Guid.NewGuid().ToString());
        return mock.Object;
    }
}
