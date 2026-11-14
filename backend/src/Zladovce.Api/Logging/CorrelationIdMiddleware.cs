using Serilog.Context;

namespace Zladovce.Api.Logging;

/// <summary>
/// Gives every request a correlation id: the caller's <c>X-Correlation-Id</c> when it is well formed,
/// otherwise a new one. The id is returned in the response header and attached to every log event.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Accept(context.Request.Headers[HeaderName].ToString()) ?? Guid.NewGuid().ToString("N");

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    /// <summary>Returns the value when it is safe to echo and log (short, id-like characters only); otherwise null.</summary>
    public static string? Accept(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return null;
        }

        foreach (var c in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return null;
            }
        }

        return candidate;
    }
}
