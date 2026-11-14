using Zladovce.Domain.Common;

namespace Zladovce.Domain.Orders;

/// <summary>How the customer pays. Cash on delivery is the only method of the MVP.</summary>
public enum PaymentMethod
{
    CashOnDelivery,
}

/// <summary>Order number <c>ZLD-YYYY-000001</c>: the Serbian local year and a yearly sequence of at least six digits.</summary>
public static class OrderNumber
{
    public const string Prefix = "ZLD";

    public static string Format(int year, int sequence)
    {
        if (year is < 2000 or > 9999)
        {
            throw new DomainException("Order year is out of range.");
        }

        if (sequence < 1)
        {
            throw new DomainException("Order sequence starts at 1.");
        }

        return $"{Prefix}-{year:D4}-{sequence:D6}";
    }
}

/// <summary>Contact data of the buyer as entered at checkout, copied into the order.</summary>
/// <param name="Phone">E.164.</param>
public sealed record OrderContact(string FirstName, string LastName, string Email, string Phone);

/// <summary>A priced line as checkout hands it to <see cref="Order.Place"/>.</summary>
public sealed record OrderLine(Guid VariantId, string ProductName, string VariantName, string Sku, decimal UnitPrice, int Quantity);

/// <summary>
/// An order (table <c>orders</c>). Prices, contact data and items are snapshots taken at checkout and never change; only the
/// status (through <see cref="OrderStatusMachine"/>) and the collected payment change later.
/// </summary>
public sealed class Order
{
    public const int MaxDeliveryNoteLength = 500;

    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    /// <summary><c>ZLD-YYYY-000001</c>, unique.</summary>
    public string OrderNumber { get; private set; } = null!;

    /// <summary>The <c>Idempotency-Key</c> header of the checkout request, unique.</summary>
    public Guid IdempotencyKey { get; private set; }

    /// <summary>SHA-256 of the normalised checkout request; a replay with the same key must match it.</summary>
    public byte[] RequestHash { get; private set; } = null!;

    public Guid CustomerId { get; private set; }
    public Guid ShippingAddressId { get; private set; }
    public OrderStatus Status { get; private set; }

    public string ContactFirstName { get; private set; } = null!;
    public string ContactLastName { get; private set; } = null!;
    public string ContactEmail { get; private set; } = null!;

    /// <summary>E.164.</summary>
    public string ContactPhone { get; private set; } = null!;
    public string? DeliveryNote { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }

    public decimal Subtotal { get; private set; }

    /// <summary>Shipping charged, a snapshot of the store settings at checkout.</summary>
    public decimal ShippingPrice { get; private set; }

    /// <summary><c>Subtotal + ShippingPrice</c> (check constraint).</summary>
    public decimal Total { get; private set; }

