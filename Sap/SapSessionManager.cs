using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PH.SapB1.Vaniperen.Sap;

public sealed class SapSessionManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SapServiceLayerOptions _options;
    private readonly ILogger<SapSessionManager> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _cookieHeader;
    private DateTimeOffset _expiresAtUtc;

    public SapSessionManager(
        IHttpClientFactory httpClientFactory,
        IOptions<SapServiceLayerOptions> options,
        ILogger<SapSessionManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetCookieHeaderAsync(
        CancellationToken cancellationToken)
    {
        if (HasValidSession())
        {
            return _cookieHeader!;
        }

        await _sessionLock.WaitAsync(cancellationToken);

        try
        {
            if (HasValidSession())
            {
                return _cookieHeader!;
            }

            return await LoginAsync(cancellationToken);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<string> ForceRefreshAsync(
        CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);

        try
        {
            _cookieHeader = null;
            _expiresAtUtc = DateTimeOffset.MinValue;

            return await LoginAsync(cancellationToken);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool HasValidSession()
    {
        return !string.IsNullOrWhiteSpace(_cookieHeader) &&
               _expiresAtUtc >
               DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private async Task<string> LoginAsync(
        CancellationToken cancellationToken)
    {
        var client =
            _httpClientFactory.CreateClient(
                "SapServiceLayer");

        var maxAttempts =
            Math.Max(1, _options.LoginRetryCount);

        Exception? lastException = null;

        for (var attempt = 1;
             attempt <= maxAttempts;
             attempt++)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var request =
                    CreateLoginRequest();

                _logger.LogInformation(
                    "SAP LOGIN ATTEMPT Attempt={Attempt}/{MaxAttempts} BaseAddress={BaseAddress} CompanyDB={CompanyDB} UserName={UserName} PreferredRouteId={PreferredRouteId}",
                    attempt,
                    maxAttempts,
                    client.BaseAddress,
                    _options.CompanyDB,
                    _options.UserName,
                    string.IsNullOrWhiteSpace(
                        _options.PreferredRouteId)
                        ? "LoadBalanced"
                        : _options.PreferredRouteId);

                using var response =
                    await client.SendAsync(
                        request,
                        HttpCompletionOption
                            .ResponseHeadersRead,
                        cancellationToken);

                var body =
                    await response.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "SAP LOGIN RESPONSE SUCCESS Attempt={Attempt} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                        attempt,
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds);

                    return CreateSessionFromResponse(
                        response,
                        body);
                }

                var isTransient =
                    IsTransientStatusCode(
                        response.StatusCode);

                _logger.LogWarning(
                    "SAP LOGIN RESPONSE FAILED Attempt={Attempt}/{MaxAttempts} StatusCode={StatusCode} IsTransient={IsTransient} ElapsedMs={ElapsedMs} Body={Body}",
                    attempt,
                    maxAttempts,
                    (int)response.StatusCode,
                    isTransient,
                    stopwatch.ElapsedMilliseconds,
                    Truncate(body, 5000));

                if (!isTransient ||
                    attempt >= maxAttempts)
                {
                    throw new InvalidOperationException(
                        $"SAP Service Layer login failed. " +
                        $"HTTP {(int)response.StatusCode} " +
                        $"{response.StatusCode}: {body}");
                }

                await DelayBeforeRetryAsync(
                    attempt,
                    cancellationToken);
            }
            catch (HttpRequestException ex)
                when (attempt < maxAttempts)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(
                    ex,
                    "SAP LOGIN NETWORK FAILURE Attempt={Attempt}/{MaxAttempts} ElapsedMs={ElapsedMs}. Retrying.",
                    attempt,
                    maxAttempts,
                    stopwatch.ElapsedMilliseconds);

                await DelayBeforeRetryAsync(
                    attempt,
                    cancellationToken);
            }
            catch (TaskCanceledException ex)
                when (!cancellationToken
                    .IsCancellationRequested &&
                      attempt < maxAttempts)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(
                    ex,
                    "SAP LOGIN TIMEOUT Attempt={Attempt}/{MaxAttempts} ElapsedMs={ElapsedMs}. Retrying.",
                    attempt,
                    maxAttempts,
                    stopwatch.ElapsedMilliseconds);

                await DelayBeforeRetryAsync(
                    attempt,
                    cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "SAP Service Layer login failed after all retry attempts.",
            lastException);
    }

    private HttpRequestMessage CreateLoginRequest()
    {
        var payload = new JsonObject
        {
            ["CompanyDB"] = _options.CompanyDB,
            ["UserName"] = _options.UserName,
            ["Password"] = _options.Password
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "Login");

        request.Headers.TryAddWithoutValidation(
            "Accept",
            "application/json");

        var preferredRouteId =
            NormalizeRouteId(
                _options.PreferredRouteId);

        if (!string.IsNullOrWhiteSpace(
                preferredRouteId))
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                $"ROUTEID={preferredRouteId}");
        }

        request.Content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    private string CreateSessionFromResponse(
        HttpResponseMessage response,
        string responseBody)
    {
        if (!response.Headers.TryGetValues(
                "Set-Cookie",
                out var setCookieHeaders))
        {
            throw new InvalidOperationException(
                "SAP Service Layer login succeeded but did not return Set-Cookie.");
        }

        var cookies =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var setCookieHeader
                 in setCookieHeaders)
        {
            var cookiePart =
                setCookieHeader.Split(
                    ';',
                    2,
                    StringSplitOptions.TrimEntries)[0];

            var separatorIndex =
                cookiePart.IndexOf('=');

            if (separatorIndex <= 0)
            {
                continue;
            }

            var name =
                cookiePart[..separatorIndex].Trim();

            var value =
                cookiePart[(separatorIndex + 1)..]
                    .Trim();

            if (name.Equals(
                    "B1SESSION",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Equals(
                    "ROUTEID",
                    StringComparison.OrdinalIgnoreCase))
            {
                cookies[name] = value;
            }
        }

        if (!cookies.ContainsKey("B1SESSION"))
        {
            throw new InvalidOperationException(
                "SAP Service Layer login succeeded but B1SESSION was not returned.");
        }

        // A preferred route may have been supplied on the
        // login request even if it is not repeated in Set-Cookie.
        if (!cookies.ContainsKey("ROUTEID"))
        {
            var preferredRouteId =
                NormalizeRouteId(
                    _options.PreferredRouteId);

            if (!string.IsNullOrWhiteSpace(
                    preferredRouteId))
            {
                cookies["ROUTEID"] =
                    preferredRouteId;
            }
        }

        var cookieParts = new List<string>
        {
            $"B1SESSION={cookies["B1SESSION"]}"
        };

        if (cookies.TryGetValue(
                "ROUTEID",
                out var routeId) &&
            !string.IsNullOrWhiteSpace(routeId))
        {
            cookieParts.Add(
                $"ROUTEID={routeId}");
        }

        _cookieHeader =
            string.Join("; ", cookieParts);

        var sessionTimeoutMinutes =
            ReadSessionTimeout(
                responseBody);

        _expiresAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(
                sessionTimeoutMinutes);

        _logger.LogInformation(
            "SAP SESSION CREATED SessionTimeoutMinutes={SessionTimeoutMinutes} HasRouteId={HasRouteId} ExpiresAtUtc={ExpiresAtUtc}",
            sessionTimeoutMinutes,
            cookies.ContainsKey("ROUTEID"),
            _expiresAtUtc);

        return _cookieHeader;
    }

    private int ReadSessionTimeout(string responseBody)
    {
        try
        {
            var root = JsonNode.Parse(responseBody);

            var sessionTimeoutNode = root?["SessionTimeout"];

            if (sessionTimeoutNode is JsonValue jsonValue)
            {
                try
                {
                    var sessionTimeout = jsonValue.GetValue<int>();

                    if (sessionTimeout > 0)
                    {
                        return sessionTimeout;
                    }
                }
                catch
                {
                    if (int.TryParse(
                            jsonValue.ToString(),
                            out var parsedTimeout) &&
                        parsedTimeout > 0)
                    {
                        return parsedTimeout;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to read SessionTimeout from SAP login response. Falling back to configured SessionMinutes.");
        }

        return Math.Max(
            1,
            _options.SessionMinutes);
    }

    private async Task DelayBeforeRetryAsync(
        int attempt,
        CancellationToken cancellationToken)
    {
        var baseSeconds =
            Math.Max(
                1,
                _options.LoginRetryDelaySeconds);

        var delaySeconds =
            baseSeconds *
            Math.Pow(2, attempt - 1);

        _logger.LogInformation(
            "SAP LOGIN RETRY DELAY Attempt={Attempt} DelaySeconds={DelaySeconds}",
            attempt,
            delaySeconds);

        await Task.Delay(
            TimeSpan.FromSeconds(delaySeconds),
            cancellationToken);
    }

    private static bool IsTransientStatusCode(
        HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static string NormalizeRouteId(
        string? routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return "";
        }

        var value = routeId.Trim();

        return value.StartsWith('.')
            ? value
            : "." + value;
    }

    private static string Truncate(
        string value,
        int maximumLength)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength] +
               "...TRUNCATED";
    }
}