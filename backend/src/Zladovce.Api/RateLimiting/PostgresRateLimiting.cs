using System.Net;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;

namespace Zladovce.Api.RateLimiting;

/// <summary>Configuration section <c>ClientIp</c>: how the client address is determined behind proxies.</summary>
public sealed class ClientIpOptions
{
    public const string SectionName = "ClientIp";

    /// <summary>
    /// Number of trusted proxies in front of the API that append to <c>X-Forwarded-For</c>. The client address is the entry this
    /// many positions from the right (the address the outermost trusted proxy saw); entries further left are client-supplied and
    /// ignored. Default 1: API Gateway HTTP API appends the caller's address. 0 = use the connection address only. Without the
    /// header (local Kestrel) the connection address is used.
    /// </summary>
    public int TrustedProxyHops { get; set; } = 1;
}

public static class ClientIpResolver
{
    /// <summary>The client address of the request (see <see cref="ClientIpOptions"/>); null when unknown.</summary>
    public static IPAddress? Resolve(HttpContext context, ClientIpOptions options)
    {
        if (options.TrustedProxyHops > 0)
        {
            var entries = context.Request.Headers["X-Forwarded-For"]
                .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();

            if (entries.Count > 0)
            {
                var index = Math.Max(0, entries.Count - options.TrustedProxyHops);
                if (IPAddress.TryParse(entries[index], out var forwarded))
                {
                    return forwarded.IsIPv4MappedToIPv6 ? forwarded.MapToIPv4() : forwarded;
                }
            }
        }

        var remote = context.Connection.RemoteIpAddress;
        return remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote;
    }
}

/// <summary>Endpoint metadata: the endpoint is limited by the named policy (<see cref="RateLimitPolicies"/>).</summary>
/// <param name="PerClient">True: one counter per client IP. False: one counter for all clients of the endpoint (a global cap).</param>
public sealed record PostgresRateLimitMetadata(string PolicyName, bool PerClient = true);

public static class PostgresRateLimitingExtensions
{
    /// <summary>
    /// Limits the endpoint per client IP with a policy of <see cref="RateLimitPolicies"/> (counters in PostgreSQL, shared by
    /// all Lambda instances). Every request counts, also invalid ones. Exceeding it gives 429 <c>RATE_LIMITED</c> with
    /// <c>Retry-After</c> (seconds).
    /// </summary>
    public static TBuilder RequirePostgresRateLimit<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PostgresRateLimitMetadata(policyName));

    /// <summary>
    /// A cap for all clients of the endpoint together (one shared counter). Can be combined with
    /// <see cref="RequirePostgresRateLimit{TBuilder}"/>; every limit is checked, the per-client ones first.
    /// </summary>
    public static TBuilder RequireGlobalPostgresRateLimit<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PostgresRateLimitMetadata(policyName, PerClient: false));
}

/// <summary>Applies <see cref="PostgresRateLimitMetadata"/> before the endpoint (and before request validation) runs.</summary>
public sealed class PostgresRateLimitingMiddleware(RequestDelegate next)
{
    /// <summary>Key used when no client address can be determined; all such requests share one bucket.</summary>
    public const string UnknownClient = "unknown";

    /// <summary>Key of the shared counter of a global cap.</summary>
    public const string AllClients = "*";

    public async Task InvokeAsync(HttpContext context, IOptionsSnapshot<RateLimitingOptions> options, IOptions<ClientIpOptions> clientIp, IRateLimiter limiter)
    {
        var limits = context.GetEndpoint()?.Metadata.GetOrderedMetadata<PostgresRateLimitMetadata>() ?? [];
        if (limits.Count == 0 || !options.Value.Enabled || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var client = ClientIpResolver.Resolve(context, clientIp.Value)?.ToString() ?? UnknownClient;
        foreach (var limit in limits.OrderByDescending(l => l.PerClient))
        {
            var policy = options.Value.Resolve(limit.PolicyName);
            var decision = await limiter.AcquireAsync(policy, limit.PerClient ? client : AllClients, context.RequestAborted);
            if (!decision.Allowed)
            {
                throw new RateLimitedException(decision.RetryAfter);
            }
        }

        await next(context);
    }
}