    /// <summary>Money actually received from the courier, entered by an administrator (P1.8 "Stvarno naplaćeno").</summary>
    public decimal? CollectedAmount { get; private set; }
    public DateTimeOffset? CollectedAt { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    /// <summary>
    /// Creates the order in status <see cref="OrderStatus.Received"/> together with its item snapshots and the first history
    /// entry (no administrator). Totals are recomputed here from the lines; <paramref name="shippingPrice"/> comes from
    /// the cart pricing.
    /// </summary>
    public static (Order Order, OrderStatusHistoryEntry History) Place(
        string orderNumber,
        Guid idempotencyKey,
        byte[] requestHash,
        Guid customerId,
        Guid shippingAddressId,
        OrderContact contact,
        string? deliveryNote,
        IReadOnlyList<OrderLine> lines,
        decimal shippingPrice,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(orderNumber) || idempotencyKey == Guid.Empty || customerId == Guid.Empty || shippingAddressId == Guid.Empty)
        {
            throw new DomainException("An order needs a number, an idempotency key, a customer and an address.");
        }

        if (requestHash is not { Length: 32 })
        {
            throw new DomainException("The request hash is a SHA-256 value.");
        }

        if (lines.Count == 0)
        {
            throw new DomainException("An order needs at least one item.");
        }

        if (lines.Select(l => l.VariantId).Distinct().Count() != lines.Count)
        {
            throw new DomainException("Each variant appears once in an order.");
        }

        if (shippingPrice < 0 || Money.Round(shippingPrice) != shippingPrice)
        {
            throw new DomainException("Shipping price must be a non-negative amount with two decimals.");
        }

        var note = Text.NullIfBlank(deliveryNote);
        if (note is { Length: > MaxDeliveryNoteLength })
        {
            throw new DomainException("Delivery note is too long.");
        }

        var order = new Order
        {
            Id = Guid.CreateVersion7(now),
            OrderNumber = orderNumber,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CustomerId = customerId,
            ShippingAddressId = shippingAddressId,
            Status = OrderStatus.Received,
            ContactFirstName = Text.NullIfBlank(contact.FirstName) ?? throw new DomainException("First name is required."),
            ContactLastName = Text.NullIfBlank(contact.LastName) ?? throw new DomainException("Last name is required."),
            ContactEmail = Text.NullIfBlank(contact.Email) ?? throw new DomainException("Email is required."),
            ContactPhone = SerbianPhone.Normalize(contact.Phone) ?? throw new DomainException("Phone must be a Serbian number."),
            DeliveryNote = note,
            PaymentMethod = PaymentMethod.CashOnDelivery,
            PlacedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var line in lines)
        {
            order._items.Add(OrderItem.Create(order.Id, line, now));
        }

        order.Subtotal = Money.Round(order._items.Sum(i => i.LineTotal));
        order.ShippingPrice = shippingPrice;
        order.Total = order.Subtotal + shippingPrice;

        var history = OrderStatusHistoryEntry.Create(order.Id, null, OrderStatus.Received, null, null, null, now);
        return (order, history);
    }

    /// <summary>
    /// Moves the order to <paramref name="to"/>. Only transitions of <see cref="OrderStatusMachine"/> are possible; the caller
    /// handles their requirements (shipment, stock) in the same transaction. Returns the history entry to store.
    /// </summary>
    public OrderStatusHistoryEntry ChangeStatus(OrderStatus to, Guid adminId, string? internalNote, string? customerMessage, DateTimeOffset now)
    {
        if (adminId == Guid.Empty)
        {
            throw new DomainException("Status changes are made by an administrator.");
        }

        OrderStatusMachine.Describe(Status, to);

        var entry = OrderStatusHistoryEntry.Create(Id, Status, to, adminId, internalNote, customerMessage, now);
        Status = to;
        UpdatedAt = now;
        return entry;
    }

    /// <summary>Records the money received from the courier. Only for delivered orders.</summary>
    public void RecordPayment(decimal amount, DateTimeOffset collectedAt, DateTimeOffset now)
    {
        if (Status != OrderStatus.Delivered)
        {
            throw new DomainException("Payment can be recorded for delivered orders only.");
        }

        if (amount <= 0 || Money.Round(amount) != amount || amount > Catalog.Pricing.MaxPrice)
        {
            throw new DomainException("Collected amount must be positive with at most two decimals.");
        }

        CollectedAmount = amount;
        CollectedAt = collectedAt.ToUniversalTime();
        UpdatedAt = now;
    }

    public void ClearPayment(DateTimeOffset now)
    {
        if (Status != OrderStatus.Delivered)
        {
            throw new DomainException("Payment can be changed for delivered orders only.");
        }

        CollectedAmount = null;
        CollectedAt = null;
        UpdatedAt = now;
    }
}

/// <summary>Snapshot of one ordered variant (table <c>order_items</c>). Never changes after checkout.</summary>
public sealed class OrderItem
{
    private OrderItem()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>For reports and restocking only; names and prices are the snapshot columns.</summary>
    public Guid VariantId { get; private set; }
    public string ProductName { get; private set; } = null!;
    public string VariantName { get; private set; } = null!;
    public string Sku { get; private set; } = null!;

