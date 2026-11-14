using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Zladovce.Infrastructure.Persistence;

/// <summary>Result of a migration run.</summary>
/// <param name="AppliedMigrations">Migrations applied by this run, in order. Empty when the schema was already current.</param>
/// <param name="CurrentMigration">The newest migration in the database after the run.</param>
public sealed record MigrationOutcome(IReadOnlyList<string> AppliedMigrations, string? CurrentMigration);

/// <summary>
/// Applies pending EF Core migrations. Used by the migration Lambda in the deployment pipeline and,
/// optionally, by the API at start-up in Development. Never called at start-up in other environments.
/// </summary>
public sealed class DatabaseMigrator(ZladovceDbContext db, ILogger<DatabaseMigrator> logger)
{
    public async Task<MigrationOutcome> MigrateAsync(CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date.");
        }
        else
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, pending);
            await db.Database.MigrateAsync(cancellationToken);
        }

        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        return new MigrationOutcome(pending, applied.LastOrDefault());
    }
}
