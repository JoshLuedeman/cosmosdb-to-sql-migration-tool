using Azure.Monitor.Query;
using CosmosToSqlAssessment.Tests.Mocks;

namespace CosmosToSqlAssessment.Tests.Mocks;

/// <summary>
/// Meta-tests for the <see cref="LogsQueryClientMockBuilder"/>. Production code in
/// <c>CosmosDbAnalysisService.CollectPerformanceMetricsAsync</c> reads
/// <c>response.Value.Table.Rows</c> and indexes columns 1, 2, 3, so the builder
/// must produce that exact shape.
/// </summary>
public class LogsQueryClientMockBuilderTests
{
    [Fact]
    public async Task Build_default_returns_response_with_single_empty_table()
    {
        var client = new LogsQueryClientMockBuilder().Build();

        var response = await client.QueryWorkspaceAsync(
            "workspace-id",
            "AzureDiagnostics | take 1",
            new QueryTimeRange(TimeSpan.FromHours(1)));

        response.Value.Should().NotBeNull();
        response.Value.Table.Should().NotBeNull();
        response.Value.Table.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_with_metrics_rows_returns_correctly_shaped_table()
    {
        var rows = new[]
        {
            (DateTimeOffset.UtcNow.AddHours(-2), 10.0, 25.0, 100.0),
            (DateTimeOffset.UtcNow.AddHours(-1), 12.0, 30.0, 120.0)
        };

        var client = new LogsQueryClientMockBuilder()
            .WithMetricsRows(rows)
            .Build();

        var response = await client.QueryWorkspaceAsync(
            "workspace-id",
            "AzureDiagnostics | take 2",
            new QueryTimeRange(TimeSpan.FromDays(1)));

        var table = response.Value.Table;
        table.Rows.Should().HaveCount(2);

        // Production indexes row[1], row[2], row[3] -- verify those positions hold numbers.
        Convert.ToDouble(table.Rows[0][1]).Should().Be(10.0);
        Convert.ToDouble(table.Rows[0][2]).Should().Be(25.0);
        Convert.ToDouble(table.Rows[0][3]).Should().Be(100.0);
    }

    [Fact]
    public async Task Build_with_error_propagates_exception()
    {
        var client = new LogsQueryClientMockBuilder()
            .WithError(new InvalidOperationException("Workspace unavailable"))
            .Build();

        var act = async () => await client.QueryWorkspaceAsync(
            "workspace-id",
            "AzureDiagnostics",
            new QueryTimeRange(TimeSpan.FromHours(1)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Workspace unavailable");
    }
}
