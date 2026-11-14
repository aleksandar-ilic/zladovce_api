using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Zladovce.Infrastructure.Database;

/// <summary>User name and password of the database master user. Never logged.</summary>
public sealed record DatabaseCredentials(string Username, string Password)
{
    public override string ToString() => $"DatabaseCredentials {{ Username = {Username} }}";
}

/// <summary>Supplies the master credentials for the one-off role bootstrap.</summary>
public interface IMasterCredentialsProvider
{
    Task<DatabaseCredentials> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the RDS-managed master secret (<c>Database:MasterSecretArn</c>) from AWS Secrets Manager.</summary>
public sealed class SecretsManagerMasterCredentialsProvider(IOptions<DatabaseOptions> options) : IMasterCredentialsProvider
{
    public async Task<DatabaseCredentials> GetAsync(CancellationToken cancellationToken)
    {
        var secretArn = options.Value.MasterSecretArn;
        if (string.IsNullOrWhiteSpace(secretArn))
        {
            throw new InvalidOperationException("Database:MasterSecretArn is required for the bootstrap action.");
        }

        using var client = string.IsNullOrWhiteSpace(options.Value.Region)
            ? new AmazonSecretsManagerClient()
            : new AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(options.Value.Region));

        var secret = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn }, cancellationToken);
        return ParseSecret(secret.SecretString);
    }

    /// <summary>Parses the RDS secret JSON. The message of a failure never contains the secret.</summary>
    public static DatabaseCredentials ParseSecret(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var username = document.RootElement.GetProperty("username").GetString();
            var password = document.RootElement.GetProperty("password").GetString();
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                return new DatabaseCredentials(username, password);
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        throw new InvalidOperationException("The master secret is not a JSON object with 'username' and 'password'.");
    }
}

