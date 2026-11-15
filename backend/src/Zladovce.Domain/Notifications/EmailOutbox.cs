using Zladovce.Domain.Common;
using Zladovce.Domain.Orders;

namespace Zladovce.Domain.Notifications;

/// <summary>Email templates (P1.7). Stored as text in <c>email_outbox.template</c>.</summary>
public enum EmailTemplate
{
    /// <summary>Porudžbina primljena – full summary, tracking code and link. Also "resend confirmation".</summary>
    OrderReceived,

    /// <summary>Porudžbina prihvaćena.</summary>
    OrderAccepted,

    /// <summary>Porudžbina se priprema.</summary>
    OrderPreparing,

    /// <summary>Porudžbina poslata – courier and courier tracking number when entered.</summary>
    OrderShipped,

    /// <summary>Porudžbina isporučena.</summary>
    OrderDelivered,

    /// <summary>Porudžbina otkazana – also used for Rejected, with the rejection message.</summary>
    OrderCancelled,

    /// <summary>Newsletter double opt-in: confirmation link and unsubscribe link (Phase 7).</summary>
    NewsletterConfirm,

    /// <summary>A contact-form message, sent to the store contact address with Reply-To = the sender (Phase 7).</summary>
    ContactMessage,
}

/// <summary>State of an outbox row.</summary>
public enum EmailOutboxStatus
{
    /// <summary>Waiting for its next attempt (<c>next_attempt_at</c>).</summary>
    Pending,

    /// <summary>Claimed by a worker until <c>locked_until</c>.</summary>
    Processing,

    /// <summary>Accepted by the mail provider. Final.</summary>
    Sent,

    /// <summary>Every attempt failed. Final until an administrator retries it.</summary>
    Dead,
}

/// <summary>Which status change sends which email (P1.7).</summary>
public static class OrderEmailRules
{
    /// <summary>The template for an order that has just reached <paramref name="status"/>; null = no email (DeliveryFailed).</summary>
    public static EmailTemplate? TemplateFor(OrderStatus status) => status switch
    {
        OrderStatus.Received => EmailTemplate.OrderReceived,
        OrderStatus.Accepted => EmailTemplate.OrderAccepted,
        OrderStatus.Preparing => EmailTemplate.OrderPreparing,
        OrderStatus.Shipped => EmailTemplate.OrderShipped,
        OrderStatus.Delivered => EmailTemplate.OrderDelivered,
        OrderStatus.Cancelled or OrderStatus.Rejected => EmailTemplate.OrderCancelled,
        OrderStatus.DeliveryFailed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>
/// The unique <c>dedup_key</c> of an outbox row: one row per business event, so a repeated write of the same event fails
/// on the unique index instead of sending a second email.
/// </summary>
public static class EmailDedupKeys
{
    /// <summary>The confirmation written by checkout.</summary>
    public static string OrderReceived(Guid orderId) => $"order:{orderId:D}:received";

    /// <summary>The email of one status change (one history row).</summary>
    public static string StatusChange(Guid orderId, Guid historyId) => $"order:{orderId:D}:history:{historyId:D}";

    /// <summary>The <paramref name="sequence"/>-th confirmation resent by an administrator (1, 2, …).</summary>
    public static string ResentConfirmation(Guid orderId, int sequence) =>
        sequence >= 1 ? $"order:{orderId:D}:resend:{sequence}" : throw new ArgumentOutOfRangeException(nameof(sequence));

    /// <summary>One newsletter confirmation request (the subscriber's <c>confirmation_sent_at</c> in Unix milliseconds).</summary>
    public static string NewsletterConfirm(Guid subscriberId, DateTimeOffset confirmationSentAt) =>
        $"newsletter:{subscriberId:D}:confirm:{confirmationSentAt.ToUnixTimeMilliseconds()}";

    /// <summary>The email of one contact-form message.</summary>
    public static string ContactMessage(Guid messageId) => $"contact:{messageId:D}";
}

/// <summary>
/// Retry schedule of the outbox (A4 "Email outbox"): after a failed attempt n the next one is due after
/// 1 min, 5 min, 30 min, 2 h, 12 h; when attempt 6 fails the row is Dead.
/// </summary>
public static class EmailRetryPolicy
{
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(12),
    ];

    /// <summary>The first attempt plus one per delay.</summary>
    public static int MaxAttempts => Delays.Count + 1;

    /// <summary>Delay before the next attempt after attempt <paramref name="attempt"/> (1-based) failed; null = no more attempts (Dead).</summary>
    public static TimeSpan? DelayAfter(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return attempt <= Delays.Count ? Delays[attempt - 1] : null;
    }
}

/// <summary>
/// A queued email (table <c>email_outbox</c>), written in the same transaction as the business change that causes it.
/// The worker claims due rows, sends them and records every attempt in <see cref="EmailLog"/>.
/// </summary>
public sealed class EmailOutboxMessage
{
    public const int MaxErrorLength = 1000;

