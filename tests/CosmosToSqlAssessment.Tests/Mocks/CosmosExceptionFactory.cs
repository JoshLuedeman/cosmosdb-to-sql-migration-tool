using System.Net;
using Microsoft.Azure.Cosmos;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Factory for crafting <see cref="CosmosException"/>s that mirror real Azure
/// error shapes. Used by the harness, #183 (edge cases), and especially #184
/// (transient failure / retry tests).
/// </summary>
public static class CosmosExceptionFactory
{
    /// <summary>HTTP 429 - request rate too large (throttling).</summary>
    public static CosmosException Throttled(string? activityId = null)
        => new(
            message: "Request rate is large",
            statusCode: (HttpStatusCode)429,
            subStatusCode: 3200,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);

    /// <summary>HTTP 503 - service unavailable.</summary>
    public static CosmosException ServiceUnavailable(string? activityId = null)
        => new(
            message: "Service unavailable",
            statusCode: HttpStatusCode.ServiceUnavailable,
            subStatusCode: 0,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);

    /// <summary>HTTP 408 - request timeout.</summary>
    public static CosmosException Timeout(string? activityId = null)
        => new(
            message: "Request timed out",
            statusCode: HttpStatusCode.RequestTimeout,
            subStatusCode: 0,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);

    /// <summary>HTTP 403 - insufficient permissions (Forbidden).</summary>
    public static CosmosException Forbidden(string? activityId = null)
        => new(
            message: "Forbidden",
            statusCode: HttpStatusCode.Forbidden,
            subStatusCode: 0,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);

    /// <summary>HTTP 404 - resource not found.</summary>
    public static CosmosException NotFound(string? activityId = null)
        => new(
            message: "Not Found",
            statusCode: HttpStatusCode.NotFound,
            subStatusCode: 0,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);

    /// <summary>HTTP 400 - bad request (also raised when a container is using shared db throughput).</summary>
    public static CosmosException BadRequest(string? message = null, string? activityId = null)
        => new(
            message: message ?? "Bad request",
            statusCode: HttpStatusCode.BadRequest,
            subStatusCode: 0,
            activityId: activityId ?? Guid.NewGuid().ToString(),
            requestCharge: 0);
}
