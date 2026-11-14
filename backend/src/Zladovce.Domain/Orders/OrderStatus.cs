namespace Zladovce.Domain.Orders;

/// <summary>
/// Status of an order (P1.5). Stored as text with a check constraint; JSON uses these names.
/// Serbian labels: see <see cref="OrderStatusLabels"/>.
/// </summary>
public enum OrderStatus
{
    /// <summary>Primljena – created by checkout, stock already deducted.</summary>
    Received,

    /// <summary>Prihvaćena.</summary>
    Accepted,

    /// <summary>U pripremi.</summary>
    Preparing,

    /// <summary>Poslata – handed to a courier (a shipment exists).</summary>
    Shipped,

    /// <summary>Isporučena – final.</summary>
    Delivered,

    /// <summary>Otkazana – final.</summary>
    Cancelled,

    /// <summary>Odbijena – final.</summary>
    Rejected,

    /// <summary>Neuspešna isporuka – the courier could not deliver.</summary>
    DeliveryFailed,
}

/// <summary>Serbian (Latin) labels of the statuses, as used in the UI and in emails.</summary>
public static class OrderStatusLabels
{
    public static string Of(OrderStatus status) => status switch
    {
        OrderStatus.Received => "Primljena",
        OrderStatus.Accepted => "Prihvaćena",
        OrderStatus.Preparing => "U pripremi",
        OrderStatus.Shipped => "Poslata",
        OrderStatus.Delivered => "Isporučena",
        OrderStatus.Cancelled => "Otkazana",
        OrderStatus.Rejected => "Odbijena",
        OrderStatus.DeliveryFailed => "Neuspešna isporuka",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>What happens to the stock of the order's items when a transition is made.</summary>
public enum StockReturnPolicy
{
    /// <summary>Stock is not touched.</summary>
    None,

    /// <summary>Everything goes back on stock automatically (movement <c>OrderCancelled</c>): Rejected, or Cancelled before shipping.</summary>
    Automatic,

    /// <summary>
    /// The administrator must decide (<c>restock</c> true or false): Cancelled after a failed delivery, because returned food
    /// may be unusable. True puts the goods back (movement <c>ReturnRestock</c>).
    /// </summary>
    AdminChoice,
}

/// <summary>What a transition needs and does besides changing the status.</summary>
/// <param name="To">Target status.</param>
/// <param name="RequiresShipment">A shipment (courier company, optional tracking number and date) must be given: moving to Shipped.</param>
/// <param name="StockReturn">Effect on stock.</param>
public sealed record OrderTransition(OrderStatus To, bool RequiresShipment, StockReturnPolicy StockReturn);

/// <summary>The order status state machine of P1.5. The only place that decides which transitions exist.</summary>
public static class OrderStatusMachine
{
    private static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> Next = new Dictionary<OrderStatus, OrderStatus[]>
    {
        [OrderStatus.Received] = [OrderStatus.Accepted, OrderStatus.Rejected, OrderStatus.Cancelled],
        [OrderStatus.Accepted] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.DeliveryFailed],
        [OrderStatus.DeliveryFailed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Rejected] = [],
    };

    /// <summary>Statuses that can follow <paramref name="from"/>, in display order. Empty for a final status.</summary>
    public static IReadOnlyList<OrderStatus> AllowedNext(OrderStatus from) =>
        Next.TryGetValue(from, out var next) ? next : throw new ArgumentOutOfRangeException(nameof(from), from, null);

    public static bool IsFinal(OrderStatus status) => AllowedNext(status).Count == 0;

    public static bool CanTransition(OrderStatus from, OrderStatus to) => AllowedNext(from).Contains(to);

    /// <summary>The transitions allowed from <paramref name="from"/> with their requirements.</summary>
    public static IReadOnlyList<OrderTransition> TransitionsFrom(OrderStatus from) =>
        [.. AllowedNext(from).Select(to => Describe(from, to))];

    /// <summary>Requirements of an allowed transition. Throws <see cref="Common.DomainException"/> for a transition that does not exist.</summary>
    public static OrderTransition Describe(OrderStatus from, OrderStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new Common.DomainException($"An order cannot go from {from} to {to}.");
        }

        var stock = (from, to) switch
        {
            (_, OrderStatus.Rejected) => StockReturnPolicy.Automatic,
            (OrderStatus.DeliveryFailed, OrderStatus.Cancelled) => StockReturnPolicy.AdminChoice,
            (_, OrderStatus.Cancelled) => StockReturnPolicy.Automatic,
            _ => StockReturnPolicy.None,
        };

        return new OrderTransition(to, to == OrderStatus.Shipped, stock);
    }
}
