using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Zladovce.Infrastructure;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Persistence;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Zladovce.Lambda;

/// <summary>
/// Input of the migration Lambda. <c>{}</c> or <c>{ "action": "migrate" }</c> applies migrations;
/// <c>{ "action": "bootstrap" }</c> creates and configures the database roles as the master user.
/// </summary>
public sealed class MigrationRequest
{
    public const string MigrateAction = "migrate";
    public const string BootstrapAction = "bootstrap";

    [JsonPropertyName("action")]
    public string? Action { get; set; }
}

/// <summary>Output of the migration Lambda, shown in the pipeline log.</summary>
public sealed class MigrationResponse
{
    /// <summary><c>migrate</c> or <c>bootstrap</c>.</summary>
    public string Action { get; init; } = MigrationRequest.MigrateAction;

    /// <summary>Migrations applied by this invocation, in order. Empty when the schema was already current (and for bootstrap).</summary>
    public required IReadOnlyList<string> AppliedMigrations { get; init; }

    /// <summary>Newest migration in the database after the run (migrate only).</summary>
    public string? CurrentMigration { get; init; }

    /// <summary>Roles configured by a bootstrap: the migrator and the application role.</summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>
/// The migration Lambda. Handler string: <c>Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync</c>.
/// <list type="bullet">
/// <item><c>migrate</c> (default): applies pending EF Core migrations as <c>Database__User</c> (the schema-owning
/// migrator role, RDS IAM authentication). Invoked by the pipeline before a new API version is published.</item>
/// <item><c>bootstrap</c>: reads the master credentials from <c>Database__MasterSecretArn</c> (Secrets Manager) and
/// idempotently creates <c>Database__User</c> (migrator: owns the schema) and <c>Database__AppUser</c> (API: data
/// access only), both with <c>rds_iam</c>. Run once per environment before the first migrate.</item>
/// </list>
/// A failure throws, so the invocation reports an error and the pipeline stops.
/// </summary>
public sealed class MigrationRunner
{
    private readonly IServiceProvider _services;

    /// <summary>Used by the Lambda runtime.</summary>
    public MigrationRunner()
        : this(new ConfigurationBuilder().AddEnvironmentVariables().Build())
    {
    }

    /// <summary>Used by tests and local runs with explicit configuration.</summary>
    /// <param name="configuration">Settings (section <c>Database</c>).</param>
    /// <param name="masterCredentials">Replaces Secrets Manager (tests).</param>
    public MigrationRunner(IConfiguration configuration, IMasterCredentialsProvider? masterCredentials = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
            .AddFilter("Npgsql", LogLevel.Warning)
            .AddJsonConsole());
        services.AddPersistence(configuration);

        if (masterCredentials is not null)
        {
            services.AddSingleton(masterCredentials);
        }

        services.TryAddSingleton<IMasterCredentialsProvider, SecretsManagerMasterCredentialsProvider>();
        services.AddSingleton<DatabaseRoleBootstrapper>();
        _services = services.BuildServiceProvider();
    }

    public async Task<MigrationResponse> HandleAsync(MigrationRequest? request, ILambdaContext context)
    {
        // Leave a margin before the Lambda timeout so a slow migration fails with a clear cancellation.
        using var timeout = new CancellationTokenSource();
        if (context.RemainingTime > TimeSpan.FromSeconds(10))
        {
            timeout.CancelAfter(context.RemainingTime - TimeSpan.FromSeconds(5));
        }

        var action = string.IsNullOrWhiteSpace(request?.Action) ? MigrationRequest.MigrateAction : request.Action.Trim().ToLowerInvariant();

        switch (action)
        {
            case MigrationRequest.BootstrapAction:
            {
                var outcome = await _services.GetRequiredService<DatabaseRoleBootstrapper>().BootstrapAsync(timeout.Token);
                return new MigrationResponse
                {
                    Action = MigrationRequest.BootstrapAction,
                    AppliedMigrations = [],
                    Roles = [outcome.MigratorRole, outcome.AppRole],
                };
            }

            case MigrationRequest.MigrateAction:
            {
                await using var scope = _services.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(timeout.Token);
                return new MigrationResponse
                {
                    Action = MigrationRequest.MigrateAction,
                    AppliedMigrations = outcome.AppliedMigrations,
                    CurrentMigration = outcome.CurrentMigration,
                };
            }

            default:
                throw new ArgumentException("Unknown action. Use \"migrate\" or \"bootstrap\".", nameof(request));
        }
    }
}
