using System.Security.Cryptography;
using System.Text;

namespace Zladovce.Application.Common;

/// <summary>Configuration section <c>RateLimiting</c>.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>False switches every limit off (integration tests that are not about rate limiting).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Overrides of the built-in policies by name (<see cref="RateLimitPolicies"/>).</summary>
    public Dictionary<string, RateLimitPolicyOptions> Policies { get; set; } = new(StringComparer.Ordinal);

    public RateLimitPolicy Resolve(string name)
    {
        var defaults = RateLimitPolicies.Defaults.TryGetValue(name, out var builtIn)
            ? builtIn
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown rate limit policy.");

        if (!Policies.TryGetValue(name, out var overrides))
        {
            return defaults;
        }

        return new RateLimitPolicy(
            name,
            overrides.PermitLimit is > 0 and var limit ? limit : defaults.PermitLimit,
            overrides.WindowSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : defaults.Window);
    }
}

public sealed class RateLimitPolicyOptions
{
    public int? PermitLimit { get; set; }
    public int? WindowSeconds { get; set; }
}

/// <summary>A limit: at most <paramref name="PermitLimit"/> requests per client in each fixed window.</summary>
public sealed record RateLimitPolicy(string Name, int PermitLimit, TimeSpan Window);

/// <summary>Built-in policies (API_SPECIFICATION.md, "Rate limits").</summary>
public static class RateLimitPolicies
{
    /// <summary><c>POST /api/v1/orders</c>: 5 per 10 minutes per IP.</summary>
    public const string CreateOrder = "CreateOrder";

    /// <summary><c>POST /api/v1/tracking/lookup</c>: 10 per 10 minutes per IP.</summary>
    public const string TrackingLookup = "TrackingLookup";

    /// <summary>
    /// <c>POST /api/v1/tracking/lookup</c>, all clients together: 300 per minute. A coarse safety cap against guessing spread
    /// over many addresses (API Gateway route throttling is the outer limit).
    /// </summary>
    public const string TrackingLookupGlobal = "TrackingLookupGlobal";

    public static readonly IReadOnlyDictionary<string, RateLimitPolicy> Defaults = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal)
    {
        [CreateOrder] = new(CreateOrder, 5, TimeSpan.FromMinutes(10)),
        [TrackingLookup] = new(TrackingLookup, 10, TimeSpan.FromMinutes(10)),
        [TrackingLookupGlobal] = new(TrackingLookupGlobal, 300, TimeSpan.FromMinutes(1)),
    };
}

/// <param name="Allowed">False when the limit of the current window is exceeded.</param>
/// <param name="RetryAfter">Time until the current window ends (when not allowed).</param>
public sealed record RateLimitDecision(bool Allowed, int Count, TimeSpan RetryAfter);

/// <summary>
/// Counts requests per client and policy in PostgreSQL (table <c>rate_limit_buckets</c>), so the limit holds across Lambda
/// instances. Fixed windows aligned to the Unix epoch; each call is one atomic upsert outside any business transaction.
/// The client key (an IP address) is stored only as a SHA-256 hash.
/// </summary>
public interface IRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(RateLimitPolicy policy, string clientKey, CancellationToken cancellationToken);

    /// <summary>Deletes buckets whose window started before <paramref name="before"/>. Called by the outbox worker on every run.</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

public static class RateLimitKeys
{
    /// <summary><c>{policy}:{hex sha256(client key)}</c>.</summary>
    public static string BucketKey(string policyName, string clientKey) =>
        $"{policyName}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientKey)))}";

    /// <summary>Start of the fixed window containing <paramref name="now"/>.</summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now, TimeSpan window)
    {
        var ticks = now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return DateTimeOffset.UnixEpoch.AddTicks(ticks - (ticks % window.Ticks));
    }
}
