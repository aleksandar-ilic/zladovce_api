using Amazon;
using Amazon.RDS.Util;

namespace Zladovce.Infrastructure.Database;

/// <summary>Creates an RDS IAM authentication token (a locally signed, 15-minute database password).</summary>
public interface IRdsAuthTokenGenerator
{
    string GenerateToken(string region, string host, int port, string user);
}

/// <summary>Signs the token with the ambient AWS credentials (the Lambda execution role). No network call is made.</summary>
public sealed class AwsRdsAuthTokenGenerator : IRdsAuthTokenGenerator
{
    public string GenerateToken(string region, string host, int port, string user) =>
        RDSAuthTokenGenerator.GenerateAuthToken(RegionEndpoint.GetBySystemName(region), host, port, user);
}

/// <summary>
/// Supplies the database password for new physical connections and reuses a token for
/// <see cref="TokenReuse"/> (tokens are valid for 15 minutes). Expiry is checked against the wall clock on
/// every call, so a Lambda instance that was frozen for a long time never presents a stale token —
/// which a timer-based refresh could.
/// </summary>
public sealed class RdsAuthTokenProvider(IRdsAuthTokenGenerator generator, DatabaseOptions options, TimeProvider timeProvider)
{
    public static readonly TimeSpan TokenReuse = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private string? _token;
    private DateTimeOffset _refreshAfter;

    public string GetToken()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_token is null || now >= _refreshAfter)
            {
                _token = generator.GenerateToken(options.Region!, options.Host!, options.Port, options.User!);
                _refreshAfter = now + TokenReuse;
            }

            return _token;
        }
    }
}
