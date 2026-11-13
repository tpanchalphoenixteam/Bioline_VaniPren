using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace PH.SapB1.Vaniperen.Logging;

public sealed class HttpRequestResponseLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DeepLoggingOptions _options;
    private readonly ILogger<HttpRequestResponseLoggingMiddleware> _logger;

    public HttpRequestResponseLoggingMiddleware(
        RequestDelegate next,
        IOptions<DeepLoggingOptions> options,
        ILogger<HttpRequestResponseLoggingMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || IsExcluded(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        var requestBody = "";

        if (_options.LogRequestBody)
        {
            requestBody = await ReadRequestBodyAsync(context.Request);
        }

        var requestHeaders = context.Request.Headers
            .ToDictionary(
                x => x.Key,
                x => LogSanitizer.MaskHeader(x.Key, string.Join(",", x.Value.ToArray())));

        _logger.LogInformation(
            "HTTP REQUEST START {Method} {Path}{QueryString} Headers={Headers} Body={Body}",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            requestHeaders,
            LogSanitizer.MaskBody(requestBody, _options.MaxBodyChars));

        var originalBodyStream = context.Response.Body;

        await using var responseBodyStream = new MemoryStream();
        context.Response.Body = responseBodyStream;

        try
        {
            await _next(context);

            stopwatch.Stop();

            var responseBody = "";

            if (_options.LogResponseBody)
            {
                responseBody = await ReadResponseBodyAsync(responseBodyStream);
            }

            _logger.LogInformation(
                "HTTP REQUEST END {Method} {Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs} ResponseBody={ResponseBody}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                LogSanitizer.MaskBody(responseBody, _options.MaxBodyChars));

            responseBodyStream.Position = 0;
            await responseBodyStream.CopyToAsync(originalBodyStream);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(
                ex,
                "HTTP REQUEST FAILED {Method} {Path} ElapsedMs={ElapsedMs}",
                context.Request.Method,
                context.Request.Path,
                stopwatch.ElapsedMilliseconds);

            throw;
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private bool IsExcluded(PathString path)
    {
        var pathValue = path.Value ?? "";

        return _options.ExcludedPaths.Any(x =>
            pathValue.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();

        request.Body.Position = 0;

        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var body = await reader.ReadToEndAsync();

        request.Body.Position = 0;

        return body;
    }

    private static async Task<string> ReadResponseBodyAsync(MemoryStream responseBodyStream)
    {
        responseBodyStream.Position = 0;

        using var reader = new StreamReader(
            responseBodyStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var body = await reader.ReadToEndAsync();

        responseBodyStream.Position = 0;

        return body;
    }
}