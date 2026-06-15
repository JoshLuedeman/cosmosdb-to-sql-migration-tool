using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Tests.Infrastructure;
using CosmosToSqlAssessment.Tests.Mocks;
using Newtonsoft.Json.Linq;

namespace CosmosToSqlAssessment.Tests.Services;

/// <summary>
/// Smoke tests that drive the real <see cref="CosmosDbAnalysisService"/> through
/// the mock Cosmos SDK harness. The point of these tests is to prove the harness
/// can exercise production code end-to-end without an Azure resource - deeper
/// edge-case coverage lands in sub-issues #183 and #184.
/// </summary>
public class CosmosDbAnalysisServiceMockedTests : TestBase
{
    [Fact]
    public async Task AnalyzeDatabaseAsync_with_two_containers_returns_both()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("AppDb", db => db
                .WithContainer("users", c => c
                    .WithPartitionKey("/userId")
                    .WithThroughput(400)
                    .WithDocuments(
                        JObject.Parse("{\"id\":\"u1\",\"email\":\"a@b.com\",\"age\":30}"),
                        JObject.Parse("{\"id\":\"u2\",\"email\":\"c@d.com\",\"age\":42}")))
                .WithContainer("orders", c => c
                    .WithPartitionKey("/orderId")
                    .WithThroughput(800)
                    .WithDocuments(
                        JObject.Parse("{\"id\":\"o1\",\"total\":99.99}"))))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("AppDb", CancellationToken.None);

        result.Should().NotBeNull();
        result.Containers.Should().HaveCount(2);

        var users = result.Containers.Single(c => c.ContainerName == "users");
        users.PartitionKey.Should().Be("/userId");
        users.ProvisionedRUs.Should().Be(400);
        users.DocumentCount.Should().Be(2);
        users.DetectedSchemas.Should().NotBeEmpty();

        var orders = result.Containers.Single(c => c.ContainerName == "orders");
        orders.PartitionKey.Should().Be("/orderId");
        orders.ProvisionedRUs.Should().Be(800);
        orders.DocumentCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_with_no_documents_yields_zero_count()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("EmptyDb", db => db
                .WithContainer("empty", c => c
                    .WithPartitionKey("/id")
                    .WithThroughput(400)))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var result = await service.AnalyzeDatabaseAsync("EmptyDb", CancellationToken.None);

        result.Containers.Should().ContainSingle();
        result.Containers[0].DocumentCount.Should().Be(0);
        result.Containers[0].DetectedSchemas.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_without_logs_client_records_monitoring_limitation()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("AppDb", db => db.WithContainer("c", c => c.WithThroughput(400)))
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient,
            logsQueryClient: null);

        var result = await service.AnalyzeDatabaseAsync("AppDb", CancellationToken.None);

        result.MonitoringLimitations.Should().ContainSingle(l => l.Contains("Azure Monitor"));
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_with_logs_client_populates_performance_metrics()
    {
        var cosmosClient = new CosmosClientMockBuilder()
            .WithDatabase("AppDb", db => db.WithContainer("c", c => c.WithThroughput(400)))
            .Build();

        // Configure the workspace so the production code attempts the query.
        MockConfiguration.Setup(c => c["AzureMonitor:WorkspaceId"]).Returns("workspace-1");
        MockConfiguration.Setup(c => c["AzureMonitor:CosmosAccountName"]).Returns("test-account");

        var logsClient = new LogsQueryClientMockBuilder()
            .WithMetricsRows(new[]
            {
                (DateTimeOffset.UtcNow.AddHours(-2), 10.0, 25.0, 100.0),
                (DateTimeOffset.UtcNow.AddHours(-1), 20.0, 50.0, 200.0)
            })
            .Build();

        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient,
            logsClient);

        var result = await service.AnalyzeDatabaseAsync("AppDb", CancellationToken.None);

        result.PerformanceMetrics.Should().NotBeNull();
        result.PerformanceMetrics.AverageRUsPerSecond.Should().Be(15.0);    // mean of 10 and 20
        result.PerformanceMetrics.PeakRUsPerSecond.Should().Be(50.0);       // max of 25 and 50
        result.PerformanceMetrics.TotalRUConsumption.Should().Be(300.0);    // sum of 100 and 200
    }

    [Fact]
    public async Task AnalyzeDatabaseAsync_with_empty_database_name_throws()
    {
        var cosmosClient = new CosmosClientMockBuilder().Build();
        var service = new CosmosDbAnalysisService(
            MockConfiguration.Object,
            CreateMockLogger<CosmosDbAnalysisService>().Object,
            cosmosClient);

        var act = async () => await service.AnalyzeDatabaseAsync(string.Empty, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
