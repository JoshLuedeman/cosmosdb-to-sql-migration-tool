using System.Net;
using System.Net.Http;
using CosmosToSqlAssessment.Services;
using CosmosToSqlAssessment.Services.Feedback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CosmosToSqlAssessment.Tests.Services.Feedback;

public class HttpFeedbackTelemetrySinkTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return _responder(request, cancellationToken);
        }
    }

    private static CoarsenedOutcome SamplePayload() =>
        CoarsenedOutcome.From(new MigrationOutcome { Status = MigrationOutcomeStatus.Succeeded });

    [Fact]
    public async Task SendAsync_PostsJsonToConfiguredEndpoint()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var options = new FeedbackOptions { TelemetryEndpoint = "https://example.test/ingest" };
        var sink = new HttpFeedbackTelemetrySink(client, options, NullLogger<HttpFeedbackTelemetrySink>.Instance);

        await sink.SendAsync(SamplePayload());

        handler.CallCount.Should().Be(1);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://example.test/ingest");
        handler.LastRequest.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task SendAsync_NoEndpointConfigured_DoesNotCallHttp()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var sink = new HttpFeedbackTelemetrySink(client, new FeedbackOptions(), NullLogger<HttpFeedbackTelemetrySink>.Instance);

        await sink.SendAsync(SamplePayload());

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_TransportFailure_IsSwallowed()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("boom"));
        using var client = new HttpClient(handler);
        var options = new FeedbackOptions { TelemetryEndpoint = "https://example.test/ingest" };
        var sink = new HttpFeedbackTelemetrySink(client, options, NullLogger<HttpFeedbackTelemetrySink>.Instance);

        var act = () => sink.SendAsync(SamplePayload());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_DoesNotThrow()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = new HttpClient(handler);
        var options = new FeedbackOptions { TelemetryEndpoint = "https://example.test/ingest" };
        var sink = new HttpFeedbackTelemetrySink(client, options, NullLogger<HttpFeedbackTelemetrySink>.Instance);

        var act = () => sink.SendAsync(SamplePayload());

        await act.Should().NotThrowAsync();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_Cancellation_Rethrows()
    {
        var handler = new StubHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var client = new HttpClient(handler);
        var options = new FeedbackOptions { TelemetryEndpoint = "https://example.test/ingest" };
        var sink = new HttpFeedbackTelemetrySink(client, options, NullLogger<HttpFeedbackTelemetrySink>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sink.SendAsync(SamplePayload(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
