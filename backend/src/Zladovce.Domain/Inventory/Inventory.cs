using System.Text.Json.Serialization;
using Zladovce.Domain.Common;

namespace Zladovce.Domain.Inventory;

/// <summary>Stock situation of a variant relative to its low-stock threshold.</summary>
public enum StockStatus
{
    /// <summary>More than the threshold.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Some stock, but at or below the threshold.</summary>
    [JsonStringEnumMemberName("low")]
    Low,

    /// <summary>Nothing on stock.</summary>
    [JsonStringEnumMemberName("out")]
    Out,
}

public static class StockRules
{
    /// <summary>Upper bound for a stock figure or a single change; keeps arithmetic far away from integer overflow.</summary>
    public const int MaxQuantity = 1_000_000;

    public static StockStatus StatusOf(int quantityOnHand, int lowStockThreshold) =>
        quantityOnHand <= 0 ? StockStatus.Out
        : quantityOnHand <= lowStockThreshold ? StockStatus.Low
        : StockStatus.Ok;

    /// <summary>
    /// The largest quantity a customer may order in one go: the stock, limited by the configured cap.
    /// The exact stock figure is never shown publicly.
    /// </summary>
    public static int MaxOrderQuantity(int quantityOnHand, int cap) => Math.Max(0, Math.Min(quantityOnHand, Math.Max(cap, 0)));
}

/// <summary>Stock of one variant (table <c>inventory</c>, one row per variant).</summary>
public sealed class InventoryItem
{
    private InventoryItem()
    {
    }

    public Guid VariantId { get; private set; }
    public int QuantityOnHand { get; private set; }
    public int LowStockThreshold { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public StockStatus Status => StockRules.StatusOf(QuantityOnHand, LowStockThreshold);

    public static InventoryItem Create(Guid variantId, int initialQuantity, int lowStockThreshold, DateTimeOffset now)
    {
        if (variantId == Guid.Empty)
        {
            throw new DomainException("Inventory requires a variant.");
        }

        if (initialQuantity is < 0 or > StockRules.MaxQuantity)
        {
            throw new DomainException("Initial stock is out of range.");
        }

        EnsureThreshold(lowStockThreshold);

        return new InventoryItem
        {
            VariantId = variantId,
            QuantityOnHand = initialQuantity,
            LowStockThreshold = lowStockThreshold,
            UpdatedAt = now,
        };
    }

    public void SetLowStockThreshold(int lowStockThreshold, DateTimeOffset now)
    {
        EnsureThreshold(lowStockThreshold);
        if (LowStockThreshold == lowStockThreshold)
        {
            return;
        }

        LowStockThreshold = lowStockThreshold;
        UpdatedAt = now;
    }

    private static void EnsureThreshold(int lowStockThreshold)
    {
        if (lowStockThreshold is < 0 or > StockRules.MaxQuantity)
        {
            throw new DomainException("Low-stock threshold is out of range.");
        }
    }
}

/// <summary>Kind of stock change.</summary>
public enum InventoryMovementType
{
    /// <summary>Stock entered when the variant was created.</summary>
    InitialStock,

    /// <summary>Correction by an administrator; always has a reason.</summary>
    ManualAdjustment,

    /// <summary>Deducted for a new order.</summary>
    OrderPlaced,

    /// <summary>Returned because an order was rejected or cancelled before shipping.</summary>
    OrderCancelled,

    /// <summary>Returned goods put back on stock by an administrator.</summary>
    ReturnRestock,
}

/// <summary>Append-only record of one stock change (table <c>inventory_movements</c>).</summary>
public sealed class InventoryMovement
{
    private InventoryMovement()
    {
    }

    public Guid Id { get; private set; }
    public Guid VariantId { get; private set; }
    public InventoryMovementType MovementType { get; private set; }
    public int QuantityDelta { get; private set; }

    /// <summary>Stock after this movement.</summary>
    public int QuantityAfter { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>The order that caused the movement. The foreign key is added together with the orders table.</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>Null = the system (for example checkout).</summary>
    public Guid? AdminUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static InventoryMovement Create(
        Guid variantId,
        InventoryMovementType movementType,
        int quantityDelta,
        int quantityAfter,
        string? reason,
        Guid? orderId,
        Guid? adminUserId,
        DateTimeOffset now)
    {
        if (variantId == Guid.Empty)
        {
            throw new DomainException("A stock movement requires a variant.");
        }

        if (!Enum.IsDefined(movementType))
        {
            throw new DomainException("Unknown stock movement type.");
        }

        if (quantityDelta == 0)
        {
            throw new DomainException("A stock movement must change the quantity.");
        }

        if (quantityAfter < 0)
        {
            throw new DomainException("Stock cannot be negative.");
        }

        var trimmedReason = Text.NullIfBlank(reason);
        if (movementType == InventoryMovementType.ManualAdjustment && trimmedReason is null)
        {
            throw new DomainException("A manual stock adjustment requires a reason.");
        }

        var expectedSign = movementType switch
        {
            InventoryMovementType.InitialStock or InventoryMovementType.OrderCancelled or InventoryMovementType.ReturnRestock => 1,
            InventoryMovementType.OrderPlaced => -1,
            _ => 0,
        };
        if (expectedSign != 0 && Math.Sign(quantityDelta) != expectedSign)
        {
            throw new DomainException("The quantity change has the wrong sign for this movement type.");
        }

        return new InventoryMovement
        {
            Id = Guid.CreateVersion7(now),
            VariantId = variantId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            QuantityAfter = quantityAfter,
            Reason = trimmedReason,
            OrderId = orderId,
            AdminUserId = adminUserId,
            CreatedAt = now,
        };
    }
}
