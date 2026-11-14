using System.Text.Json;
using System.Text.Json.Serialization;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;

namespace Zladovce.Application.Notifications;

/// <summary>One ordered product in the confirmation email.</summary>
public sealed record OrderEmailLine(string ProductName, string VariantName, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record OrderEmailAddress(string City, string Street, string PostalCode);

/// <summary>Courier data of the Shipped email.</summary>
/// <param name="TrackingUrl">The courier's page for the parcel (from the courier's verified URL template), when known.</param>
public sealed record OrderEmailShipment(string CourierName, string? TrackingNumber, string? TrackingUrl, DateTimeOffset ShippedAt);

/// <summary>
/// The <c>payload</c> of an order email: exactly what the templates need and nothing secret. The tracking code is not in it;
/// it travels encrypted in <c>email_outbox.encrypted_token</c>. Lines, totals and the address are only filled for
/// <see cref="EmailTemplate.OrderReceived"/>; the shipment only for <see cref="EmailTemplate.OrderShipped"/>.
/// </summary>
/// <param name="Status">The status the order reached (Rejected and Cancelled share a template and differ by this).</param>
/// <param name="EventAt">When the status was reached (or the confirmation was resent).</param>
/// <param name="TrackingPageUrl"><c>{Storefront:BaseUrl}/track-order</c>; the link is <c>{TrackingPageUrl}#kod={code}</c>.</param>
/// <param name="IsResent">True for a confirmation resent by an administrator.</param>
public sealed record OrderEmailPayload(
    int Version,
    string OrderNumber,
    string FirstName,
    DateTimeOffset PlacedAt,
    OrderStatus Status,
    DateTimeOffset EventAt,
    IReadOnlyList<OrderEmailLine>? Lines,
    decimal? Subtotal,
    decimal? ShippingPrice,
    decimal? Total,
    PaymentMethod PaymentMethod,
    OrderEmailAddress? ShippingAddress,
    OrderEmailShipment? Shipment,
    string? CustomerMessage,
    string TrackingPageUrl,
    bool IsResent)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OrderEmailPayload FromJson(string json) =>
        JsonSerializer.Deserialize<OrderEmailPayload>(json, JsonOptions) ?? throw new InvalidOperationException("Empty email payload.");
}

/// <summary>
/// Encrypts the plain tracking code for its trip through the outbox (AES-GCM, 256-bit key from configuration or Secrets
/// Manager). The ciphertext is bound to the outbox row id, so it cannot be moved to another row.
/// </summary>
public interface ITrackingCodeProtector
{
    Task<byte[]> ProtectAsync(string code, Guid outboxId, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the data was changed or the key is wrong.</summary>
    Task<string> UnprotectAsync(byte[] protectedCode, Guid outboxId, CancellationToken cancellationToken);
}

/// <summary>An email to put in the outbox.</summary>
/// <param name="TrackingCode">Plain code (canonical) to encrypt into the row; null for emails without a tracking link.</param>
public sealed record OutboxEmail(string DedupKey, EmailTemplate Template, Guid? OrderId, string Recipient, string PayloadJson, string? TrackingCode);

/// <summary>
/// The transactional outbox writer. Adds the row to the shared unit of work; the caller's <c>SaveChanges</c>/commit stores
/// it together with the business change, and a rollback drops it.
/// </summary>
public interface IEmailOutbox
{
    Task<EmailOutboxMessage> EnqueueAsync(OutboxEmail email, CancellationToken cancellationToken);
}

/// <summary>A rendered email.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>A message for <see cref="IEmailSender"/>.</summary>
public sealed record EmailMessage(string To, string FromAddress, string FromName, string? ReplyTo, string Subject, string Html, string Text);

/// <summary>Sends one email. Throws on any failure (the dispatcher records it and retries).</summary>
public interface IEmailSender
{
    /// <returns>The provider's message id, when it reports one.</returns>
    Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Result of one dispatcher run.</summary>
/// <param name="Claimed">Rows claimed (= attempts made).</param>
/// <param name="Expired">Rows whose lease ran out on their last attempt and were marked Dead without another send.</param>
/// <param name="PurgedRateLimitBuckets">Expired rate-limit windows deleted.</param>
public sealed record EmailDispatchResult(int Claimed, int Sent, int Retrying, int Dead, int Expired, int PurgedRateLimitBuckets);

/// <summary>
/// Sends due outbox rows: claims them one at a time (<c>FOR UPDATE SKIP LOCKED</c> and a lease), renders, sends, logs the
/// attempt and marks the row Sent (erasing the encrypted code) or schedules the next attempt / Dead. Also deletes expired
/// rate-limit windows.
/// </summary>
public interface IEmailDispatcher
{
    /// <param name="maxMessages">Most rows to claim in this run.</param>
    /// <param name="timeBudget">No new row is claimed once less than one send timeout of this budget is left.</param>
    Task<EmailDispatchResult> DispatchAsync(int maxMessages, TimeSpan timeBudget, CancellationToken cancellationToken);
}
