using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using CosmosToSqlAssessment.Models.Monitoring;
using Microsoft.Extensions.Logging;

namespace CosmosToSqlAssessment.Services.Monitoring;

/// <summary>
/// Default <see cref="IMigrationMetricPublisher"/> that streams custom metrics to the
/// Azure Monitor regional ingestion endpoint
/// (<c>https://&lt;region&gt;.monitoring.azure.com&lt;resourceId&gt;/metrics</c>).
/// </summary>
/// <remarks>
/// <para>
/// Reuses the Azure Monitor surface introduced by #76 — the same
/// <see cref="DefaultAzureCredential"/> auth and the <c>AzureMonitor</c> configuration
/// root — but targets the metric-ingestion REST API rather than the read-only Query SDK.
/// </para>
/// <para>
/// Publishing is gated by <see cref="AzureMonitorMetricOptions.Enabled"/> (default
/// <c>false</c>), so offline / CI runs never make a live call. When enabled but missing
/// the region or resource id, the publisher logs an error once and no-ops. Per-payload
/// HTTP failures are logged and swallowed so a publish failure never aborts the caller's
/// progress stream.
/// </para>
/// </remarks>
public sealed class AzureMonitorMetricPublisher : IMigrationMetricPublisher
{
    private static readonly string[] IngestionScopes = { "https://monitoring.azure.com/.default" };

    private readonly AzureMonitorMetricOptions _options;
    private readonly ILogger<AzureMonitorMetricPublisher> _logger;
    private readonly TokenCredential _credential;
    private readonly HttpClient _httpClient;
    private readonly AzureMonitorMetricPayloadBuilder _payloadBuilder;

    private bool _loggedDisabled;
    private bool _loggedMisconfigured;

    /// <summary>
    /// Production constructor. Builds a <see cref="DefaultAzureCredential"/> and an
    /// <see cref="HttpClient"/> internally.
    /// </summary>
    /// <param name="options">Metric publishing options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public AzureMonitorMetricPublisher(
        AzureMonitorMetricOptions options,
        ILogger<AzureMonitorMetricPublisher> logger)
        : this(options, logger, new DefaultAzureCredential(), new HttpClient(), new AzureMonitorMetricPayloadBuilder())
    {
    }

    /// <summary>
    /// Test/advanced constructor that accepts the credential, HTTP client, and payload
    /// builder so unit tests can inject fakes and never make a live call.
    /// </summary>
    /// <param name="options">Metric publishing options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="credential">Token credential used to acquire ingestion tokens.</param>
    /// <param name="httpClient">HTTP client used to POST payloads.</param>
    /// <param name="payloadBuilder">Builder that converts metric points to ingestion payloads.</param>
    internal AzureMonitorMetricPublisher(
        AzureMonitorMetricOptions options,
        ILogger<AzureMonitorMetricPublisher> logger,
        TokenCredential credential,
        HttpClient httpClient,
        AzureMonitorMetricPayloadBuilder payloadBuilder)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _payloadBuilder = payloadBuilder ?? throw new ArgumentNullException(nameof(payloadBuilder));
    }

    /// <inheritdoc />
    public async Task PublishAsync(IReadOnlyList<MigrationMetricPoint> metrics, CancellationToken cancellationToken = default)
    {
        if (metrics is null || metrics.Count == 0)
        {
            return;
        }

        if (!_options.Enabled)
        {
            if (!_loggedDisabled)
            {
                _loggedDisabled = true;
                _logger.LogInformation("Azure Monitor metric publishing is disabled (AzureMonitor:Metrics:Enabled=false). Metrics will not be sent.");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Region) || string.IsNullOrWhiteSpace(_options.ResourceId))
        {
            if (!_loggedMisconfigured)
            {
                _loggedMisconfigured = true;
                _logger.LogError("Azure Monitor metric publishing is enabled but AzureMonitor:Metrics:Region or :ResourceId is missing. Metrics will not be sent.");
            }
            return;
        }

        var endpoint = BuildEndpoint(_options.Region!, _options.ResourceId!);

        string accessToken;
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(IngestionScopes), cancellationToken).ConfigureAwait(false);
            accessToken = token.Token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to acquire an Azure Monitor ingestion token; skipping metric publish.");
            return;
        }

        foreach (var json in _payloadBuilder.BuildPayloadJson(metrics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Azure Monitor metric ingestion returned {StatusCode}: {Body}",
                        (int)response.StatusCode,
                        body);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to POST a metric payload to Azure Monitor; continuing.");
            }
        }
    }

    /// <summary>
    /// Builds the regional metric-ingestion endpoint for the supplied region and resource id.
    /// </summary>
    /// <param name="region">Azure region (e.g. <c>eastus</c>).</param>
    /// <param name="resourceId">Full ARM resource id beginning with <c>/subscriptions/</c>.</param>
    /// <returns>The absolute ingestion URL.</returns>
    public static string BuildEndpoint(string region, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        var normalizedResource = resourceId.StartsWith('/') ? resourceId : "/" + resourceId;
        return $"https://{region}.monitoring.azure.com{normalizedResource}/metrics";
    }
}
