using Amazon.Lambda.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Notifications;
using Zladovce.Infrastructure;
using Zladovce.Infrastructure.Notifications;
using Zladovce.Infrastructure.RateLimiting;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Zladovce.Lambda;

/// <summary>Result of one outbox worker invocation (visible in the Lambda log / test output).</summary>
public sealed class OutboxWorkerResponse
{
    public required int Claimed { get; init; }
    public required int Sent { get; init; }
    public required int Retrying { get; init; }
    public required int Dead { get; init; }
    public required int Expired { get; init; }
    public required int PurgedRateLimitBuckets { get; init; }
}

/// <summary>
/// The outbox worker Lambda, scheduled every minute by EventBridge (the event content is ignored).
/// Handler string: <c>Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync</c>.
/// Sends at most <c>Email__Dispatcher__BatchSize</c> due emails (default 50) and stops claiming new ones when less than one send
/// timeout of the invocation's remaining time is left; deletes expired rate-limit windows. A failed email never fails the
/// invocation (it is retried with back-off); a database failure does (the next schedule tries again).
/// Configuration: <c>Database__*</c> (app role, IAM auth), <c>Email__*</c>, <c>Storefront__BaseUrl</c>,
/// <c>Tracking__TokenEncryptionKeySecretArn</c>.
/// </summary>
public sealed class OutboxWorkerFunction
{
    private readonly IServiceProvider _services;

    /// <summary>Used by the Lambda runtime.</summary>
    public OutboxWorkerFunction()
        : this(new ConfigurationBuilder().AddEnvironmentVariables().Build())
    {
    }

    /// <summary>Used by tests and local runs with explicit configuration.</summary>
    /// <param name="configureServices">Replaces services after the defaults (tests: clock).</param>
    public OutboxWorkerFunction(IConfiguration configuration, Action<IServiceCollection>? configureServices = null)
    {
        // Same rules as the API: a deployed worker with a broken email configuration fails at once.
        NotificationServices.EnsureConfiguration(
            configuration,
            isDevelopment: string.Equals(configuration["DOTNET_ENVIRONMENT"] ?? configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase),
            sendsEmail: true);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
            .AddFilter("Npgsql", LogLevel.Warning)
            .AddFilter("Amazon", LogLevel.Warning)
            .AddJsonConsole());
        services.AddPersistence(configuration);
        services.AddSingleton<IRateLimiter, PostgresRateLimiter>();
        services.AddNotifications(configuration);
        configureServices?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public async Task<OutboxWorkerResponse> HandleAsync(object? input, ILambdaContext context)
    {
        // Keep a margin for recording the last attempt before the Lambda timeout.
        var budget = context.RemainingTime - TimeSpan.FromSeconds(5);
        using var timeout = new CancellationTokenSource();
        if (context.RemainingTime > TimeSpan.FromSeconds(3))
        {
            timeout.CancelAfter(context.RemainingTime - TimeSpan.FromSeconds(1));
        }

        var batchSize = _services.GetRequiredService<IOptions<EmailOptions>>().Value.Dispatcher.BatchSize;
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>()
            .DispatchAsync(Math.Max(1, batchSize), budget > TimeSpan.Zero ? budget : TimeSpan.Zero, timeout.Token);

        return new OutboxWorkerResponse
        {
            Claimed = result.Claimed,
            Sent = result.Sent,
            Retrying = result.Retrying,
            Dead = result.Dead,
            Expired = result.Expired,
            PurgedRateLimitBuckets = result.PurgedRateLimitBuckets,
        };
    }
}
