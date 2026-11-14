using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;

namespace Zladovce.Application.Orders;

public enum AdminOrderSort
{
    /// <summary>Placed, newest first (default).</summary>
    Newest,
    Oldest,
    TotalDescending,
    TotalAscending,
}

/// <summary>Filter of the admin order list.</summary>
/// <param name="Search">Order number, customer name, email or phone; ignores case and Serbian diacritics.</param>
/// <param name="Statuses">Orders in any of these statuses; empty = all.</param>
/// <param name="From">First day (Serbian local date), inclusive.</param>
/// <param name="To">Last day (Serbian local date), inclusive.</param>
public sealed record AdminOrderListCriteria(
    string? Search, IReadOnlySet<OrderStatus> Statuses, DateOnly? From, DateOnly? To, AdminOrderSort Sort, PageRequest Page);

/// <summary>One row of the admin order list.</summary>
/// <param name="CustomerName">First and last name from the order's contact snapshot.</param>
/// <param name="City">City of the delivery address.</param>
/// <param name="ItemCount">Number of lines (different variants).</param>
/// <param name="TotalQuantity">Number of pieces.</param>
/// <param name="IsPaymentCollected">True when an administrator recorded the money received from the courier.</param>
public sealed record AdminOrderListItem(
    Guid Id,
    string OrderNumber,
    DateTimeOffset PlacedAt,
    string CustomerName,
    string City,
    int ItemCount,
    int TotalQuantity,
    decimal Total,
    OrderStatus Status,
    bool IsPaymentCollected);

/// <summary>Number of orders in one status.</summary>
public sealed record OrderStatusCount(OrderStatus Status, int Count);

/// <summary>Response of <c>GET /api/v1/admin/orders/summary</c>.</summary>
/// <param name="Counts">Every status in state order (Received, Accepted, Preparing, Shipped, Delivered, Cancelled, Rejected, DeliveryFailed), zero included.</param>
/// <param name="Total">All orders.</param>
public sealed record OrderSummaryResponse(IReadOnlyList<OrderStatusCount> Counts, int Total);

/// <summary>Buyer contact as entered at checkout (snapshot in the order).</summary>
/// <param name="CustomerId">The customer record (one per email address).</param>
/// <param name="Phone">E.164, e.g. <c>+381641234567</c>.</param>
public sealed record AdminOrderContact(Guid CustomerId, string FirstName, string LastName, string Email, string Phone);

public sealed record AdminOrderAddress(string City, string Street, string PostalCode);

/// <summary>Item snapshot.</summary>
public sealed record AdminOrderItem(Guid Id, Guid VariantId, string ProductName, string VariantName, string Sku, decimal UnitPrice, int Quantity, decimal LineTotal);

/// <summary>Money received from the courier (P1.8 "Stvarno naplaćeno").</summary>
/// <param name="CanRecord">True when the order is Delivered: only then can the payment be recorded or cleared.</param>
public sealed record AdminOrderPayment(decimal? CollectedAmount, DateTimeOffset? CollectedAt, bool IsCollected, bool CanRecord);

/// <summary>One status change.</summary>
/// <param name="PreviousStatus">Null for the first entry (the order was placed).</param>
/// <param name="ChangedByAdminName">Null when the system did it (checkout).</param>
public sealed record AdminOrderTimelineEntry(
    Guid Id,
    OrderStatus? PreviousStatus,
    OrderStatus NewStatus,
    Guid? ChangedByAdminId,
    string? ChangedByAdminName,
    string? InternalNote,
    string? CustomerMessage,
    DateTimeOffset CreatedAt);

/// <summary>An internal note added without a status change.</summary>
public sealed record AdminOrderNote(Guid Id, string Note, Guid AdminUserId, string AdminName, DateTimeOffset CreatedAt);

public sealed record AdminCourierSummary(Guid Id, string Name, bool IsActive);

