using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Cart;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Inventory;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Common;
using Zladovce.Domain.Customers;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Tracking;

namespace Zladovce.Application.Orders;

/// <summary>Creates orders from the browser checkout (A4 "Checkout").</summary>
public interface ICheckoutService
{
    /// <summary>
    /// Places the order in one transaction: idempotency check, pricing (<see cref="ICartPricingService"/>, the rules of the
    /// cart quote), customer upsert, order number, address, order, item snapshots, history <c>Received</c>, stock deduction and
    /// movements. A repeated request with the same key and the same content returns the first order (<c>Created = false</c>).
    /// </summary>
    Task<CheckoutResult> PlaceOrderAsync(Guid idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken);
}

/// <summary>A checkout request after trimming and normalisation: what is compared for idempotency and stored.</summary>
public sealed record NormalizedCheckout(
    IReadOnlyList<CartItem> Items,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string City,
    string Street,
    string PostalCode,
    string? DeliveryNote,
    decimal? ExpectedTotal)
{
    /// <summary>Normalises a request that passed validation.</summary>
    public static NormalizedCheckout From(CreateOrderRequest request)
    {
        var customer = request.Customer!;
        var address = request.Address!;
        return new NormalizedCheckout(
            [.. request.Items!.Select(i => new CartItem(i.VariantId!.Value, i.Quantity!.Value, i.KnownUnitPrice))],
            customer.FirstName!.Trim(),
            customer.LastName!.Trim(),
            customer.Email!.Trim().ToLowerInvariant(),
            SerbianPhone.Normalize(customer.Phone) ?? throw new InvalidOperationException("The phone number was not validated."),
            address.City!.Trim(),
            address.Street!.Trim(),
            address.PostalCode!.Trim(),
            string.IsNullOrWhiteSpace(request.DeliveryNote) ? null : request.DeliveryNote.Trim(),
            request.ExpectedTotal);
    }
}

/// <summary>
/// The fingerprint of a checkout request stored with the order (<c>orders.request_hash</c>). Two requests are "the same" when
/// they order the same quantities of the same variants for the same contact and address: lines are merged and sorted, texts
/// trimmed, the email lower-cased and the phone in E.164. The prices the browser last saw (<c>knownUnitPrice</c>,
/// <c>expectedTotal</c>) are not part of it, because they do not change what is ordered.
/// </summary>
public static class CheckoutRequestHash
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static byte[] Compute(NormalizedCheckout checkout)
    {
        var lines = CartQuoteCalculator.Merge(checkout.Items)
            .OrderBy(i => i.VariantId)
            .Select(i => new object[] { i.VariantId.ToString("D"), i.Quantity })
            .ToArray();

        var canonical = JsonSerializer.Serialize(
            new object?[]
            {
                "v1", lines, checkout.FirstName, checkout.LastName, checkout.Email, checkout.Phone,
                checkout.City, checkout.Street, checkout.PostalCode, checkout.DeliveryNote,
            },
            JsonOptions);

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }
}

/// <summary>
/// The SQL statements of checkout that need PostgreSQL features (advisory lock, upserts). Implemented in the infrastructure;
/// every call joins the current transaction of the shared database context.
/// </summary>
public interface ICheckoutStore
{
    /// <summary>Transaction-scoped lock on the idempotency key: requests with the same key run one after the other.</summary>
    Task LockIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Inserts the customer or updates the latest name and phone of the existing one (email is unique). Returns its id.</summary>
    Task<Guid> UpsertCustomerAsync(string email, string firstName, string lastName, string phone, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Next order sequence of the year. The counter row stays locked until the transaction ends, so there are no gaps.</summary>
    Task<int> NextOrderSequenceAsync(int year, CancellationToken cancellationToken);
}

/// <summary>Row lock on an order for the rest of the current transaction (<c>SELECT … FOR UPDATE</c>; does not change its version).</summary>
public interface IOrderLocks
{
    Task LockAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class CheckoutService(
    IZladovceDbContext db,
    ICartPricingService pricing,
    IInventoryService inventory,
    ICheckoutStore store,
    IOrderNotifier notifier,
    TimeProvider timeProvider) : ICheckoutService
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn-RS");

