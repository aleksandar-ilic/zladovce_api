using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zladovce.Infrastructure.Database;

/// <summary>Builds the single <see cref="NpgsqlDataSource"/> of the process from <see cref="DatabaseOptions"/>.</summary>
public static class NpgsqlDataSourceFactory
{
    public static NpgsqlDataSource Create(
        DatabaseOptions options,
        IRdsAuthTokenGenerator tokenGenerator,
        TimeProvider timeProvider,
        ILoggerFactory? loggerFactory = null)
    {
        options.Validate();

        var builder = new NpgsqlDataSourceBuilder(BuildConnectionString(options));
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        if (options.UseIamAuth)
        {
            var provider = new RdsAuthTokenProvider(tokenGenerator, options, timeProvider);
            builder.UsePasswordProvider(
                _ => provider.GetToken(),
                (_, _) => ValueTask.FromResult(provider.GetToken()));
        }

        return builder.Build();
    }

    /// <summary>The connection string without any password when IAM authentication is used.</summary>
    public static string BuildConnectionString(DatabaseOptions options)
    {
        options.Validate();

        NpgsqlConnectionStringBuilder csb;
        if (options.UseIamAuth)
        {
            csb = new NpgsqlConnectionStringBuilder
            {
                Host = options.Host,
                Port = options.Port,
                Database = options.Name,
                Username = options.User,
                // IAM authentication is refused by RDS on unencrypted connections.
                SslMode = string.IsNullOrWhiteSpace(options.RootCertificatePath) ? SslMode.Require : SslMode.VerifyFull,
            };

            if (!string.IsNullOrWhiteSpace(options.RootCertificatePath))
            {
                csb.RootCertificate = options.RootCertificatePath;
            }
        }
        else
        {
            csb = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        }

        if (options.MaxPoolSize is { } maxPoolSize)
        {
            csb.MaxPoolSize = maxPoolSize;
            csb.MinPoolSize = Math.Min(csb.MinPoolSize, maxPoolSize);
        }

        csb.ApplicationName ??= "zladovce";
        return csb.ConnectionString;
    }
}