    /// <summary>Price actually charged per piece.</summary>
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    /// <summary><c>UnitPrice × Quantity</c> (check constraint).</summary>
    public decimal LineTotal { get; private set; }

    internal static OrderItem Create(Guid orderId, OrderLine line, DateTimeOffset now)
    {
        if (line.VariantId == Guid.Empty || string.IsNullOrWhiteSpace(line.ProductName) || string.IsNullOrWhiteSpace(line.Sku)
            || line.VariantName is null)
        {
            throw new DomainException("An order item needs a variant, names and a SKU.");
        }

        if (line.Quantity <= 0)
        {
            throw new DomainException("Quantity must be positive.");
        }

        if (line.UnitPrice <= 0 || Money.Round(line.UnitPrice) != line.UnitPrice)
        {
            throw new DomainException("Unit price must be positive with two decimals.");
        }

        return new OrderItem
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            VariantId = line.VariantId,
            ProductName = line.ProductName,
            VariantName = line.VariantName,
            Sku = line.Sku,
            UnitPrice = line.UnitPrice,
            Quantity = line.Quantity,

            // Exact: two decimals times an integer has two decimals, so the check constraint holds without rounding.
            LineTotal = line.UnitPrice * line.Quantity,
        };
    }
}

/// <summary>Append-only record of one status change (table <c>order_status_history</c>).</summary>
public sealed class OrderStatusHistoryEntry
{
    public const int MaxTextLength = 2000;

    private OrderStatusHistoryEntry()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>Null for the first entry (order created).</summary>
    public OrderStatus? PreviousStatus { get; private set; }
    public OrderStatus NewStatus { get; private set; }

    /// <summary>Null = created by the customer's checkout.</summary>
    public Guid? ChangedByAdminId { get; private set; }

    /// <summary>Never shown to the customer.</summary>
    public string? InternalNote { get; private set; }

    /// <summary>Shown on the tracking page and in the email.</summary>
    public string? CustomerMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static OrderStatusHistoryEntry Create(
        Guid orderId, OrderStatus? previous, OrderStatus next, Guid? adminId, string? internalNote, string? customerMessage, DateTimeOffset now)
    {
        var note = Text.NullIfBlank(internalNote);
        var message = Text.NullIfBlank(customerMessage);
        if (note is { Length: > MaxTextLength } || message is { Length: > MaxTextLength })
        {
            throw new DomainException("Status change texts are too long.");
        }

        return new OrderStatusHistoryEntry
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            PreviousStatus = previous,
            NewStatus = next,
            ChangedByAdminId = adminId,
            InternalNote = note,
            CustomerMessage = message,
            CreatedAt = now,
        };
    }
}

/// <summary>
/// An internal note on an order without a status change (table <c>order_notes</c>, append-only). Kept apart from the status
/// history so that the history stays exactly one row per transition (P1.5) and the customer timeline never has to filter notes.
/// </summary>
public sealed class OrderNote
{
    public const int MaxLength = 2000;

    private OrderNote()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid AdminUserId { get; private set; }
    public string Note { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static OrderNote Create(Guid orderId, Guid adminUserId, string note, DateTimeOffset now)
    {
        var text = Text.NullIfBlank(note) ?? throw new DomainException("A note needs text.");
        if (text.Length > MaxLength || orderId == Guid.Empty || adminUserId == Guid.Empty)
        {
            throw new DomainException("Invalid order note.");
        }

        return new OrderNote { Id = Guid.CreateVersion7(now), OrderId = orderId, AdminUserId = adminUserId, Note = text, CreatedAt = now };
    }
}

/// <summary>
/// Yearly counter of order numbers (table <c>order_number_counters</c>). Incremented with an upsert inside the order
/// transaction; the row lock serialises checkouts at that point, and a rollback returns the number, so there are no gaps.
/// </summary>
public sealed class OrderNumberCounter
{
    private OrderNumberCounter()
    {
    }

    /// <summary>Serbian local year.</summary>
    public int Year { get; private set; }
    public int LastValue { get; private set; }
}
