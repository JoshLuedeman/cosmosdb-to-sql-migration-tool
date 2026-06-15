using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.EndToEnd;

/// <summary>
/// Tiny, deterministic in-memory document samples for the E2E pipeline harness.
///
/// Samples are intentionally small (2-3 documents per container) so the full
/// pipeline runs in well under 60 seconds even with verbose console logging
/// from the production services. Each document is a <see cref="JObject"/>,
/// which is the only document shape supported by the mock harness (see
/// <c>Mocks/README.md</c>).
/// </summary>
public static class E2ESampleData
{
    /// <summary>Two simple user documents.</summary>
    public static IReadOnlyList<JObject> TwoUsers => new[]
    {
        JObject.Parse("{\"id\":\"u1\",\"userId\":\"u1\",\"email\":\"a@example.com\",\"age\":30}"),
        JObject.Parse("{\"id\":\"u2\",\"userId\":\"u2\",\"email\":\"b@example.com\",\"age\":42}")
    };

    /// <summary>Three simple order documents.</summary>
    public static IReadOnlyList<JObject> ThreeOrders => new[]
    {
        JObject.Parse("{\"id\":\"o1\",\"orderId\":\"o1\",\"total\":99.99,\"userId\":\"u1\"}"),
        JObject.Parse("{\"id\":\"o2\",\"orderId\":\"o2\",\"total\":12.50,\"userId\":\"u2\"}"),
        JObject.Parse("{\"id\":\"o3\",\"orderId\":\"o3\",\"total\":250.00,\"userId\":\"u1\"}")
    };

    /// <summary>Two product documents.</summary>
    public static IReadOnlyList<JObject> TwoProducts => new[]
    {
        JObject.Parse("{\"id\":\"p1\",\"productId\":\"p1\",\"name\":\"Widget\",\"price\":9.99}"),
        JObject.Parse("{\"id\":\"p2\",\"productId\":\"p2\",\"name\":\"Gizmo\",\"price\":19.99}")
    };

    /// <summary>
    /// Six hours of synthetic RU metrics (avg, max, total per hour). The
    /// production code averages the avg column, takes max of the max column,
    /// and sums the total column - so these numbers are easy to reason about.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Timestamp, double Avg, double Max, double Total)> SixHoursOfMetrics
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new[]
            {
                (now.AddHours(-6), 10.0, 25.0, 100.0),
                (now.AddHours(-5), 12.0, 30.0, 110.0),
                (now.AddHours(-4), 15.0, 40.0, 150.0),
                (now.AddHours(-3), 18.0, 45.0, 180.0),
                (now.AddHours(-2), 20.0, 50.0, 200.0),
                (now.AddHours(-1), 22.0, 55.0, 220.0)
            };
        }
    }
}