    public async Task<CheckoutResult> PlaceOrderAsync(Guid idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("The idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        var checkout = NormalizedCheckout.From(request);
        var requestHash = CheckoutRequestHash.Compute(checkout);

        try
        {
            return await PlaceAsync(idempotencyKey, checkout, requestHash, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Last guard behind the advisory lock: the unique key refused a second order with this key (another request
            // committed first). Anything else is a real failure and is rethrown.
            db.ChangeTracker.Clear();
            if (await FindByKeyAsync(idempotencyKey, cancellationToken) is { } existing)
            {
                return Replay(existing, requestHash);
            }

            throw;
        }
    }

    private async Task<CheckoutResult> PlaceAsync(Guid idempotencyKey, NormalizedCheckout checkout, byte[] requestHash, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Requests with the same key wait for each other here, so the second one sees the first one's order.
        await store.LockIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        if (await FindByKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            return Replay(existing, requestHash);
        }

        var quote = await pricing.QuoteAsync(checkout.Items, cancellationToken);
        EnsureCanOrder(checkout, quote);

        // PostgreSQL keeps microseconds: the response of the new order must equal what a replay reads back.
        var clock = timeProvider.GetUtcNow();
        var now = new DateTimeOffset(clock.UtcTicks - (clock.UtcTicks % 10), TimeSpan.Zero);
        var customerId = await store.UpsertCustomerAsync(checkout.Email, checkout.FirstName, checkout.LastName, checkout.Phone, now, cancellationToken);
        var orderNumber = OrderNumber.Format(SerbianTime.YearOf(now), await store.NextOrderSequenceAsync(SerbianTime.YearOf(now), cancellationToken));

        var address = Address.Create(customerId, checkout.City, checkout.Street, checkout.PostalCode, now);
        var lines = quote.Lines.Select(l => new OrderLine(l.VariantId, l.ProductName, l.VariantName, l.Sku, l.UnitPrice, l.Quantity)).ToList();
        var (order, history) = Order.Place(
            orderNumber,
            idempotencyKey,
            requestHash,
            customerId,
            address.Id,
            new OrderContact(checkout.FirstName, checkout.LastName, checkout.Email, checkout.Phone),
            checkout.DeliveryNote,
            lines,
            quote.Shipping.Price,
            now);

        if (order.Total != quote.Total || order.Subtotal != quote.Subtotal)
        {
            throw new InvalidOperationException("Order totals differ from the cart pricing.");
        }

        db.Addresses.Add(address);
        db.Orders.Add(order);
        db.OrderStatusHistory.Add(history);

        // Same transaction: the tracking token hash (order_tracking_tokens) and the "Porudžbina primljena" outbox row (P1.7,
        // dedup key order:{id}:received) with the code encrypted. A rollback below drops both together with the order.
        var trackingCode = await notifier.OrderPlacedAsync(order, address, cancellationToken);

        try
        {
            // Conditional updates in variant-id order; saves the order, items, history and movements in the same transaction.
            await inventory.DeductAsync([.. lines.Select(l => new StockLine(l.VariantId, l.Quantity))], order.Id, cancellationToken);
        }
        catch (InsufficientStockException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // Priced again after the rollback, so the customer sees what is available now.
            var fresh = await pricing.QuoteAsync(checkout.Items, cancellationToken);
            throw new CheckoutConflictException(
                ErrorCodes.InsufficientStock,
                ErrorTexts.InsufficientStockTitle,
                ErrorTexts.CheckoutInsufficientStockDetail,
                fresh,
                ShortageErrors(checkout.Items, exception.Shortages.Select(s => s.VariantId), fresh));
        }

        await transaction.CommitAsync(cancellationToken);
        return new CheckoutResult(PlacedOrderResponse.From(order) with { TrackingCode = TrackingCode.Format(trackingCode) }, Created: true);
    }

    private Task<Order?> FindByKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

    private static CheckoutResult Replay(Order existing, byte[] requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, requestHash))
        {
            throw new ConflictException(ErrorTexts.IdempotencyKeyReusedDetail, ErrorCodes.IdempotencyKeyReused, ErrorTexts.IdempotencyKeyReusedTitle);
        }

        return new CheckoutResult(PlacedOrderResponse.From(existing), Created: false);
    }

    /// <summary>The order must be exactly what the shop showed: same lines, quantities and (when sent) prices and total.</summary>
    private static void EnsureCanOrder(NormalizedCheckout checkout, CartQuoteResponse quote)
    {
        if (!quote.Shipping.Enabled)
        {
            throw new CheckoutConflictException(ErrorCodes.ShippingDisabled, ErrorTexts.ShippingDisabledTitle, ErrorTexts.ShippingDisabledDetail, quote);
        }

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var adjustment in quote.Adjustments)
        {
            var index = IndexOf(checkout.Items, adjustment.VariantId);
            var (field, message) = adjustment.Reason switch
            {
                CartAdjustmentReason.Unavailable => ($"items[{index}].variantId", "Proizvod više nije dostupan."),
                CartAdjustmentReason.QuantityReduced => ($"items[{index}].quantity", $"Dostupno najviše: {adjustment.AvailableQuantity}."),
                _ => ($"items[{index}].knownUnitPrice", $"Cena je promenjena: sada {Format(adjustment.UnitPrice!.Value)} RSD."),
            };
            Add(errors, field, message);
        }

        if (checkout.ExpectedTotal is { } expected && expected != quote.Total)
        {
            Add(errors, "expectedTotal", $"Ukupan iznos je promenjen: sada {Format(quote.Total)} RSD.");
        }

        if (errors.Count > 0 || quote.Lines.Count == 0)
        {
            throw new CheckoutConflictException(
                ErrorCodes.CartChanged, ErrorTexts.CartChangedTitle, ErrorTexts.CartChangedDetail, quote, Freeze(errors));
        }

        if (!quote.MeetsMinimumOrder)
        {
            throw new CheckoutConflictException(
                ErrorCodes.MinimumOrderNotMet,
                ErrorTexts.MinimumOrderNotMetTitle,
                $"Minimalni iznos porudžbine je {Format(quote.MinOrderAmount!.Value)} RSD. Dodajte proizvode u vrednosti od još {Format(quote.AmountUntilMinimumOrder!.Value)} RSD.",
                quote);
        }

        if (!quote.CanCheckout)
        {
            throw new InvalidOperationException("The quote cannot be ordered for an unknown reason.");
        }
    }

    private static Dictionary<string, string[]> ShortageErrors(IReadOnlyList<CartItem> items, IEnumerable<Guid> variantIds, CartQuoteResponse fresh)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var variantId in variantIds)
        {
            var line = fresh.Lines.FirstOrDefault(l => l.VariantId == variantId);
            Add(errors, $"items[{IndexOf(items, variantId)}].quantity", line is null ? "Proizvod više nije dostupan." : $"Dostupno najviše: {line.MaxOrderQuantity}.");
        }

        return Freeze(errors);
    }

    private static int IndexOf(IReadOnlyList<CartItem> items, Guid variantId)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].VariantId == variantId)
            {
                return i;
            }
        }

        return 0;
    }

    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            errors[field] = messages = [];
        }

        messages.Add(message);
    }

    private static Dictionary<string, string[]> Freeze(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

    private static string Format(decimal amount) => amount.ToString("N2", Serbian);
}
