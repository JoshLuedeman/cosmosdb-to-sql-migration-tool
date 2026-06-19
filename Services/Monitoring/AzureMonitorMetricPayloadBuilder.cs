using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmosToSqlAssessment.Models.Monitoring;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Builds Azure Monitor custom-metric ingestion payloads from
/// <see cref="MigrationMetricPoint"/>s. The output matches the documented schema:
/// <code>
/// { "time": "&lt;ISO-8601 UTC&gt;",
///   "data": { "baseData": { "metric": "...", "namespace": "...",
///     "dimNames": ["..."], "series": [ { "dimValues": ["..."], "min": n, "max": n, "sum": n, "count": n } ] } } }
/// </code>
/// Points that share a namespace, metric name, timestamp, and dimension-name shape are
/// grouped into a single payload with one series per point. This keeps each payload valid
/// (Azure Monitor requires a stable <c>dimNames</c> array across all series in a payload).
/// The class is pure and deterministic, so it is exercised directly in unit tests without a live call.
/// </summary>
public sealed class AzureMonitorMetricPayloadBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds one payload object per (namespace, metric, timestamp, dimension-name shape) group.
    /// </summary>
    /// <param name="points">Metric points to convert. <c>null</c> or empty yields an empty list.</param>
    /// <returns>An ordered, deterministic list of payload dictionaries.</returns>
    public IReadOnlyList<Dictionary<string, object?>> BuildPayloads(IEnumerable<MigrationMetricPoint>? points)
    {
        if (points is null)
        {
            return Array.Empty<Dictionary<string, object?>>();
        }

        var groups = new Dictionary<string, List<MigrationMetricPoint>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var point in points)
        {
            var dimNames = OrderedDimensionNames(point);
            var time = FormatTimestamp(point.Timestamp);
            var key = string.Join('\u001f', point.Namespace, point.Name, time, string.Join(',', dimNames));

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<MigrationMetricPoint>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(point);
        }

        var payloads = new List<Dictionary<string, object?>>(order.Count);
        foreach (var key in order)
        {
            payloads.Add(BuildPayload(groups[key]));
        }
        return payloads;
    }

    /// <summary>
    /// Builds the payloads (see <see cref="BuildPayloads"/>) and serializes each to a
    /// compact, camelCase JSON string ready to POST to the metrics ingestion endpoint.
    /// </summary>
    /// <param name="points">Metric points to convert.</param>
    /// <returns>One JSON string per payload group.</returns>
    public IReadOnlyList<string> BuildPayloadJson(IEnumerable<MigrationMetricPoint>? points)
        => BuildPayloads(points).Select(p => JsonSerializer.Serialize(p, SerializerOptions)).ToList();

    private static Dictionary<string, object?> BuildPayload(IReadOnlyList<MigrationMetricPoint> group)
    {
        var representative = group[0];
        var dimNames = OrderedDimensionNames(representative);

        var series = new List<object?>(group.Count);
        foreach (var point in group)
        {
            var dimValues = dimNames
                .Select(name => point.Dimensions.TryGetValue(name, out var value) ? value : string.Empty)
                .Cast<object?>()
                .ToList();

            series.Add(new Dictionary<string, object?>
            {
                ["dimValues"] = dimValues,
                ["min"] = point.Value,
                ["max"] = point.Value,
                ["sum"] = point.Value,
                ["count"] = 1,
            });
        }

        var baseData = new Dictionary<string, object?>
        {
            ["metric"] = representative.Name,
            ["namespace"] = representative.Namespace,
            ["dimNames"] = dimNames.Cast<object?>().ToList(),
            ["series"] = series,
        };

        return new Dictionary<string, object?>
        {
            ["time"] = FormatTimestamp(representative.Timestamp),
            ["data"] = new Dictionary<string, object?>
            {
                ["baseData"] = baseData,
            },
        };
    }

    private static List<string> OrderedDimensionNames(MigrationMetricPoint point)
        => point.Dimensions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