/// <summary>
/// SQL of the role bootstrap. Idempotent: every statement can run again on an already bootstrapped database.
/// Result:
/// <list type="bullet">
/// <item>the migrator role owns the <c>public</c> schema and every table, view, sequence and function in it, and may create extensions;</item>
/// <item>the application role may connect and read/write data (SELECT, INSERT, UPDATE, DELETE, sequences, functions),
/// now and for every object the migrator creates later (default privileges), but cannot create, alter or drop anything;</item>
/// <item>both roles can sign in with RDS IAM tokens (<c>rds_iam</c>); neither has a password.</item>
/// </list>
/// </summary>
public static partial class DatabaseRoleSql
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    public static IReadOnlyList<string> Build(string migratorRole, string appRole, string databaseName)
    {
        EnsureIdentifier(migratorRole, "Database:User");
        EnsureIdentifier(appRole, "Database:AppUser");
        if (migratorRole == appRole)
        {
            throw new InvalidOperationException("Database:User and Database:AppUser must be different roles.");
        }

        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The database name is not usable.");
        }

        var migrator = Quote(migratorRole);
        var app = Quote(appRole);
        var database = Quote(databaseName);

        return
        [
            $"""
            DO $bootstrap$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{migratorRole}') THEN
                    CREATE ROLE {migrator} LOGIN;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{appRole}') THEN
                    CREATE ROLE {app} LOGIN;
                END IF;
            END
            $bootstrap$;
            """,
            $"ALTER ROLE {migrator} LOGIN",
            $"ALTER ROLE {app} LOGIN",
            $"GRANT rds_iam TO {migrator}",
            $"GRANT rds_iam TO {app}",

            // The master needs to act as the migrator to hand over ownership and to set its default privileges.
            $"GRANT {migrator} TO CURRENT_USER WITH INHERIT TRUE, SET TRUE",

            $"GRANT CONNECT, CREATE, TEMPORARY ON DATABASE {database} TO {migrator}",
            $"GRANT CONNECT ON DATABASE {database} TO {app}",

            "REVOKE CREATE ON SCHEMA public FROM PUBLIC",
            $"ALTER SCHEMA public OWNER TO {migrator}",
            $"GRANT USAGE ON SCHEMA public TO {app}",

            // Objects created earlier by the master (for example by a first deployment that migrated as master).
            $$"""
            DO $bootstrap$
            DECLARE
                item record;
            BEGIN
                FOR item IN
                    SELECT c.oid::regclass AS name
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public'
                      AND c.relkind IN ('r', 'p', 'v', 'S', 'f')
                      AND c.relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                      AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('a', 'i', 'e'))
                LOOP
                    EXECUTE format('ALTER TABLE %s OWNER TO %I', item.name, '{{migratorRole}}');
                END LOOP;

                FOR item IN
                    SELECT p.oid::regprocedure AS name
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = 'public'
                      AND p.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                      AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
                LOOP
                    EXECUTE format('ALTER ROUTINE %s OWNER TO %I', item.name, '{{migratorRole}}');
                END LOOP;
            END
            $bootstrap$;
            """,

            // Data access for the application: what exists now ...
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {app}",
            $"GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {app}",
            $"GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {app}",

            // ... and whatever the migrator creates later.
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {migrator} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {app}",
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {migrator} IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {app}",
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {migrator} IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO {app}",
        ];
    }

    private static void EnsureIdentifier(string? role, string setting)
    {
        if (string.IsNullOrEmpty(role) || !IdentifierPattern().IsMatch(role))
        {
            throw new InvalidOperationException($"{setting} must be a lower-case PostgreSQL role name (letters, digits, underscore).");
        }
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

/// <summary>Result of the role bootstrap.</summary>
public sealed record BootstrapOutcome(string MigratorRole, string AppRole);

/// <summary>
/// Creates and configures the database roles (<see cref="DatabaseRoleSql"/>) as the master user. Run once per
/// environment, and again whenever in doubt: every step is idempotent. Never part of the API.
/// </summary>
public sealed class DatabaseRoleBootstrapper(
    IOptions<DatabaseOptions> options,
    IMasterCredentialsProvider credentialsProvider,
    ILogger<DatabaseRoleBootstrapper> logger)
{
    public async Task<BootstrapOutcome> BootstrapAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var migrator = settings.User ?? string.Empty;
        var app = settings.AppUser ?? string.Empty;

        var credentials = await credentialsProvider.GetAsync(cancellationToken);
        var connectionString = BuildMasterConnectionString(settings, credentials);
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? throw new InvalidOperationException("The database name is not configured.");

        var statements = DatabaseRoleSql.Build(migrator, app, databaseName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Database roles bootstrapped: migrator {MigratorRole}, application {AppRole}.", migrator, app);
        return new BootstrapOutcome(migrator, app);
    }

    /// <summary>
    /// The master connection: the endpoint of the normal configuration (IAM mode: host, port, database name and TLS;
    /// otherwise the connection string) with the master user name and password.
    /// </summary>
    public static string BuildMasterConnectionString(DatabaseOptions settings, DatabaseCredentials credentials)
    {
        NpgsqlConnectionStringBuilder builder;
        if (settings.UseIamAuth)
        {
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Name))
            {
                throw new InvalidOperationException("Database:Host and Database:Name are required for the bootstrap action.");
            }

            builder = new NpgsqlConnectionStringBuilder
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Name,
                SslMode = string.IsNullOrWhiteSpace(settings.RootCertificatePath) ? SslMode.Require : SslMode.VerifyFull,
            };

            if (!string.IsNullOrWhiteSpace(settings.RootCertificatePath))
            {
                builder.RootCertificate = settings.RootCertificatePath;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                throw new InvalidOperationException("Database:ConnectionString is required for the bootstrap action outside IAM mode.");
            }

            builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
        }

        builder.Username = credentials.Username;
        builder.Password = credentials.Password;
        builder.Pooling = false;
        builder.ApplicationName = "zladovce-bootstrap";
        return builder.ConnectionString;
    }
}
