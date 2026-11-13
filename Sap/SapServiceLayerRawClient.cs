using Microsoft.Extensions.Options;
using PH.SapB1.Vaniperen.Logging;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PH.SapB1.Vaniperen.Sap;

public sealed class SapServiceLayerRawClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SapSessionManager _sessionManager;
    private readonly ILogger<SapServiceLayerRawClient> _logger;
    private readonly DeepLoggingOptions _deepLoggingOptions;

    public SapServiceLayerRawClient(
    IHttpClientFactory httpClientFactory,
    SapSessionManager sessionManager,
    ILogger<SapServiceLayerRawClient> logger,
    IOptions<DeepLoggingOptions> deepLoggingOptions)
    {
        _httpClientFactory = httpClientFactory;
        _sessionManager = sessionManager;
        _logger = logger;
        _deepLoggingOptions = deepLoggingOptions.Value;
    }

    public async Task<ServiceLayerResult> SendAsync(
        HttpMethod method,
        string relativeUrl,
        JsonNode? payload,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraHeaders = null)
    {
        var cookie = await _sessionManager.GetCookieHeaderAsync(cancellationToken);

        var firstResult = await SendInternalAsync(
            method,
            relativeUrl,
            payload,
            cookie,
            cancellationToken,
            extraHeaders);

        if (firstResult.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshedCookie = await _sessionManager.ForceRefreshAsync(cancellationToken);

            return await SendInternalAsync(
                method,
                relativeUrl,
                payload,
                refreshedCookie,
                cancellationToken,
                extraHeaders);
        }

        return firstResult;
    }

    private async Task<ServiceLayerResult> SendInternalAsync(
        HttpMethod method,
        string relativeUrl,
        JsonNode? payload,
        string cookie,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraHeaders)
    {
        var client = _httpClientFactory.CreateClient("SapServiceLayer");

        using var request = new HttpRequestMessage(method, relativeUrl);

        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Accept", "application/json");

        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                request.Headers.Remove(header.Key);
                request.Headers.Add(header.Key, header.Value);
            }
        }

        if (payload is not null)
        {
            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");
        }
        if (_deepLoggingOptions.Enabled)
        {
            _logger.LogInformation(
                "SAP SL REQUEST {Method} {Url} Payload={Payload}",
                method,
                relativeUrl,
                payload is null
                    ? ""
                    : LogSanitizer.MaskBody(payload.ToJsonString(), _deepLoggingOptions.MaxBodyChars));
        }
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (_deepLoggingOptions.Enabled)
        {
            _logger.LogInformation(
                "SAP SL RESPONSE {Method} {Url} Status={Status} Body={Body}",
                method,
                relativeUrl,
                response.StatusCode,
                LogSanitizer.MaskBody(body, _deepLoggingOptions.MaxBodyChars));
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Service Layer call failed. Method: {Method}, Url: {Url}, Status: {Status}, Body: {Body}",
                method,
                relativeUrl,
                response.StatusCode,
                body);
        }

        return new ServiceLayerResult
        {
            StatusCode = response.StatusCode,
            Body = body
        };
    }
}