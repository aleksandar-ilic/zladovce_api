namespace Zladovce.Infrastructure.Database;

/// <summary>
/// Configuration section <c>Database</c>. Two modes:
/// a plain <see cref="ConnectionString"/> (local development, tests), or
/// <see cref="UseIamAuth"/> with host/port/name/user/region, where the password is a short-lived
/// RDS IAM token signed locally with the Lambda role (no stored database password).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Full Npgsql connection string. Used when <see cref="UseIamAuth"/> is false.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Switches to RDS IAM database authentication.</summary>
    public bool UseIamAuth { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 5432;

    /// <summary>Database name.</summary>
    public string? Name { get; set; }

    /// <summary>Database role that has the <c>rds_iam</c> grant.</summary>
    public string? User { get; set; }

    /// <summary>AWS region of the database instance, for example <c>eu-central-1</c>.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Optional path to the RDS certificate bundle. When set, the server certificate and host name are
    /// verified (<c>VerifyFull</c>); otherwise the connection is encrypted without verification (<c>Require</c>).
    /// </summary>
    public string? RootCertificatePath { get; set; }

    /// <summary>Upper bound of pooled connections per process. Kept small because each Lambda instance has its own pool.</summary>
    public int? MaxPoolSize { get; set; }

    /// <summary>Development convenience only: apply migrations when the API starts. Ignored outside Development.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>
    /// Migration Lambda, <c>bootstrap</c> action only: ARN of the RDS-managed master secret
    /// (JSON with <c>username</c> and <c>password</c>). Never used by the API.
    /// </summary>
    public string? MasterSecretArn { get; set; }

    /// <summary>
    /// Migration Lambda, <c>bootstrap</c> action only: the role the API connects as (data access only, for example
    /// <c>zladovce_app</c>). The schema-owning role is <see cref="User"/> (for example <c>zladovce_migrator</c>).
    /// </summary>
    public string? AppUser { get; set; }

    /// <summary>Throws when the configuration cannot produce a connection. Messages never include secrets.</summary>
    public void Validate()
    {
        if (!UseIamAuth)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                throw new InvalidOperationException(
                    "Database is not configured: set Database:ConnectionString, or Database:UseIamAuth=true with Host, Name, User and Region.");
            }

            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) missing.Add(nameof(Host));
        if (string.IsNullOrWhiteSpace(Name)) missing.Add(nameof(Name));
        if (string.IsNullOrWhiteSpace(User)) missing.Add(nameof(User));
        if (string.IsNullOrWhiteSpace(Region)) missing.Add(nameof(Region));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Database:UseIamAuth is true but these Database settings are missing: {string.Join(", ", missing)}.");
        }

        if (Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Database:Port must be between 1 and 65535.");
        }

        if (MaxPoolSize is <= 0)
        {
            throw new InvalidOperationException("Database:MaxPoolSize must be greater than zero.");
        }
    }
}