    private EmailOutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique per business event, see <see cref="EmailDedupKeys"/>.</summary>
    public string DedupKey { get; private set; } = null!;
    public EmailTemplate Template { get; private set; }
    public Guid? OrderId { get; private set; }
    public string Recipient { get; private set; } = null!;

    /// <summary>JSON (jsonb): only what the template needs, no secrets.</summary>
    public string Payload { get; private set; } = null!;

    /// <summary>The plain tracking code, AES-GCM encrypted (bound to this row's id). Null after a successful send.</summary>
    public byte[]? EncryptedToken { get; private set; }
    public EmailOutboxStatus Status { get; private set; }

    /// <summary>Attempts since the row was created or last retried by an administrator.</summary>
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Lease of the worker that claimed it.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>A new Pending row due at once. <paramref name="id"/> is chosen by the caller because the token encryption is bound to it.</summary>
    public static EmailOutboxMessage Create(
        Guid id, string dedupKey, EmailTemplate template, Guid? orderId, string recipient, string payload, byte[]? encryptedToken, DateTimeOffset now)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(dedupKey) || string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(payload))
        {
            throw new DomainException("An outbox email needs an id, a dedup key, a recipient and a payload.");
        }

        return new EmailOutboxMessage
        {
            Id = id,
            DedupKey = dedupKey,
            Template = template,
            OrderId = orderId,
            Recipient = recipient.Trim(),
            Payload = payload,
            EncryptedToken = encryptedToken,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// An administrator sends a Dead row again: Pending, due now, attempts start over. <paramref name="encryptedToken"/>
    /// replaces the token when a new one was issued (the old one was erased or never existed).
    /// </summary>
    public void Retry(DateTimeOffset now, byte[]? encryptedToken)
    {
        if (Status != EmailOutboxStatus.Dead)
        {
            throw new DomainException("Only a dead email can be retried.");
        }

        Status = EmailOutboxStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        LockedUntil = null;
        if (encryptedToken is not null)
        {
            EncryptedToken = encryptedToken;
        }
    }
}

/// <summary>One send attempt of an outbox row (table <c>email_logs</c>).</summary>
public sealed class EmailLog
{
    private EmailLog()
    {
    }

    public Guid Id { get; private set; }
    public Guid OutboxId { get; private set; }

    /// <summary>1, 2, … over the whole life of the row (also across administrator retries).</summary>
    public int AttemptNumber { get; private set; }
    public bool Succeeded { get; private set; }

    /// <summary>The provider's message id (SES message id or the SMTP server's answer).</summary>
    public string? SesMessageId { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static EmailLog Create(Guid outboxId, int attemptNumber, bool succeeded, string? providerMessageId, string? error, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OutboxId = outboxId,
        AttemptNumber = attemptNumber,
        Succeeded = succeeded,
        SesMessageId = providerMessageId is { Length: > 500 } ? providerMessageId[..500] : providerMessageId,
        Error = error is { Length: > EmailOutboxMessage.MaxErrorLength } ? error[..EmailOutboxMessage.MaxErrorLength] : error,
        CreatedAt = now,
    };
}
