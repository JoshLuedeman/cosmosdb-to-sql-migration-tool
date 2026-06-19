using System.Text.Json;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AzureMonitorMetricPayloadBuilderTests
{
    private static readonly DateTimeOffset Time = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

    private static MigrationMetricPoint Point(
        string name,
        double value,
        DateTimeOffset? time = null,
        IReadOnlyDictionary<string, string>? dims = null,
        string ns = "CosmosToSqlMigration")
        => new()
        {
            Name = name,
            Namespace = ns,
            Value = value,
            Timestamp = time ?? Time,
            Dimensions = dims ?? new Dictionary<string, string>(),
        };

    [Fact]
    public void BuildPayloads_NullOrEmpty_ReturnsEmpty()
    {
        var builder = new AzureMonitorMetricPayloadBuilder();

        builder.BuildPayloads(null).Should().BeEmpty();
        builder.BuildPayloads(Array.Empty<MigrationMetricPoint>()).Should().BeEmpty();
    }

    [Fact]
    public void BuildPayloads_SinglePoint_ProducesDocumentedSchema()
    {
        var dims = new Dictionary<string, string> { ["PipelineName"] = "Migrate_Sales", ["Status"] = "InProgress" };
        var builder = new AzureMonitorMetricPayloadBuilder();

        var payloads = builder.BuildPayloads(new[] { Point("MigrationRowsMigrated", 123, dims: dims) });

        payloads.Should().HaveCount(1);
        var payload = payloads[0];
        payload["time"].Should().Be("2026-06-18T12:00:00.000Z");

        var data = (IDictionary<string, object?>)payload["data"]!;
        var baseData = (IDictionary<string, object?>)data["baseData"]!;
        baseData["metric"].Should().Be("MigrationRowsMigrated");
        baseData["namespace"].Should().Be("CosmosToSqlMigration");

        var dimNames = ((IEnumerable<object?>)baseData["dimNames"]!).Cast<string>().ToList();
        dimNames.Should().ContainInOrder("PipelineName", "Status"); // ordinal-sorted

        var series = (List<object?>)baseData["series"]!;
        series.Should().HaveCount(1);
        var s0 = (IDictionary<string, object?>)series[0]!;
        ((IEnumerable<object?>)s0["dimValues"]!).Cast<string>().Should().ContainInOrder("Migrate_Sales", "InProgress");
        s0["min"].Should().Be(123d);
        s0["max"].Should().Be(123d);
        s0["sum"].Should().Be(123d);
        s0["count"].Should().Be(1);
    }

    [Fact]
    public void BuildPayloads_DifferentMetricNames_ProduceSeparatePayloads()
    {
        var builder = new AzureMonitorMetricPayloadBuilder();

        var payloads = builder.BuildPayloads(new[]
        {
            Point("MigrationRowsMigrated", 10),
            Point("MigrationErrorRate", 0.5),
        });

        payloads.Should().HaveCount(2);
    }

    [Fact]
    public void BuildPayloads_SameMetricDifferentDimValues_GroupIntoOnePayloadWithSeries()
    {
        var builder = new AzureMonitorMetricPayloadBuilder();
        var a = new Dictionary<string, string> { ["PipelineName"] = "A" };
        var b = new Dictionary<string, string> { ["PipelineName"] = "B" };

        var payloads = builder.BuildPayloads(new[]
        {
            Point("MigrationRowsMigrated", 1, dims: a),
            Point("MigrationRowsMigrated", 2, dims: b),
        });

        payloads.Should().HaveCount(1);
        var baseData = (IDictionary<string, object?>)((IDictionary<string, object?>)payloads[0]["data"]!)["baseData"]!;
        ((List<object?>)baseData["series"]!).Should().HaveCount(2);
    }

    [Fact]
    public void BuildPayloads_DifferentTimestamps_ProduceSeparatePayloads()
    {
        var builder = new AzureMonitorMetricPayloadBuilder();

        var payloads = builder.BuildPayloads(new[]
        {
            Point("MigrationRowsMigrated", 1, time: Time),
            Point("MigrationRowsMigrated", 2, time: Time.AddMinutes(1)),
        });

        payloads.Should().HaveCount(2);
    }

    [Fact]
    public void BuildPayloads_EmptyDimensions_ProduceEmptyDimArrays()
    {
        var builder = new AzureMonitorMetricPayloadBuilder();

        var payloads = builder.BuildPayloads(new[] { Point("MigrationErrorCount", 0) });

        var baseData = (IDictionary<string, object?>)((IDictionary<string, object?>)payloads[0]["data"]!)["baseData"]!;
        ((IEnumerable<object?>)baseData["dimNames"]!).Should().BeEmpty();
        var s0 = (IDictionary<string, object?>)((List<object?>)baseData["series"]!)[0]!;
        ((IEnumerable<object?>)s0["dimValues"]!).Should().BeEmpty();
    }

    [Fact]
    public void BuildPayloadJson_ProducesValidParsableJson()
    {
        var dims = new Dictionary<string, string> { ["PipelineName"] = "Migrate_Sales" };
        var builder = new AzureMonitorMetricPayloadBuilder();

        var jsonList = builder.BuildPayloadJson(new[] { Point("MigrationRowsMigrated", 7, dims: dims) });

        jsonList.Should().HaveCount(1);
        using var doc = JsonDocument.Parse(jsonList[0]);
        var root = doc.RootElement;
        root.GetProperty("time").GetString().Should().Be("2026-06-18T12:00:00.000Z");
        var baseData = root.GetProperty("data").GetProperty("baseData");
        baseData.GetProperty("metric").GetString().Should().Be("MigrationRowsMigrated");
        baseData.GetProperty("series")[0].GetProperty("count").GetInt32().Should().Be(1);
    }
}
