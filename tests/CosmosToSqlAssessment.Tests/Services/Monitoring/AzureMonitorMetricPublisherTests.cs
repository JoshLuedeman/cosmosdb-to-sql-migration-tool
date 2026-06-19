using System.Net;
using System.Net.Http;
using Azure.Core;
using CosmosToSqlAssessment.Models.Monitoring;
using CosmosToSqlAssessment.Services.Monitoring;

namespace CosmosToSqlAssessment.Tests.Services.Monitoring;

public class AzureMonitorMetricPublisherTests
{
    private sealed class StubTokenCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public CountingHandler(HttpStatusCode status = HttpStatusCode.OK) => _status = status;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(_status) { Content = new StringContent("{}") };
        }
    }

    private static List<MigrationMetricPoint> SamplePoints() => new()
    {
        new MigrationMetricPoint
        {
            Name = "MigrationRowsMigrated",
            Namespace = "CosmosToSqlMigration",
            Value = 100,
            Timestamp = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero),
            Dimensions = new Dictionary<string, string> { ["PipelineName"] = "P" },
        },
    };

    private static AzureMonitorMetricPublisher Create(
        AzureMonitorMetricOptions options,
        TokenCredential credential,
        HttpMessageHandler handler)
        => new(options, Mock.Of<ILogger<AzureMonitorMetricPublisher>>(), credential, new HttpClient(handler), new AzureMonitorMetricPayloadBuilder());

    [Fact]
    public async Task PublishAsync_Disabled_DoesNotCallEndpointOrCredential()
    {
        var credential = new StubTokenCredential();
        var handler = new CountingHandler();
        var publisher = Create(new AzureMonitorMetricOptions { Enabled = false }, credential, handler);

        await publisher.PublishAsync(SamplePoints());

        handler.Requests.Should().BeEmpty();
        credential.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_EnabledButMissingConfig_DoesNotCallEndpoint()
    {
        var credential = new StubTokenCredential();
        var handler = new CountingHandler();
        var publisher = Create(new AzureMonitorMetricOptions { Enabled = true, Region = "", ResourceId = null }, credential, handler);

        await publisher.PublishAsync(SamplePoints());

        handler.Requests.Should().BeEmpty();
        credential.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_EmptyMetrics_IsNoOp()
    {
        var credential = new StubTokenCredential();
        var handler = new CountingHandler();
        var publisher = Create(
            new AzureMonitorMetricOptions { Enabled = true, Region = "eastus", ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataFactory/factories/f" },
            credential, handler);

        await publisher.PublishAsync(Array.Empty<MigrationMetricPoint>());

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_Configured_PostsBearerAuthorizedPayloadToRegionalEndpoint()
    {
        var credential = new StubTokenCredential();
        var handler = new CountingHandler();
        var options = new AzureMonitorMetricOptions
        {
            Enabled = true,
            Region = "eastus",
            ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataFactory/factories/f",
        };
        var publisher = Create(options, credential, handler);

        await publisher.PublishAsync(SamplePoints());

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be(
            "https://eastus.monitoring.azure.com/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataFactory/factories/f/metrics");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("fake-token");
        handler.Bodies[0].Should().Contain("MigrationRowsMigrated");
    }

    [Fact]
    public async Task PublishAsync_HttpError_DoesNotThrow()
    {
        var credential = new StubTokenCredential();
        var handler = new CountingHandler(HttpStatusCode.BadRequest);
        var options = new AzureMonitorMetricOptions
        {
            Enabled = true,
            Region = "eastus",
            ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataFactory/factories/f",
        };
        var publisher = Create(options, credential, handler);

        var act = async () => await publisher.PublishAsync(SamplePoints());

        await act.Should().NotThrowAsync();
        handler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("/subscriptions/s/rg")]
    [InlineData("subscriptions/s/rg")]
    public void BuildEndpoint_NormalizesLeadingSlash(string resourceId)
    {
        var endpoint = AzureMonitorMetricPublisher.BuildEndpoint("westus2", resourceId);
        endpoint.Should().Be("https://westus2.monitoring.azure.com/subscriptions/s/rg/metrics");
    }
}
