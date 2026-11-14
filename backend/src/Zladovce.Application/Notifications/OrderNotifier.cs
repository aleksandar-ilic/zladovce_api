using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Domain.Customers;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;
using Zladovce.Domain.Tracking;

namespace Zladovce.Application.Notifications;

public sealed class EmailOutbox(IZladovceDbContext db, ITrackingCodeProtector protector, TimeProvider timeProvider) : IEmailOutbox
{
    public async Task<EmailOutboxMessage> EnqueueAsync(OutboxEmail email, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var encrypted = email.TrackingCode is null ? null : await protector.ProtectAsync(email.TrackingCode, id, cancellationToken);
        var message = EmailOutboxMessage.Create(id, email.DedupKey, email.Template, email.OrderId, email.Recipient, email.PayloadJson, encrypted, now);
        db.EmailOutbox.Add(message);
        return message;
    }
}

/// <summary>
/// The order emails of P1.7 and the tracking codes they carry. Every method only adds rows to the shared unit of work
/// (token, outbox row); the caller saves and commits them in its own transaction.
/// </summary>
public interface IOrderNotifier
{
    /// <summary>
    /// Checkout: issues the order's first tracking code and queues "Porudžbina primljena". Returns the plain code (canonical)
    /// for the create-order response; it is not stored anywhere in plain form.
    /// </summary>
    Task<string> OrderPlacedAsync(Order order, Address address, CancellationToken cancellationToken);

    /// <summary>
    /// A status change: queues its email (with a freshly issued code) when P1.7 defines one; nothing for DeliveryFailed.
    /// <paramref name="shipment"/> is the shipment created by a change to Shipped.
    /// </summary>
    Task<EmailOutboxMessage?> StatusChangedAsync(Order order, OrderStatusHistoryEntry history, Shipment? shipment, CancellationToken cancellationToken);

    /// <summary>Queues the <paramref name="sequence"/>-th resent confirmation with a new code.</summary>
    Task<EmailOutboxMessage> ResendConfirmationAsync(Order order, Address address, int sequence, CancellationToken cancellationToken);

    /// <summary>Issues a new code for an order (token row added) and returns the plain code.</summary>
    string IssueCode(Guid orderId);
}

public sealed class OrderNotifier(
    IZladovceDbContext db,
    IEmailOutbox outbox,
    IOptions<StorefrontOptions> storefront,
    TimeProvider timeProvider) : IOrderNotifier
{
    public async Task<string> OrderPlacedAsync(Order order, Address address, CancellationToken cancellationToken)
    {
        var code = IssueCode(order.Id);
        var payload = Payload(order, OrderStatus.Received, order.PlacedAt, customerMessage: null, Summary(order, address), shipment: null, isResent: false);
        await outbox.EnqueueAsync(
            new OutboxEmail(EmailDedupKeys.OrderReceived(order.Id), EmailTemplate.OrderReceived, order.Id, order.ContactEmail, payload.ToJson(), code),
            cancellationToken);
        return code;
    }

    public async Task<EmailOutboxMessage?> StatusChangedAsync(
        Order order, OrderStatusHistoryEntry history, Shipment? shipment, CancellationToken cancellationToken)
    {
        if (OrderEmailRules.TemplateFor(history.NewStatus) is not { } template)
        {
            return null;
        }

        OrderEmailShipment? shipmentData = null;
        if (template == EmailTemplate.OrderShipped && shipment is not null)
        {
            var courier = await db.CourierCompanies.AsNoTracking()
                .Where(c => c.Id == shipment.CourierCompanyId)
                .Select(c => new { c.Name, c.TrackingUrlTemplate })
                .SingleAsync(cancellationToken);
            shipmentData = new OrderEmailShipment(
                courier.Name, shipment.TrackingNumber, CourierCompany.TrackingUrl(courier.TrackingUrlTemplate, shipment.TrackingNumber), shipment.ShippedAt);
        }

        var code = IssueCode(order.Id);
        var payload = Payload(order, history.NewStatus, history.CreatedAt, history.CustomerMessage, summary: null, shipmentData, isResent: false);
        return await outbox.EnqueueAsync(
            new OutboxEmail(EmailDedupKeys.StatusChange(order.Id, history.Id), template, order.Id, order.ContactEmail, payload.ToJson(), code),
            cancellationToken);
    }

    public async Task<EmailOutboxMessage> ResendConfirmationAsync(Order order, Address address, int sequence, CancellationToken cancellationToken)
    {
        var code = IssueCode(order.Id);
        var payload = Payload(order, order.Status, timeProvider.GetUtcNow(), customerMessage: null, Summary(order, address), shipment: null, isResent: true);
        return await outbox.EnqueueAsync(
            new OutboxEmail(
                EmailDedupKeys.ResentConfirmation(order.Id, sequence), EmailTemplate.OrderReceived, order.Id, order.ContactEmail, payload.ToJson(), code),
            cancellationToken);
    }

    public string IssueCode(Guid orderId)
    {
        var (token, code) = OrderTrackingToken.Issue(orderId, timeProvider.GetUtcNow());
        db.OrderTrackingTokens.Add(token);
        return code;
    }

    private sealed record OrderSummary(IReadOnlyList<OrderEmailLine> Lines, OrderEmailAddress Address);

    private static OrderSummary Summary(Order order, Address address) => new(
        [.. order.Items
            .OrderBy(i => i.ProductName, StringComparer.Ordinal).ThenBy(i => i.VariantName, StringComparer.Ordinal).ThenBy(i => i.Sku, StringComparer.Ordinal)
            .Select(i => new OrderEmailLine(i.ProductName, i.VariantName, i.Quantity, i.UnitPrice, i.LineTotal))],
        new OrderEmailAddress(address.City, address.Street, address.PostalCode));

    private OrderEmailPayload Payload(
        Order order, OrderStatus status, DateTimeOffset eventAt, string? customerMessage, OrderSummary? summary, OrderEmailShipment? shipment, bool isResent) =>
        new(
            OrderEmailPayload.CurrentVersion,
            order.OrderNumber,
            order.ContactFirstName,
            order.PlacedAt,
            status,
            eventAt,
            summary?.Lines,
            summary is null ? null : order.Subtotal,
            summary is null ? null : order.ShippingPrice,
            summary is null ? null : order.Total,
            order.PaymentMethod,
            summary?.Address,
            shipment,
            customerMessage,
            storefront.Value.TrackingPageUrl,
            isResent);
}
