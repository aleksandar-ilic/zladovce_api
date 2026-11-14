using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;
using Zladovce.Domain.Tracking;

namespace Zladovce.Application.Tracking;

/// <summary>Body of <c>POST /api/v1/tracking/lookup</c>.</summary>
public sealed record TrackingLookupRequest
{
    /// <summary>
    /// The tracking code from the confirmation email or the order page: 26 characters, hyphens and spaces optional, any case.
    /// Any value that is not a valid, current code gives the same 404 <c>TRACKING_NOT_FOUND</c>.
    /// </summary>
    public required string? Code { get; init; }
}

/// <summary>One recorded status change, as the customer sees it.</summary>
/// <param name="Label">Customer wording, e.g. "Porudžbina primljena", "Pošiljka predata kurirskoj službi".</param>
/// <param name="At">When the status was reached.</param>
/// <param name="CustomerMessage">The message the store wrote for the customer with this change, if any.</param>
public sealed record TrackingTimelineEntry(OrderStatus Status, string Label, DateTimeOffset At, string? CustomerMessage);

/// <summary>The latest shipment.</summary>
/// <param name="TrackingNumber">The courier's own number, when entered.</param>
/// <param name="TrackingUrl">The courier's tracking page, when the courier has a verified URL template and a number is entered.</param>
public sealed record TrackingShipment(string CourierName, string? TrackingNumber, string? TrackingUrl, DateTimeOffset ShippedAt);

/// <summary>
/// Response of <c>POST /api/v1/tracking/lookup</c>. Deliberately minimal: no name, address, phone, email, items, prices,
/// internal notes or administrator names.
/// </summary>
/// <param name="StatusLabel">Serbian label of the current status ("Primljena", "Poslata", …).</param>
/// <param name="Timeline">The recorded status history, oldest first.</param>
/// <param name="Shipment">The latest shipment; null before the order was shipped.</param>
public sealed record TrackingLookupResponse(
    string OrderNumber,
    OrderStatus Status,
    string StatusLabel,
    DateTimeOffset PlacedAt,
    IReadOnlyList<TrackingTimelineEntry> Timeline,
    TrackingShipment? Shipment);

/// <summary>Response of <c>POST /api/v1/admin/orders/{id}/tracking/revoke</c>.</summary>
/// <param name="RevokedCount">Codes that were valid and are now revoked.</param>
public sealed record RevokeTrackingResponse(int RevokedCount);

/// <summary>Customer wording of the statuses on the tracking page (§9 example timeline).</summary>
public static class TrackingLabels
{
    public static string Of(OrderStatus status) => status switch
    {
        OrderStatus.Received => "Porudžbina primljena",
        OrderStatus.Accepted => "Porudžbina prihvaćena",
        OrderStatus.Preparing => "Proizvodi se pripremaju",
        OrderStatus.Shipped => "Pošiljka predata kurirskoj službi",
        OrderStatus.Delivered => "Porudžbina isporučena",
        OrderStatus.Cancelled => "Porudžbina otkazana",
        OrderStatus.Rejected => "Porudžbina nije prihvaćena",
        OrderStatus.DeliveryFailed => "Isporuka nije uspela",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public interface ITrackingService
{
    /// <summary>
    /// Finds the order of a tracking code. Every input is hashed and looked up the same way; unknown, revoked and malformed
    /// codes all raise the same <see cref="NotFoundException"/> (<c>TRACKING_NOT_FOUND</c>).
    /// </summary>
    Task<TrackingLookupResponse> LookupAsync(string? code, CancellationToken cancellationToken);

    /// <summary>Revokes every valid code of the order (administrator). Codes issued later work again.</summary>
    Task<RevokeTrackingResponse> RevokeAllAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class TrackingService(IZladovceDbContext db, IAuditLogger audit, TimeProvider timeProvider) : ITrackingService
{
    public const string RevokedAction = "Order.TrackingRevoked";

    public async Task<TrackingLookupResponse> LookupAsync(string? code, CancellationToken cancellationToken)
    {
        var hash = TrackingCode.LookupHash(code);

        var match = await (
                from t in db.OrderTrackingTokens
                where t.TokenHash == hash && t.RevokedAt == null
                join o in db.Orders on t.OrderId equals o.Id
                select new { TokenId = t.Id, o.Id, o.OrderNumber, o.Status, o.PlacedAt })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();

        var now = timeProvider.GetUtcNow();
        await db.OrderTrackingTokens.Where(t => t.Id == match.TokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), cancellationToken);

        var history = await db.OrderStatusHistory.AsNoTracking()
            .Where(h => h.OrderId == match.Id)
            .OrderBy(h => h.CreatedAt).ThenBy(h => h.Id)
            .Select(h => new { h.NewStatus, h.CreatedAt, h.CustomerMessage })
            .ToListAsync(cancellationToken);

        var shipment = await (
                from s in db.Shipments.AsNoTracking()
                where s.OrderId == match.Id
                join c in db.CourierCompanies on s.CourierCompanyId equals c.Id
                orderby s.CreatedAt descending, s.Id descending
                select new { c.Name, c.TrackingUrlTemplate, s.TrackingNumber, s.ShippedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return new TrackingLookupResponse(
            match.OrderNumber,
            match.Status,
            OrderStatusLabels.Of(match.Status),
            match.PlacedAt,
            [.. history.Select(h => new TrackingTimelineEntry(h.NewStatus, TrackingLabels.Of(h.NewStatus), h.CreatedAt, h.CustomerMessage))],
            shipment is null
                ? null
                : new TrackingShipment(
                    shipment.Name, shipment.TrackingNumber, CourierCompany.TrackingUrl(shipment.TrackingUrlTemplate, shipment.TrackingNumber), shipment.ShippedAt));
    }

    public async Task<RevokeTrackingResponse> RevokeAllAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == orderId, cancellationToken))
        {
            throw new NotFoundException();
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revoked = await db.OrderTrackingTokens
            .Where(t => t.OrderId == orderId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        audit.Record(RevokedAction, "Order", orderId.ToString(), new { revokedCount = revoked });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RevokeTrackingResponse(revoked);
    }

    private static NotFoundException NotFound() =>
        new(ErrorTexts.TrackingNotFoundDetail, ErrorCodes.TrackingNotFound, ErrorTexts.TrackingNotFoundTitle);
}
