using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;
using Zladovce.Application.Identity;
using Zladovce.Application.Media;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Identity;
using Zladovce.Infrastructure.Media;
using Zladovce.Infrastructure.Notifications;
using Zladovce.Infrastructure.Persistence;
using Zladovce.Infrastructure.RateLimiting;

namespace Zladovce.Infrastructure;

public static class InfrastructureServices
{
    /// <summary>Registers persistence only: data source, DbContext and the migrator. Enough for the migration Lambda.</summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.TryAddSingleton<IRdsAuthTokenGenerator, AwsRdsAuthTokenGenerator>();

        // One data source (and one connection pool) per process, created on first use.
        services.AddSingleton(sp => NpgsqlDataSourceFactory.Create(
            sp.GetRequiredService<IOptions<DatabaseOptions>>().Value,
            sp.GetRequiredService<IRdsAuthTokenGenerator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));

        services.AddDbContext<ZladovceDbContext>((sp, options) =>
        {
            ConfigureNpgsql(options, sp.GetRequiredService<NpgsqlDataSource>());

            // Audit of administrator changes. Hosts without a request context (migration Lambda) have no auditing.
            if (sp.GetService<IRequestContext>() is { } requestContext)
            {
                options.AddInterceptors(new AuditSaveChangesInterceptor(requestContext, sp.GetRequiredService<TimeProvider>()));
            }
        });

        services.AddScoped<IZladovceDbContext>(sp => sp.GetRequiredService<ZladovceDbContext>());
        services.AddScoped<DatabaseMigrator>();

        return services;
    }

    /// <summary>
    /// The media bucket and the image transformer, without any database. Used by the API and by the image processor Lambda.
    /// The S3 client is created on first use, so hosts that never touch media need no media configuration.
    /// </summary>
    public static IServiceCollection AddMediaStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
        services.TryAddSingleton<IMediaStorage, S3MediaStorage>();
        services.TryAddSingleton<IImageTransformer, MagickImageTransformer>();
        return services;
    }

    /// <summary>Registers everything the API needs from this project.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.AddMediaStorage(configuration);
        services.Configure<CatalogOptions>(configuration.GetSection(CatalogOptions.SectionName));
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.AddSingleton<IRateLimiter, PostgresRateLimiter>();
        services.AddScoped<Application.Orders.ICheckoutStore, CheckoutStore>();
        services.AddScoped<Application.Orders.IOrderLocks, OrderLocks>();
        services.AddNotifications(configuration);

        services.Configure<CognitoOptions>(configuration.GetSection(CognitoOptions.SectionName));
        services.AddSingleton<IAdminProfileLookup, CognitoAdminProfileLookup>();

        return services;
    }

    internal static void ConfigureNpgsql(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, ConfigureProvider)
            .UseSnakeCaseNamingConvention();

    internal static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, ConfigureProvider)
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Products are loaded with two collections (variants and images); split queries avoid the cartesian product.
    /// No retrying execution strategy: services open their own transactions (stock changes) and must see failures.
    /// </summary>
    private static void ConfigureProvider(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
}
