using Microsoft.Extensions.Options;
using Zladovce.Application.Notifications;

namespace Zladovce.Api.Notifications;

/// <summary>
/// Local development stand-in for the outbox worker Lambda: with <c>Email:Dispatcher:RunInProcess=true</c> the API sends due
/// emails itself every <c>Email:Dispatcher:IntervalSeconds</c>. Never registered inside AWS Lambda.
/// </summary>
public sealed class OutboxDispatcherService(
    IServiceScopeFactory scopes,
    IOptions<EmailOptions> options,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Dispatcher;
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.IntervalSeconds, 1, 3600));
        logger.LogInformation("In-process email dispatcher started (every {Interval} s).", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await RunOnceAsync(settings, interval, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(EmailDispatcherOptions settings, TimeSpan interval, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>()
                .DispatchAsync(Math.Max(1, settings.BatchSize), interval + TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // The database may be down for a moment; the next tick tries again.
            logger.LogWarning("Email dispatcher run failed: {ExceptionType}: {Message}", exception.GetType().Name, exception.Message);
        }
    }
}