/// <summary>A shipping attempt.</summary>
/// <param name="TrackingUrl">The courier's tracking page from its URL template; null without template or tracking number.</param>
public sealed record AdminOrderShipment(
    Guid Id,
    AdminCourierSummary Courier,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTimeOffset ShippedAt,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A status the order can move to now, with what that step needs.</summary>
/// <param name="RequiresShipment">The status change must contain <c>shipment</c> (courier required; tracking number, date, note optional).</param>
/// <param name="StockReturn">
/// <c>None</c>; <c>Automatic</c> (stock goes back by itself: Rejected, Cancelled before shipping); <c>AdminChoice</c>
/// (Cancelled after a failed delivery: <c>restock</c> true/false must be sent).
/// </param>
/// <param name="RequiresRestockChoice">Same as <c>stockReturn == AdminChoice</c>.</param>
public sealed record AllowedStatusTransition(OrderStatus Status, bool RequiresShipment, StockReturnPolicy StockReturn, bool RequiresRestockChoice)
{
    public static AllowedStatusTransition From(OrderTransition transition) =>
        new(transition.To, transition.RequiresShipment, transition.StockReturn, transition.StockReturn == StockReturnPolicy.AdminChoice);
}

/// <summary>Response of <c>GET /api/v1/admin/orders/{id}</c> and of every order action.</summary>
/// <param name="Version">Send back with status and payment changes; a stale value gives 409 <c>CONCURRENT_EDIT</c>.</param>
/// <param name="Items">Sorted by product name, variant name and SKU.</param>
/// <param name="Timeline">Status history, oldest first.</param>
/// <param name="Notes">Internal notes, oldest first.</param>
/// <param name="Shipments">Shipping attempts, oldest first.</param>
/// <param name="AllowedNextStatuses">Empty for a final status.</param>
/// <param name="Emails">The order's emails (state only, no bodies), oldest first.</param>
public sealed record AdminOrderDetail(
    Guid Id,
    string OrderNumber,
    OrderStatus Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    AdminOrderContact Customer,
    AdminOrderAddress ShippingAddress,
    string? DeliveryNote,
    PaymentMethod PaymentMethod,
    IReadOnlyList<AdminOrderItem> Items,
    decimal Subtotal,
    decimal ShippingPrice,
    decimal Total,
    string Currency,
    AdminOrderPayment Payment,
    IReadOnlyList<AdminOrderTimelineEntry> Timeline,
    IReadOnlyList<AdminOrderNote> Notes,
    IReadOnlyList<AdminOrderShipment> Shipments,
    IReadOnlyList<AllowedStatusTransition> AllowedNextStatuses,
    IReadOnlyList<Notifications.AdminEmailItem> Emails);

/// <summary>Shipment entered when an order moves to Shipped.</summary>
public sealed record NewShipmentRequest
{
    /// <summary>An active courier company.</summary>
    public required Guid? CourierCompanyId { get; init; }

    /// <summary>The courier's number, up to 100 characters; may be added later.</summary>
    public string? TrackingNumber { get; init; }

    /// <summary>When the parcel was handed over; default now. Not in the future, not before the order was placed.</summary>
    public DateTimeOffset? ShippedAt { get; init; }

    /// <summary>Up to 1000 characters.</summary>
    public string? Note { get; init; }
}

/// <summary>Body of <c>POST /api/v1/admin/orders/{id}/status</c>.</summary>
public sealed record ChangeOrderStatusRequest
{
    public required OrderStatus? NewStatus { get; init; }

    /// <summary>The order's <c>version</c> as last read.</summary>
    public required uint? Version { get; init; }

    /// <summary>Never shown to the customer. Up to 2000 characters.</summary>
    public string? InternalNote { get; init; }

    /// <summary>Shown on the tracking page and in the status email. Up to 2000 characters.</summary>
    public string? CustomerMessage { get; init; }

    /// <summary>Required when <c>newStatus</c> is Shipped, must be absent or null otherwise.</summary>
    public NewShipmentRequest? Shipment { get; init; }

    /// <summary>
    /// Required (true/false) when cancelling an order in DeliveryFailed: true puts the returned goods back on stock.
    /// Must be absent or null for every other transition.
    /// </summary>
    public bool? Restock { get; init; }
}

public sealed class NewShipmentRequestValidator : AbstractValidator<NewShipmentRequest>
{
    public NewShipmentRequestValidator()
    {
        RuleFor(s => s.CourierCompanyId).NotNull().Must(id => id != Guid.Empty).WithMessage("Izaberite kurirsku službu.");
        RuleFor(s => s.TrackingNumber).MaximumLength(Shipment.MaxTrackingNumberLength)
            .WithMessage($"Broj pošiljke može imati najviše {Shipment.MaxTrackingNumberLength} znakova.");
        RuleFor(s => s.Note).MaximumLength(Shipment.MaxNoteLength)
            .WithMessage($"Napomena može imati najviše {Shipment.MaxNoteLength} znakova.");
    }
}

public sealed class ChangeOrderStatusRequestValidator : AbstractValidator<ChangeOrderStatusRequest>
{
    public ChangeOrderStatusRequestValidator()
    {
        RuleFor(r => r.NewStatus).NotNull().WithMessage("Izaberite novi status.").IsInEnum();
        RuleFor(r => r.Version).NotNull().WithMessage("Verzija porudžbine je obavezna.");
        RuleFor(r => r.InternalNote).MaximumLength(OrderStatusHistoryEntry.MaxTextLength)
            .WithMessage($"Interna napomena može imati najviše {OrderStatusHistoryEntry.MaxTextLength} znakova.");
        RuleFor(r => r.CustomerMessage).MaximumLength(OrderStatusHistoryEntry.MaxTextLength)
            .WithMessage($"Poruka kupcu može imati najviše {OrderStatusHistoryEntry.MaxTextLength} znakova.");
        RuleFor(r => r.Shipment).SetValidator(new NewShipmentRequestValidator()!);
    }
}

/// <summary>Body of <c>PUT /api/v1/admin/orders/{id}/shipments/{shipmentId}</c>: corrects a shipment. Every member must be sent.</summary>
public sealed record UpdateShipmentRequest
{
    /// <summary>An active courier, or the shipment's current one (also when it was deactivated meanwhile).</summary>
    public required Guid? CourierCompanyId { get; init; }

    public required string? TrackingNumber { get; init; }

    /// <summary>Not in the future, not before the order was placed.</summary>
    public required DateTimeOffset? ShippedAt { get; init; }

    public required string? Note { get; init; }
}

public sealed class UpdateShipmentRequestValidator : AbstractValidator<UpdateShipmentRequest>
{
    public UpdateShipmentRequestValidator()
    {
        RuleFor(s => s.CourierCompanyId).NotNull().Must(id => id != Guid.Empty).WithMessage("Izaberite kurirsku službu.");
        RuleFor(s => s.TrackingNumber).MaximumLength(Shipment.MaxTrackingNumberLength)
            .WithMessage($"Broj pošiljke može imati najviše {Shipment.MaxTrackingNumberLength} znakova.");
        RuleFor(s => s.ShippedAt).NotNull().WithMessage("Datum slanja je obavezan.");
        RuleFor(s => s.Note).MaximumLength(Shipment.MaxNoteLength)
            .WithMessage($"Napomena može imati najviše {Shipment.MaxNoteLength} znakova.");
    }
}

/// <summary>Body of <c>POST /api/v1/admin/orders/{id}/notes</c>.</summary>
public sealed record AddOrderNoteRequest
{
    /// <summary>1–2000 characters. Internal, never shown to the customer.</summary>
    public required string? Note { get; init; }
}

public sealed class AddOrderNoteRequestValidator : AbstractValidator<AddOrderNoteRequest>
{
    public AddOrderNoteRequestValidator()
    {
        RuleFor(r => r.Note)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Napomena je obavezna.")
            .Must(n => n!.Trim().Length <= OrderNote.MaxLength).WithMessage($"Napomena može imati najviše {OrderNote.MaxLength} znakova.");
    }
}

/// <summary>
/// Body of <c>PUT /api/v1/admin/orders/{id}/payment</c>: records the money received from the courier, or clears it
/// (<c>collectedAmount</c> null). Only for Delivered orders. Every member must be sent.
/// </summary>
public sealed record SetOrderPaymentRequest
{
    /// <summary>Amount received (RSD, more than 0, two decimals; may differ from the total). Null clears the record.</summary>
    public required decimal? CollectedAmount { get; init; }

    /// <summary>When it was received; null with an amount means now. Must be null when clearing. Not in the future, not before the order was placed.</summary>
    public required DateTimeOffset? CollectedAt { get; init; }

    /// <summary>The order's <c>version</c> as last read.</summary>
    public required uint? Version { get; init; }
}

public sealed class SetOrderPaymentRequestValidator : AbstractValidator<SetOrderPaymentRequest>
{
    public SetOrderPaymentRequestValidator()
    {
        RuleFor(r => r.CollectedAmount)
            .GreaterThan(0).WithMessage("Iznos mora biti veći od nule.")
            .LessThanOrEqualTo(Pricing.MaxPrice)
            .Must(a => a is null || Pricing.HasAtMostTwoDecimals(a.Value)).WithMessage("Iznos može imati najviše dve decimale.");
        RuleFor(r => r.CollectedAt)
            .Null().WithMessage("Datum naplate se ne šalje kada se naplata briše.").When(r => r.CollectedAmount is null);
        RuleFor(r => r.Version).NotNull().WithMessage("Verzija porudžbine je obavezna.");
    }
}

/// <summary>Raised for a status change that the state machine does not allow; carries the statuses that are allowed.</summary>
public sealed class InvalidStatusTransitionException(OrderStatus current, OrderStatus requested, IReadOnlyList<OrderStatus> allowed)
    : Common.Errors.AppException(
        Common.Errors.ErrorCodes.InvalidStatusTransition,
        Common.Errors.ErrorTexts.InvalidStatusTransitionTitle,
        allowed.Count == 0
            ? $"Porudžbina je u završnom statusu „{OrderStatusLabels.Of(current)}” i više se ne može menjati."
            : $"Porudžbina ne može da pređe iz statusa „{OrderStatusLabels.Of(current)}” u „{OrderStatusLabels.Of(requested)}”.")
{
    public OrderStatus CurrentStatus { get; } = current;

    public IReadOnlyList<OrderStatus> AllowedNextStatuses { get; } = allowed;
}
