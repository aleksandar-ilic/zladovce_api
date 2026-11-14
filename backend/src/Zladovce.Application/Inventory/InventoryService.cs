using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Inventory;

/// <summary>
/// The only place where stock changes. Every change is a conditional update that cannot take the stock
/// below zero, and writes an <c>inventory_movements</c> row with the resulting quantity.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Adds the stock record of a new variant (and its <c>InitialStock</c> movement when the quantity is above zero)
    /// to the current unit of work. The caller saves, so it commits together with the variant.
    /// </summary>
    void RegisterVariant(Guid variantId, int initialStock, int lowStockThreshold);

    /// <summary>Current stock of the given variants. Variants without a stock record are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the quantities off stock for an order (movement <c>OrderPlaced</c>). All or nothing:
    /// when any variant has too little stock, <see cref="InsufficientStockException"/> lists every shortage and nothing is changed.
    /// Runs inside the caller's database transaction when there is one (the caller must then roll back on failure);
    /// otherwise in its own. Variants are processed in id order, so concurrent calls cannot deadlock.
    /// </summary>
    Task DeductAsync(IReadOnlyCollection<StockLine> lines, Guid? orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts quantities back on stock: <see cref="InventoryMovementType.OrderCancelled"/> (order rejected or cancelled
    /// before shipping) or <see cref="InventoryMovementType.ReturnRestock"/> (returned goods accepted by an administrator).
    /// Transaction rules are those of <see cref="DeductAsync"/>.
    /// </summary>
    Task ReturnAsync(IReadOnlyCollection<StockLine> lines, InventoryMovementType movementType, Guid? orderId, CancellationToken cancellationToken);

    Task<PagedResult<InventoryItemResponse>> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Manual correction by the current administrator: stock, movement and audit entry in one transaction.</summary>
    Task<InventoryItemResponse> AdjustAsync(Guid variantId, AdjustStockRequest request, CancellationToken cancellationToken);

    Task<InventoryItemResponse> SetLowStockThresholdAsync(Guid variantId, SetLowStockThresholdRequest request, CancellationToken cancellationToken);

    Task<PagedResult<InventoryMovementResponse>> GetMovementsAsync(Guid variantId, PageRequest page, CancellationToken cancellationToken);

    Task<LowStockResponse> GetLowStockAsync(int limit, CancellationToken cancellationToken);
}

public sealed class InventoryService(
    IZladovceDbContext db,
    IRequestContext requestContext,
    IAuditLogger audit,
    TimeProvider timeProvider) : IInventoryService
{
    public const string AuditEntityType = "Inventory";
    public const string AdjustedAction = "Inventory.Adjusted";

    private sealed record StockChange(Guid VariantId, int Delta, int QuantityAfter);

    public void RegisterVariant(Guid variantId, int initialStock, int lowStockThreshold)
    {
        var now = timeProvider.GetUtcNow();
        db.Inventory.Add(InventoryItem.Create(variantId, initialStock, lowStockThreshold, now));

        if (initialStock > 0)
        {
            db.InventoryMovements.Add(InventoryMovement.Create(
                variantId, InventoryMovementType.InitialStock, initialStock, initialStock, reason: null, orderId: null, requestContext.Admin?.Id, now));
        }
    }

    public async Task<IReadOnlyDictionary<Guid, StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, StockLevel>();
        }

        return await db.Inventory
            .AsNoTracking()
            .Where(i => variantIds.Contains(i.VariantId))
            .Select(i => new StockLevel(i.VariantId, i.QuantityOnHand, i.LowStockThreshold))
            .ToDictionaryAsync(l => l.VariantId, cancellationToken);
    }

    public Task DeductAsync(IReadOnlyCollection<StockLine> lines, Guid? orderId, CancellationToken cancellationToken) =>
        ChangeAsync(lines, sign: -1, InventoryMovementType.OrderPlaced, reason: null, orderId, adminUserId: null, cancellationToken);

    public Task ReturnAsync(IReadOnlyCollection<StockLine> lines, InventoryMovementType movementType, Guid? orderId, CancellationToken cancellationToken)
    {
        if (movementType is not (InventoryMovementType.OrderCancelled or InventoryMovementType.ReturnRestock))
        {
            throw new ArgumentOutOfRangeException(nameof(movementType), movementType, "A return is OrderCancelled or ReturnRestock.");
        }

        return ChangeAsync(lines, sign: 1, movementType, reason: null, orderId, requestContext.Admin?.Id, cancellationToken);
    }

    public async Task<InventoryItemResponse> AdjustAsync(Guid variantId, AdjustStockRequest request, CancellationToken cancellationToken)
    {
        await EnsureVariantAsync(variantId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        IReadOnlyList<StockChange> changes;
        try
        {
            changes = await ChangeAsync(
                [new StockLine(variantId, Math.Abs(request.QuantityDelta))],
                Math.Sign(request.QuantityDelta),
                InventoryMovementType.ManualAdjustment,
                request.Reason,
                orderId: null,
                requestContext.Admin?.Id,
                cancellationToken);
        }
        catch (InsufficientStockException exception)
        {
            var available = exception.Shortages.Single().Available;
            throw new InsufficientStockException(
                exception.Shortages,
                $"Trenutno na stanju: {available}. Stanje ne može biti manje od nule.");
        }

        var change = changes.Single();
        audit.Record(
            AdjustedAction,
            AuditEntityType,
            variantId.ToString(),
            new
            {
                quantityOnHand = new { old = change.QuantityAfter - change.Delta, @new = change.QuantityAfter },
                quantityDelta = change.Delta,
                reason = request.Reason!.Trim(),
            });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetItemAsync(variantId, cancellationToken);
    }

    public async Task<InventoryItemResponse> SetLowStockThresholdAsync(Guid variantId, SetLowStockThresholdRequest request, CancellationToken cancellationToken)
    {
        await EnsureVariantAsync(variantId, cancellationToken);

        var item = await db.Inventory.SingleOrDefaultAsync(i => i.VariantId == variantId, cancellationToken)
            ?? throw new NotFoundException();

        // Tracked change: the audit interceptor records the old and the new threshold.
        item.SetLowStockThreshold(request.LowStockThreshold, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return await GetItemAsync(variantId, cancellationToken);
    }

    public async Task<PagedResult<InventoryItemResponse>> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken)
    {
        var query = Rows();

        if (DbSearch.ContainsPattern(criteria.Search) is { } pattern)
        {
            query = query.Where(r =>
                EF.Functions.Like(DbSearch.Unaccent(r.ProductName).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(r.VariantName).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(r.Sku).ToLower(), DbSearch.Unaccent(pattern).ToLower()));
        }

        query = criteria.Status switch
        {
            StockStatus.Out => query.Where(r => r.QuantityOnHand <= 0),
            StockStatus.Low => query.Where(r => r.QuantityOnHand > 0 && r.QuantityOnHand <= r.LowStockThreshold),
            StockStatus.Ok => query.Where(r => r.QuantityOnHand > r.LowStockThreshold && r.QuantityOnHand > 0),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);

        var ordered = criteria.Sort switch
        {
            InventorySort.StockAscending => query.OrderBy(r => r.QuantityOnHand).ThenBy(r => r.ProductName),
            InventorySort.StockDescending => query.OrderByDescending(r => r.QuantityOnHand).ThenBy(r => r.ProductName),
            _ => query.OrderBy(r => r.ProductName).ThenBy(r => r.ProductId),
        };

        var rows = await ordered
            .ThenBy(r => r.VariantSortOrder).ThenBy(r => r.VariantId)
            .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize)
            .ToListAsync(cancellationToken);

        return PagedResult<InventoryItemResponse>.Create([.. rows.Select(ToResponse)], criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<PagedResult<InventoryMovementResponse>> GetMovementsAsync(Guid variantId, PageRequest page, CancellationToken cancellationToken)
    {
        if (!await db.ProductVariants.AnyAsync(v => v.Id == variantId, cancellationToken))
        {
            throw new NotFoundException();
        }

        var query = db.InventoryMovements.AsNoTracking().Where(m => m.VariantId == variantId);
        var total = await query.CountAsync(cancellationToken);

        var items = await (
            from movement in query
            join admin in db.AdminUsers on movement.AdminUserId equals (Guid?)admin.Id into admins
            from admin in admins.DefaultIfEmpty()
            orderby movement.CreatedAt descending, movement.Id descending
            select new InventoryMovementResponse(
                movement.Id,
                movement.MovementType,
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.Reason,
                movement.OrderId,
                movement.AdminUserId,
                admin != null ? admin.FullName : null,
                movement.CreatedAt))
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return PagedResult<InventoryMovementResponse>.Create(items, page.Page, page.PageSize, total);
    }

    public async Task<LowStockResponse> GetLowStockAsync(int limit, CancellationToken cancellationToken)
    {
        var query = Rows().Where(r => r.QuantityOnHand <= r.LowStockThreshold);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(r => r.QuantityOnHand).ThenBy(r => r.ProductName).ThenBy(r => r.VariantSortOrder).ThenBy(r => r.VariantId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return new LowStockResponse([.. rows.Select(ToResponse)], total);
    }

    /// <summary>
    /// Applies the same kind of change to several variants. Each variant is one conditional UPDATE that only
    /// succeeds when the result stays within 0..<see cref="StockRules.MaxQuantity"/>; the row lock taken by the
    /// UPDATE is held until the transaction ends, so the quantity read back right after it is this change's result.
    /// </summary>
    private async Task<IReadOnlyList<StockChange>> ChangeAsync(
        IReadOnlyCollection<StockLine> lines,
        int sign,
        InventoryMovementType movementType,
        string? reason,
        Guid? orderId,
        Guid? adminUserId,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        if (lines.Any(l => l.Quantity is <= 0 or > StockRules.MaxQuantity))
        {
            throw new ArgumentException("Every stock line needs a quantity between 1 and the maximum.", nameof(lines));
        }

        // One change per variant, always in the same order: no deadlocks between concurrent orders.
        var changes = lines
            .GroupBy(l => l.VariantId)
            .Select(g => (VariantId: g.Key, Delta: sign * g.Sum(l => l.Quantity)))
            .OrderBy(c => c.VariantId)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await using var _ = transaction;

        var applied = new List<StockChange>(changes.Count);
        var movements = new List<InventoryMovement>(changes.Count);
        var shortages = new List<StockShortage>();

        foreach (var (variantId, delta) in changes)
        {
            var updated = await db.Inventory
                .Where(i => i.VariantId == variantId
                    && i.QuantityOnHand + delta >= 0
                    && i.QuantityOnHand + delta <= StockRules.MaxQuantity)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(i => i.QuantityOnHand, i => i.QuantityOnHand + delta)
                        .SetProperty(i => i.UpdatedAt, now),
                    cancellationToken);

            var quantity = await db.Inventory
                .AsNoTracking()
                .Where(i => i.VariantId == variantId)
                .Select(i => (int?)i.QuantityOnHand)
                .SingleOrDefaultAsync(cancellationToken);

            if (updated == 1)
            {
                applied.Add(new StockChange(variantId, delta, quantity!.Value));
                movements.Add(InventoryMovement.Create(variantId, movementType, delta, quantity.Value, reason, orderId, adminUserId, now));
            }
            else if (delta < 0 || quantity is null)
            {
                shortages.Add(new StockShortage(variantId, Math.Abs(delta), quantity ?? 0));
            }
            else
            {
                throw new RequestValidationException("quantityDelta", $"Stanje ne može biti veće od {StockRules.MaxQuantity}.");
            }
        }

        if (shortages.Count > 0)
        {
            // An owned transaction is rolled back when it is disposed; a caller's transaction is the caller's to roll back.
            throw new InsufficientStockException(shortages);
        }

        db.InventoryMovements.AddRange(movements);
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return applied;
    }

    private sealed class Row
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = null!;
        public string VariantName { get; init; } = null!;
        public string Sku { get; init; } = null!;
        public int VariantSortOrder { get; init; }
        public int QuantityOnHand { get; init; }
        public int LowStockThreshold { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    /// <summary>Variants that are not archived, of products that are not archived, with their stock.</summary>
    private IQueryable<Row> Rows() =>
        from variant in db.ProductVariants.AsNoTracking()
        join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
        join item in db.Inventory.AsNoTracking() on variant.Id equals item.VariantId
        where variant.DeletedAt == null && product.DeletedAt == null
        select new Row
        {
            VariantId = variant.Id,
            ProductId = product.Id,
            ProductName = product.Name,
            VariantName = variant.Name,
            Sku = variant.Sku,
            VariantSortOrder = variant.SortOrder,
            QuantityOnHand = item.QuantityOnHand,
            LowStockThreshold = item.LowStockThreshold,
            IsActive = product.IsActive && variant.IsActive,
            UpdatedAt = item.UpdatedAt,
        };

    private static InventoryItemResponse ToResponse(Row row) => new(
        row.VariantId,
        row.ProductId,
        row.ProductName,
        row.VariantName,
        row.Sku,
        row.QuantityOnHand,
        row.LowStockThreshold,
        StockRules.StatusOf(row.QuantityOnHand, row.LowStockThreshold),
        row.IsActive,
        row.UpdatedAt);

    private async Task<InventoryItemResponse> GetItemAsync(Guid variantId, CancellationToken cancellationToken) =>
        ToResponse(await Rows().SingleAsync(r => r.VariantId == variantId, cancellationToken));

    private async Task EnsureVariantAsync(Guid variantId, CancellationToken cancellationToken)
    {
        var exists = await (
            from variant in db.ProductVariants
            join product in db.Products on variant.ProductId equals product.Id
            where variant.Id == variantId && variant.DeletedAt == null && product.DeletedAt == null
            select variant.Id).AnyAsync(cancellationToken);

        if (!exists)
        {
            throw new NotFoundException();
        }
    }
}
