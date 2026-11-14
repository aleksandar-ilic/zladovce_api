using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Inventory;

/// <summary>A quantity of one variant, as used by stock deductions and returns.</summary>
public sealed record StockLine(Guid VariantId, int Quantity);

/// <summary>Current stock of a variant.</summary>
public sealed record StockLevel(Guid VariantId, int QuantityOnHand, int LowStockThreshold)
{
    public StockStatus Status => StockRules.StatusOf(QuantityOnHand, LowStockThreshold);
}

public enum InventorySort
{
    /// <summary>Product name, then variant order.</summary>
    Product,
    StockAscending,
    StockDescending,
}

/// <summary>Filter of the inventory list.</summary>
/// <param name="Statuses">Rows in any of these statuses; empty = every status.</param>
public sealed record InventoryListCriteria(string? Search, IReadOnlySet<StockStatus> Statuses, InventorySort Sort, PageRequest Page);

public static class InventoryStatusFilter
{
    /// <summary>
    /// What "low on stock" means on the dashboard: <c>low</c> and <c>out</c> (stock at or below the threshold, including zero).
    /// The dashboard card links to <c>GET /api/v1/admin/inventory?status=low,out</c>, which returns exactly the same rows.
    /// </summary>
    public static readonly IReadOnlySet<StockStatus> LowStock = new HashSet<StockStatus> { StockStatus.Low, StockStatus.Out };
}

/// <summary>Body of <c>POST /api/v1/admin/inventory/{variantId}/adjustments</c>.</summary>
/// <param name="QuantityDelta">Change of the stock: positive adds, negative removes. Not zero.</param>
/// <param name="Reason">Why the stock is corrected. Required, up to 500 characters.</param>
public sealed record AdjustStockRequest(int QuantityDelta, string Reason);

/// <summary>Body of <c>PUT /api/v1/admin/inventory/{variantId}/threshold</c>.</summary>
public sealed record SetLowStockThresholdRequest
{
    /// <summary>The variant counts as low on stock at or below this quantity. 0 or greater. Must be sent.</summary>
    public required int LowStockThreshold { get; init; }
}

/// <summary>One row of the inventory list: a variant with its stock.</summary>
/// <param name="Status"><c>ok</c> (above the threshold), <c>low</c> (at or below it) or <c>out</c> (nothing on stock).</param>
/// <param name="IsActive">True when both the product and the variant are active, i.e. the variant is offered in the shop.</param>
/// <param name="UpdatedAt">Last change of the stock or the threshold.</param>
public sealed record InventoryItemResponse(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string VariantName,
    string Sku,
    int QuantityOnHand,
    int LowStockThreshold,
    StockStatus Status,
    bool IsActive,
    DateTimeOffset UpdatedAt);

/// <summary>One recorded stock change.</summary>
/// <param name="MovementType"><c>InitialStock</c>, <c>ManualAdjustment</c>, <c>OrderPlaced</c>, <c>OrderCancelled</c> or <c>ReturnRestock</c>.</param>
/// <param name="QuantityAfter">Stock after this change.</param>
/// <param name="AdminName">Administrator who made the change; null when the system did (for example an order).</param>
public sealed record InventoryMovementResponse(
    Guid Id,
    InventoryMovementType MovementType,
    int QuantityDelta,
    int QuantityAfter,
    string? Reason,
    Guid? OrderId,
    Guid? AdminUserId,
    string? AdminName,
    DateTimeOffset CreatedAt);

/// <summary>Variants with status <c>low</c> or <c>out</c> (stock at or below the threshold), emptiest first.</summary>
/// <param name="Items">At most <c>limit</c> rows.</param>
/// <param name="TotalItems">How many variants are <c>low</c> or <c>out</c> in total (= <c>totalItems</c> of <c>GET /admin/inventory?status=low,out</c>).</param>
public sealed record LowStockResponse(IReadOnlyList<InventoryItemResponse> Items, int TotalItems);

public sealed class AdjustStockRequestValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockRequestValidator()
    {
        RuleFor(r => r.QuantityDelta)
            .NotEqual(0).WithMessage("Promena količine ne može biti nula.")
            .InclusiveBetween(-StockRules.MaxQuantity, StockRules.MaxQuantity);
        RuleFor(r => r.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason)).WithMessage("Razlog promene je obavezan.")
            .MaximumLength(500);
    }
}

public sealed class SetLowStockThresholdRequestValidator : AbstractValidator<SetLowStockThresholdRequest>
{
    public SetLowStockThresholdRequestValidator()
    {
        RuleFor(r => r.LowStockThreshold).InclusiveBetween(0, StockRules.MaxQuantity);
    }
}
