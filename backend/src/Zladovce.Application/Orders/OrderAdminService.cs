using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Inventory;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Common;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;

namespace Zladovce.Application.Orders;

/// <summary>Order administration: list, detail, status changes (P1.5/P1.6), notes, shipments and collected payments.</summary>
public interface IOrderAdminService
{
    Task<PagedResult<AdminOrderListItem>> ListAsync(AdminOrderListCriteria criteria, CancellationToken cancellationToken);
    Task<OrderSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<AdminOrderDetail> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminOrderDetail> ChangeStatusAsync(Guid id, ChangeOrderStatusRequest request, CancellationToken cancellationToken);
    Task<AdminOrderDetail> AddNoteAsync(Guid id, AddOrderNoteRequest request, CancellationToken cancellationToken);
    Task<AdminOrderDetail> UpdateShipmentAsync(Guid id, Guid shipmentId, UpdateShipmentRequest request, CancellationToken cancellationToken);
    Task<AdminOrderDetail> SetPaymentAsync(Guid id, SetOrderPaymentRequest request, CancellationToken cancellationToken);
}

public sealed class OrderAdminService(
    IZladovceDbContext db,
    IInventoryService inventory,
    IRequestContext requestContext,
    IAuditLogger audit,
    IOrderNotifier notifier,
    IAdminEmailService emails,
    TimeProvider timeProvider) : IOrderAdminService
{
    public const string AuditEntityType = "Order";
    public const string StatusChangedAction = "Order.StatusChanged";
    public const string NoteAddedAction = "Order.NoteAdded";
    public const string PaymentRecordedAction = "Order.PaymentRecorded";
    public const string PaymentClearedAction = "Order.PaymentCleared";

    /// <summary>Tolerance for clocks of administrators' devices when a date "now" is entered.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public async Task<PagedResult<AdminOrderListItem>> ListAsync(AdminOrderListCriteria criteria, CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking();

        if (DbSearch.ContainsPattern(criteria.Search) is { } pattern)
        {
            var phone = PhoneFragment(criteria.Search!);
            orders = orders.Where(o =>
                EF.Functions.Like(o.OrderNumber.ToLower(), pattern.ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(o.ContactFirstName + " " + o.ContactLastName).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(o.ContactLastName + " " + o.ContactFirstName).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(o.ContactEmail.ToLower(), pattern.ToLower())
                || (phone != null && o.ContactPhone.Contains(phone)));
        }

        if (criteria.Statuses.Count > 0)
        {
            var statuses = criteria.Statuses.ToList();
            orders = orders.Where(o => statuses.Contains(o.Status));
        }

        if (criteria.From is { } from)
        {
            var start = SerbianTime.StartOfDay(from);
            orders = orders.Where(o => o.PlacedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = SerbianTime.StartOfDay(to.AddDays(1));
            orders = orders.Where(o => o.PlacedAt < end);
        }

        var total = await orders.CountAsync(cancellationToken);

        var ordered = criteria.Sort switch
        {
            AdminOrderSort.Oldest => orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id),
            AdminOrderSort.TotalDescending => orders.OrderByDescending(o => o.Total).ThenByDescending(o => o.PlacedAt).ThenByDescending(o => o.Id),
            AdminOrderSort.TotalAscending => orders.OrderBy(o => o.Total).ThenByDescending(o => o.PlacedAt).ThenByDescending(o => o.Id),
            _ => orders.OrderByDescending(o => o.PlacedAt).ThenByDescending(o => o.Id),
        };

        var items = await ordered
            .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize)
            .Select(o => new AdminOrderListItem(
                o.Id,
                o.OrderNumber,
                o.PlacedAt,
                o.ContactFirstName + " " + o.ContactLastName,
                db.Addresses.Where(a => a.Id == o.ShippingAddressId).Select(a => a.City).First(),
                o.Items.Count(),
                o.Items.Sum(i => i.Quantity),
                o.Total,
                o.Status,
                o.CollectedAmount != null))
            .ToListAsync(cancellationToken);

        return PagedResult<AdminOrderListItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<OrderSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Orders.AsNoTracking()
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        return new OrderSummaryResponse(
            [.. Enum.GetValues<OrderStatus>().Select(s => new OrderStatusCount(s, counts.GetValueOrDefault(s)))],
            counts.Values.Sum());
    }

    public async Task<AdminOrderDetail> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException();

        var address = await db.Addresses.AsNoTracking().SingleAsync(a => a.Id == order.ShippingAddressId, cancellationToken);

        var timeline = await (
                from h in db.OrderStatusHistory.AsNoTracking()
                where h.OrderId == id
                join admin in db.AdminUsers on h.ChangedByAdminId equals (Guid?)admin.Id into admins
                from admin in admins.DefaultIfEmpty()
                orderby h.CreatedAt, h.Id
                select new AdminOrderTimelineEntry(
                    h.Id, h.PreviousStatus, h.NewStatus, h.ChangedByAdminId, admin != null ? admin.FullName : null,
                    h.InternalNote, h.CustomerMessage, h.CreatedAt))
            .ToListAsync(cancellationToken);

        var notes = await (
                from n in db.OrderNotes.AsNoTracking()
                where n.OrderId == id
                join admin in db.AdminUsers on n.AdminUserId equals admin.Id
                orderby n.CreatedAt, n.Id
                select new AdminOrderNote(n.Id, n.Note, n.AdminUserId, admin.FullName, n.CreatedAt))
            .ToListAsync(cancellationToken);

        var shipments = (await (
                    from s in db.Shipments.AsNoTracking()
                    where s.OrderId == id
                    join c in db.CourierCompanies on s.CourierCompanyId equals c.Id
                    orderby s.CreatedAt, s.Id
                    select new { Shipment = s, c.Name, c.IsActive, c.TrackingUrlTemplate })
                .ToListAsync(cancellationToken))
            .Select(x => new AdminOrderShipment(
                x.Shipment.Id,
                new AdminCourierSummary(x.Shipment.CourierCompanyId, x.Name, x.IsActive),
                x.Shipment.TrackingNumber,
                CourierCompany.TrackingUrl(x.TrackingUrlTemplate, x.Shipment.TrackingNumber),
                x.Shipment.ShippedAt,
                x.Shipment.Note,
                x.Shipment.CreatedAt,
                x.Shipment.UpdatedAt))
            .ToList();

        return new AdminOrderDetail(
            order.Id,
            order.OrderNumber,
            order.Status,
            order.PlacedAt,
            order.UpdatedAt,
            order.Version,
            new AdminOrderContact(order.CustomerId, order.ContactFirstName, order.ContactLastName, order.ContactEmail, order.ContactPhone),
            new AdminOrderAddress(address.City, address.Street, address.PostalCode),
            order.DeliveryNote,
            order.PaymentMethod,
            [.. order.Items
                .OrderBy(i => i.ProductName, StringComparer.Ordinal).ThenBy(i => i.VariantName, StringComparer.Ordinal).ThenBy(i => i.Sku, StringComparer.Ordinal)
                .Select(i => new AdminOrderItem(i.Id, i.VariantId, i.ProductName, i.VariantName, i.Sku, i.UnitPrice, i.Quantity, i.LineTotal))],
            order.Subtotal,
            order.ShippingPrice,
            order.Total,
            Money.Currency,
            new AdminOrderPayment(order.CollectedAmount, order.CollectedAt, order.CollectedAmount is not null, order.Status == OrderStatus.Delivered),
            timeline,
            notes,
            shipments,
            [.. OrderStatusMachine.TransitionsFrom(order.Status).Select(AllowedStatusTransition.From)],
            await emails.ForOrderAsync(id, cancellationToken));
    }

    public async Task<AdminOrderDetail> ChangeStatusAsync(Guid id, ChangeOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        var target = request.NewStatus!.Value;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var order = await db.Orders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException();
        EnsureVersion(order, request.Version!.Value);

        if (!OrderStatusMachine.CanTransition(order.Status, target))
        {
            throw new InvalidStatusTransitionException(order.Status, target, OrderStatusMachine.AllowedNext(order.Status));
        }

        var transition = OrderStatusMachine.Describe(order.Status, target);
        var now = timeProvider.GetUtcNow();
        var previous = order.Status;

        if (transition.RequiresShipment && request.Shipment is null)
        {
            throw new RequestValidationException("shipment", "Za status „Poslata” izaberite kurirsku službu.");
        }

        if (!transition.RequiresShipment && request.Shipment is not null)
        {
            throw new RequestValidationException("shipment", "Pošiljka se unosi samo pri prelasku u status „Poslata”.");
        }

        if (transition.StockReturn == StockReturnPolicy.AdminChoice && request.Restock is null)
        {
            throw new RequestValidationException("restock", "Izaberite da li se vraćena roba vraća na stanje.");
        }

        if (transition.StockReturn != StockReturnPolicy.AdminChoice && request.Restock is not null)
        {
            throw new RequestValidationException("restock", "Vraćanje na stanje se bira samo pri otkazivanju posle neuspešne isporuke.");
        }

        Shipment? shipment = null;
        if (request.Shipment is { } input)
        {
            var courierId = input.CourierCompanyId!.Value;
            if (!await db.CourierCompanies.AnyAsync(c => c.Id == courierId && c.IsActive, cancellationToken))
            {
                throw new RequestValidationException("shipment.courierCompanyId", "Izaberite aktivnu kurirsku službu.");
            }

            var shippedAt = input.ShippedAt?.ToUniversalTime() ?? now;
            EnsureDate(shippedAt, order, now, "shipment.shippedAt", "Datum slanja");
            shipment = Shipment.Create(order.Id, courierId, input.TrackingNumber, shippedAt, input.Note, now);
            db.Shipments.Add(shipment);
        }

        var history = order.ChangeStatus(target, admin.Id, request.InternalNote, request.CustomerMessage, now);
        db.OrderStatusHistory.Add(history);

        var returnType = transition.StockReturn switch
        {
            StockReturnPolicy.Automatic => InventoryMovementType.OrderCancelled,
            StockReturnPolicy.AdminChoice when request.Restock == true => InventoryMovementType.ReturnRestock,
            _ => (InventoryMovementType?)null,
        };

        audit.Record(StatusChangedAction, AuditEntityType, order.Id.ToString(), new
        {
            status = new { old = previous.ToString(), @new = target.ToString() },
            historyId = history.Id,
            shipmentId = shipment?.Id,
            restock = request.Restock,
            stockReturned = returnType?.ToString(),
        });

        // The email of this transition, in this transaction (P1.7: Accepted, Preparing, Shipped, Delivered, Cancelled and
        // Rejected send one, with a freshly issued tracking code; DeliveryFailed does not). Dedup key order:{id}:history:{historyId}.
        await notifier.StatusChangedAsync(order, history, shipment, cancellationToken);

        // The concurrency check (version = xmin) happens here; a stale version gives 409 CONCURRENT_EDIT and nothing is kept.
        db.Entry(order).Property(o => o.Version).OriginalValue = request.Version.Value;
        await db.SaveChangesAsync(cancellationToken);

        if (returnType is { } movementType)
        {
            await inventory.ReturnAsync([.. order.Items.Select(i => new StockLine(i.VariantId, i.Quantity))], movementType, order.Id, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminOrderDetail> AddNoteAsync(Guid id, AddOrderNoteRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        if (!await db.Orders.AnyAsync(o => o.Id == id, cancellationToken))
        {
            throw new NotFoundException();
        }

        var note = OrderNote.Create(id, admin.Id, request.Note!, timeProvider.GetUtcNow());
        db.OrderNotes.Add(note);
        audit.Record(NoteAddedAction, AuditEntityType, id.ToString(), new { noteId = note.Id });
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminOrderDetail> UpdateShipmentAsync(Guid id, Guid shipmentId, UpdateShipmentRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException();
        var shipment = await db.Shipments.SingleOrDefaultAsync(s => s.Id == shipmentId && s.OrderId == id, cancellationToken)
            ?? throw new NotFoundException();

        var courierId = request.CourierCompanyId!.Value;
        if (courierId != shipment.CourierCompanyId
            && !await db.CourierCompanies.AnyAsync(c => c.Id == courierId && c.IsActive, cancellationToken))
        {
            throw new RequestValidationException("courierCompanyId", "Izaberite aktivnu kurirsku službu.");
        }

        var now = timeProvider.GetUtcNow();
        var shippedAt = request.ShippedAt!.Value.ToUniversalTime();
        EnsureDate(shippedAt, order, now, "shippedAt", "Datum slanja");

        // Tracked change: the audit interceptor records Shipment.Updated with the changed fields.
        shipment.Update(courierId, request.TrackingNumber, shippedAt, request.Note, now);
        await db.SaveChangesAsync(cancellationToken);

        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminOrderDetail> SetPaymentAsync(Guid id, SetOrderPaymentRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException();
        EnsureVersion(order, request.Version!.Value);

        if (order.Status != OrderStatus.Delivered)
        {
            throw new ConflictException(ErrorTexts.OrderNotDeliveredDetail, ErrorCodes.OrderNotDelivered, ErrorTexts.OrderNotDeliveredTitle);
        }

        var now = timeProvider.GetUtcNow();
        var before = new { amount = order.CollectedAmount, collectedAt = order.CollectedAt };

        if (request.CollectedAmount is { } amount)
        {
            var collectedAt = request.CollectedAt?.ToUniversalTime() ?? now;
            EnsureDate(collectedAt, order, now, "collectedAt", "Datum naplate");
            order.RecordPayment(amount, collectedAt, now);
            audit.Record(PaymentRecordedAction, AuditEntityType, id.ToString(), new
            {
                collectedAmount = new { old = before.amount, @new = order.CollectedAmount },
                collectedAt = new { old = before.collectedAt, @new = order.CollectedAt },
            });
        }
        else
        {
            order.ClearPayment(now);
            audit.Record(PaymentClearedAction, AuditEntityType, id.ToString(), new
            {
                collectedAmount = new { old = before.amount, @new = (decimal?)null },
                collectedAt = new { old = before.collectedAt, @new = (DateTimeOffset?)null },
            });
        }

        db.Entry(order).Property(o => o.Version).OriginalValue = request.Version.Value;
        await db.SaveChangesAsync(cancellationToken);

        db.ChangeTracker.Clear();
        return await GetAsync(id, cancellationToken);
    }

    private static void EnsureVersion(Order order, uint version)
    {
        if (order.Version != version)
        {
            throw new ConflictException(ErrorTexts.ConcurrentEditDetail, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle);
        }
    }

    private static void EnsureDate(DateTimeOffset value, Order order, DateTimeOffset now, string field, string label)
    {
        if (value > now + FutureTolerance)
        {
            throw new RequestValidationException(field, $"{label} ne može biti u budućnosti.");
        }

        if (value < order.PlacedAt - FutureTolerance)
        {
            throw new RequestValidationException(field, $"{label} ne može biti pre datuma porudžbine.");
        }
    }

    /// <summary>
    /// The digits of a search term that looks like a phone number, without the country code or the trunk zero, so that
    /// "064 123" and "+381 64 123" both find "+381641234567". Null when the term is not a phone fragment.
    /// </summary>
    public static string? PhoneFragment(string search)
    {
        var trimmed = search.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => !char.IsDigit(c) && c is not (' ' or '+' or '-' or '/' or '(' or ')' or '.')))
        {
            return null;
        }

        var digits = new string([.. trimmed.Where(char.IsDigit)]);
        if (trimmed.StartsWith('+') && digits.StartsWith(SerbianPhone.CountryCode, StringComparison.Ordinal))
        {
            digits = digits[SerbianPhone.CountryCode.Length..];
        }
        else if (digits.StartsWith("00" + SerbianPhone.CountryCode, StringComparison.Ordinal))
        {
            digits = digits[(SerbianPhone.CountryCode.Length + 2)..];
        }

        digits = digits.TrimStart('0');
        return digits.Length >= 3 ? digits : null;
    }
}
