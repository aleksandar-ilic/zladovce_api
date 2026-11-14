using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Zladovce.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> command-line tools. The connection string comes from the
/// <c>ZLADOVCE_DB_CONNECTION</c> environment variable, falling back to the local Docker Compose database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZladovceDbContext>
{
    private const string LocalComposeConnection =
        "Host=localhost;Port=5433;Database=zladovce;Username=zladovce;Password=zladovce_dev";

    public ZladovceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ZLADOVCE_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = LocalComposeConnection;
        }

        var options = new DbContextOptionsBuilder<ZladovceDbContext>();
        InfrastructureServices.ConfigureNpgsql(options, connectionString);
        return new ZladovceDbContext(options.Options);
    }
}
