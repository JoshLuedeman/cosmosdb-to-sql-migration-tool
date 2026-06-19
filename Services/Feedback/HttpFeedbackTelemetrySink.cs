using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Feedback;

/// <summary>
/// An <see cref="IFeedbackTelemetrySink"/> that POSTs a coarsened payload as JSON to the configured
/// <see cref="FeedbackOptions.TelemetryEndpoint"/>. Transport failures are logged and swallowed so
/// that local feedback collection always succeeds regardless of network conditions.
/// </summary>
public sealed class HttpFeedbackTelemetrySink : IFeedbackTelemetrySink
{
    private readonly HttpClient _httpClient;
    private readonly FeedbackOptions _options;
    private readonly ILogger<HttpFeedbackTelemetrySink> _logger;

    /// <summary>
    /// Creates a new <see cref="HttpFeedbackTelemetrySink"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to transmit telemetry.</param>
    /// <param name="options">Feedback options supplying the telemetry endpoint.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public HttpFeedbackTelemetrySink(
        HttpClient httpClient,
        FeedbackOptions options,
        ILogger<HttpFeedbackTelemetrySink> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendAsync(CoarsenedOutcome payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(_options.TelemetryEndpoint))
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient
                .PostAsync(_options.TelemetryEndpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Feedback telemetry endpoint returned {StatusCode}; continuing.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transmission is best-effort; never fail local collection because of telemetry.
            _logger.LogWarning(ex, "Failed to transmit coarsened feedback telemetry; continuing.");
        }
    }
}
