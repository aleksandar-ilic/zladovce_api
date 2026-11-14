using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zladovce.Application.Common;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.StoreSettings;
using Zladovce.Infrastructure.Persistence;

namespace Zladovce.Infrastructure.Notifications;

/// <summary>
/// The outbox worker (A4 "Email outbox"). One run:
/// <list type="number">
/// <item>deletes expired rate-limit windows;</item>
/// <item>marks Dead the rows whose lease ran out during their last allowed attempt (a worker died mid-send);</item>
/// <item>claims due rows one at a time — <c>UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1)</c> sets
/// <c>Processing</c>, increments <c>attempt_count</c> and sets the lease <c>locked_until</c>, committed at once — so two
/// workers never hold the same row; a row is claimable again only when its lease has expired;</item>
/// <item>renders and sends it, then in one transaction writes the <c>email_logs</c> row and marks the outbox row Sent
/// (erasing <c>encrypted_token</c>) or Pending with the next back-off delay, or Dead after the last attempt. That update is
/// guarded by <c>status = 'Processing' AND attempt_count = claimed attempt</c>, so a worker whose lease was taken over
/// cannot overwrite the newer state.</item>
/// </list>
/// Residual risk (documented, not hidden): if the process dies after the provider accepted the email but before step 4
/// committed, the lease expires and the row is sent once more. The send timeout is shorter than the lease, so a slow but
/// alive worker is not overtaken; the attempt counter bounds repeats to the attempt limit.
/// </summary>
public sealed class EmailDispatcher(
    NpgsqlDataSource dataSource,
    ZladovceDbContext db,
    IEmailSender sender,
    ITrackingCodeProtector protector,
    IRateLimiter rateLimiter,
    IOptions<EmailOptions> options,
    IOptions<StorefrontOptions> storefront,
    TimeProvider timeProvider,
    ILogger<EmailDispatcher> logger) : IEmailDispatcher
{
    /// <summary>Rate-limit windows older than this are deleted (longer than any policy window).</summary>
    public static readonly TimeSpan RateLimitRetention = TimeSpan.FromDays(1);

    private const string LeaseExpiredError = "Slanje je prekinuto (radni proces je zaustavljen tokom poslednjeg pokušaja).";

    private EmailBranding? _branding;
    private StoreSettingsRecord? _settings;

    public async Task<EmailDispatchResult> DispatchAsync(int maxMessages, TimeSpan timeBudget, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var purged = await PurgeRateLimitsAsync(cancellationToken);
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new EmailDispatchResult(0, 0, 0, 0, 0, purged);
        }

        var expired = await ExpireAbandonedAsync(cancellationToken);
        var sendTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.Dispatcher.SendTimeoutSeconds));
        int claimed = 0, sent = 0, retrying = 0, dead = 0;

        while (claimed < maxMessages && !cancellationToken.IsCancellationRequested)
        {
            // Never start a send that might not finish (and be recorded) within the budget.
            if (started.Elapsed + sendTimeout + TimeSpan.FromSeconds(2) > timeBudget)
            {
                break;
            }

            if (await ClaimAsync(cancellationToken) is not { } claim)
            {
                break;
            }

            claimed++;
            switch (await ProcessAsync(claim.Id, claim.Attempt, sendTimeout, cancellationToken))
            {
                case EmailOutboxStatus.Sent:
                    sent++;
                    break;
                case EmailOutboxStatus.Dead:
                    dead++;
                    break;
                default:
                    retrying++;
                    break;
            }
        }

        if (claimed > 0 || expired > 0)
        {
            logger.LogInformation(
                "Email outbox run: {Claimed} claimed, {Sent} sent, {Retrying} retrying, {Dead} dead, {Expired} expired.",
                claimed, sent, retrying, dead, expired);
        }

        return new EmailDispatchResult(claimed, sent, retrying, dead, expired, purged);
    }

    private async Task<int> PurgeRateLimitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await rateLimiter.PurgeExpiredAsync(timeProvider.GetUtcNow() - RateLimitRetention, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Purging rate-limit windows failed: {ExceptionType}.", exception.GetType().Name);
            return 0;
        }
    }

    private async Task<int> ExpireAbandonedAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE email_outbox SET status = 'Dead', locked_until = NULL, last_error = $3
            WHERE status = 'Processing' AND locked_until <= $1 AND attempt_count >= $2
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter { Value = EmailRetryPolicy.MaxAttempts });
        command.Parameters.Add(new NpgsqlParameter { Value = LeaseExpiredError });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Claim(Guid Id, int Attempt);

    private async Task<Claim?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var command = dataSource.CreateCommand(
            """
            UPDATE email_outbox SET status = 'Processing', attempt_count = attempt_count + 1, locked_until = $2
            WHERE id = (
                SELECT id FROM email_outbox
                WHERE status IN ('Pending', 'Processing')
                  AND next_attempt_at <= $1
                  AND (locked_until IS NULL OR locked_until <= $1)
                  AND attempt_count < $3
                ORDER BY next_attempt_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id, attempt_count
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = now + TimeSpan.FromSeconds(Math.Max(10, options.Value.Dispatcher.LeaseSeconds)) });
        command.Parameters.Add(new NpgsqlParameter { Value = EmailRetryPolicy.MaxAttempts });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new Claim(reader.GetGuid(0), reader.GetInt32(1)) : null;
    }

    private async Task<EmailOutboxStatus> ProcessAsync(Guid id, int attempt, TimeSpan sendTimeout, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var message = await db.EmailOutbox.AsNoTracking().SingleAsync(m => m.Id == id, cancellationToken);

        string? providerMessageId = null;
        string? error = null;
        try
        {
            var email = await ComposeAsync(message, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(sendTimeout);
            providerMessageId = await sender.SendAsync(email, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping: nothing is recorded, the lease expires and another run tries again.
            throw;
        }
        catch (Exception exception)
        {
            error = Describe(exception, message.Recipient);
        }

        // Recorded even when the host starts stopping now: after a successful send this keeps the "sent twice" window minimal.
        return await CompleteAsync(message, attempt, providerMessageId, error, CancellationToken.None);
    }

    private async Task<EmailMessage> ComposeAsync(EmailOutboxMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            throw new InvalidOperationException("Email:FromAddress is not configured.");
        }

        var payload = OrderEmailPayload.FromJson(message.Payload);
        var code = message.EncryptedToken is null ? null : await protector.UnprotectAsync(message.EncryptedToken, message.Id, cancellationToken);
        var (branding, store) = await BrandingAsync(cancellationToken);
        var rendered = OrderEmailRenderer.Render(message.Template, payload, code, branding);

        var fromName = string.IsNullOrWhiteSpace(settings.FromName) ? store.StoreName : settings.FromName.Trim();
        var replyTo = !string.IsNullOrWhiteSpace(settings.ReplyToAddress) ? settings.ReplyToAddress.Trim()
            : string.IsNullOrWhiteSpace(store.ContactEmail) ? null : store.ContactEmail;

        return new EmailMessage(message.Recipient, settings.FromAddress.Trim(), fromName, replyTo, rendered.Subject, rendered.Html, rendered.Text);
    }

    private async Task<(EmailBranding Branding, StoreSettingsRecord Store)> BrandingAsync(CancellationToken cancellationToken)
    {
        if (_branding is null || _settings is null)
        {
            _settings = await db.StoreSettings.AsNoTracking().SingleAsync(s => s.Id == StoreSettingsRecord.SingletonId, cancellationToken);
            _branding = new EmailBranding(
                _settings.StoreName,
                _settings.ContactEmail,
                _settings.Phone,
                _settings.BusinessAddress,
                string.IsNullOrWhiteSpace(options.Value.LogoUrl) ? null : options.Value.LogoUrl.Trim(),
                storefront.Value.IsConfigured ? storefront.Value.BaseUrl!.TrimEnd('/') : null);
        }

        return (_branding, _settings);
    }

    private async Task<EmailOutboxStatus> CompleteAsync(
        EmailOutboxMessage message, int attempt, string? providerMessageId, string? error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var succeeded = error is null;
        var delay = succeeded ? null : EmailRetryPolicy.DelayAfter(attempt);
        var outcome = succeeded ? EmailOutboxStatus.Sent : delay is null ? EmailOutboxStatus.Dead : EmailOutboxStatus.Pending;

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var previous = await db.EmailLogs.Where(l => l.OutboxId == message.Id).MaxAsync(l => (int?)l.AttemptNumber, cancellationToken) ?? 0;
        db.EmailLogs.Add(EmailLog.Create(message.Id, previous + 1, succeeded, providerMessageId, error, now));
        await db.SaveChangesAsync(cancellationToken);

        var mine = db.EmailOutbox.Where(m => m.Id == message.Id && m.Status == EmailOutboxStatus.Processing && m.AttemptCount == attempt);
        var updated = outcome switch
        {
            EmailOutboxStatus.Sent => await mine.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Sent)
                .SetProperty(m => m.SentAt, now)
                .SetProperty(m => m.EncryptedToken, (byte[]?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, (string?)null), cancellationToken),
            EmailOutboxStatus.Dead => await mine.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Dead)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, Truncate(error)), cancellationToken),
            _ => await mine.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Pending)
                .SetProperty(m => m.NextAttemptAt, now + delay!.Value)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, Truncate(error)), cancellationToken),
        };

        await transaction.CommitAsync(cancellationToken);

        if (updated == 0)
        {
            logger.LogWarning("Email {OutboxId}: the lease was taken over before attempt {Attempt} was recorded.", message.Id, attempt);
        }
        else if (!succeeded)
        {
            logger.LogWarning("Email {OutboxId} ({Template}) attempt {Attempt} failed; now {Status}.", message.Id, message.Template, attempt, outcome);
        }

        return outcome;
    }

    /// <summary>Exception type and message, without the recipient's address (SMTP servers like to echo it).</summary>
    internal static string Describe(Exception exception, string recipient)
    {
        var text = exception switch
        {
            MailKit.Net.Smtp.SmtpCommandException smtp => $"{exception.GetType().Name} (SMTP {(int)smtp.StatusCode}): {exception.Message}",
            Amazon.Runtime.AmazonServiceException aws => $"{exception.GetType().Name} ({aws.ErrorCode}, HTTP {(int)aws.StatusCode}): {exception.Message}",
            _ => $"{exception.GetType().Name}: {exception.Message}",
        };
        if (!string.IsNullOrEmpty(recipient))
        {
            text = text.Replace(recipient, "[primalac]", StringComparison.OrdinalIgnoreCase);
        }

        return Truncate(text)!;
    }

    private static string? Truncate(string? value) =>
        value is { Length: > EmailOutboxMessage.MaxErrorLength } ? value[..EmailOutboxMessage.MaxErrorLength] : value;
}
