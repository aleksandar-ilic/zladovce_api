using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zladovce.Api.Errors;

public static class ProblemWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Optional members of ApiProblem are skipped through their own attributes; nested payloads (the fresh quote of a
        // checkout conflict) keep their nulls, exactly like the normal responses of the same shape.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        // Serbian letters are written as-is; the body is JSON, never HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task WriteAsync(HttpContext context, ApiProblem problem)
    {
        var body = problem with { TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier };

        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = ApiProblem.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions, context.RequestAborted);
    }
}

/// <summary>Turns every unhandled exception into a Problem Details response and logs it once.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (Exception exception)
        {
            var problem = ProblemMapper.FromException(exception);

            if (ProblemMapper.IsExpected(exception))
            {
                // Exception messages of expected failures contain only codes and fixed texts, never request data.
                logger.LogInformation(
                    "Request refused with {StatusCode} {ErrorCode} ({ExceptionType})",
                    problem.Status, problem.Code, exception.GetType().Name);
            }
            else
            {
                logger.LogError(exception, "Unhandled exception; responding with {StatusCode} {ErrorCode}", problem.Status, problem.Code);
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            if (exception is Application.Common.Errors.RateLimitedException limited)
            {
                context.Response.Headers.RetryAfter = limited.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await ProblemWriter.WriteAsync(context, problem);
        }
    }
}

public static class ErrorHandlingExtensions
{
    /// <summary>
    /// Adds exception handling and gives a Problem Details body to error responses that have none
    /// (unknown route, 401/403 from authentication and authorization, 405).
    /// </summary>
    public static IApplicationBuilder UseZladovceErrorHandling(this IApplicationBuilder app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseStatusCodePages(statusCodeContext =>
            ProblemWriter.WriteAsync(
                statusCodeContext.HttpContext,
                ProblemMapper.FromStatusCode(statusCodeContext.HttpContext.Response.StatusCode)));
        return app;
    }
}
