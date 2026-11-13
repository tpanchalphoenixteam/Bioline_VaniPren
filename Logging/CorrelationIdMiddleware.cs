using Serilog.Context;

namespace PH.SapB1.Vaniperen.Logging;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemName = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId =
            context.Request.Headers.TryGetValue(
                HeaderName,
                out var existing) &&
            !string.IsNullOrWhiteSpace(existing.FirstOrDefault())
                ? existing.First()!
                : Guid.NewGuid().ToString("N");

        context.Items[ItemName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty(
                   "CorrelationId",
                   correlationId))
        {
            await _next(context);
        }
    }
}