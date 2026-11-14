using Npgsql;
using Zladovce.Application.Common;

namespace Zladovce.Infrastructure.RateLimiting;

/// <summary>Row of <c>rate_limit_buckets</c>: requests of one client (hashed) and policy in one window.</summary>
public sealed class RateLimitBucket
{
    private RateLimitBucket()
    {
    }

    /// <summary><c>{policy}:{sha256 of the client IP}</c>.</summary>
    public string BucketKey { get; private set; } = null!;
    public DateTimeOffset WindowStart { get; private set; }
    public int Count { get; private set; }
}

/// <summary>
/// <see cref="IRateLimiter"/> on PostgreSQL. Uses its own pooled connection (autocommit), so a counted request stays
/// counted even when the business transaction of the request rolls back.
/// </summary>
public sealed class PostgresRateLimiter(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IRateLimiter
{
    public async Task<RateLimitDecision> AcquireAsync(RateLimitPolicy policy, string clientKey, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var windowStart = RateLimitKeys.WindowStart(now, policy.Window);

        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO rate_limit_buckets (bucket_key, window_start, count) VALUES ($1, $2, 1)
            ON CONFLICT (bucket_key, window_start) DO UPDATE SET count = rate_limit_buckets.count + 1
            RETURNING count
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = RateLimitKeys.BucketKey(policy.Name, clientKey) });
        command.Parameters.Add(new NpgsqlParameter { Value = windowStart });

        var count = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        var retryAfter = windowStart + policy.Window - now;

        return new RateLimitDecision(count <= policy.PermitLimit, count, retryAfter);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM rate_limit_buckets WHERE window_start < $1");
        command.Parameters.Add(new NpgsqlParameter { Value = before });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
