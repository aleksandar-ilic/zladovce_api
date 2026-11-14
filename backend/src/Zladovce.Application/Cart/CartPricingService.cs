using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Cart;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Common;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.Cart;

/// <summary>
/// Prices a cart from the database: current variant prices, availability, the order cap and the shipping and minimum-order
/// rules of the store settings. Used by <c>POST /api/v1/cart/quote</c> and, from Phase 5, by checkout inside its order
/// transaction, so that the shop and the order apply exactly the same rules. Free of HTTP concerns; never trusts prices
/// from the client.
/// </summary>
public interface ICartPricingService
{
    /// <summary>
    /// Prices <paramref name="items"/>. Duplicate variants are merged. Uses two queries: all requested variants (with product,
    /// category, main image and stock) and the store settings. Joins the caller's transaction when there is one.
    /// </summary>
    Task<CartQuoteResponse> QuoteAsync(IReadOnlyCollection<CartItem> items, CancellationToken cancellationToken);
}

public sealed class CartPricingService(
    IZladovceDbContext db,
    IOptions<MediaOptions> mediaOptions,
    IOptions<CatalogOptions> catalogOptions) : ICartPricingService
{
    private readonly MediaOptions _media = mediaOptions.Value;
    private readonly int _maxOrderQuantity = catalogOptions.Value.MaxOrderQuantity;

    public async Task<CartQuoteResponse> QuoteAsync(IReadOnlyCollection<CartItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var merged = CartQuoteCalculator.Merge(items);
        var variants = merged.Count == 0
            ? new Dictionary<Guid, CartVariantSnapshot>()
            : await LoadVariantsAsync([.. merged.Select(i => i.VariantId)], cancellationToken);

        // The row is created by the first migration; its absence is a deployment fault, not a client error.
        var settings = await db.StoreSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == StoreSettingsRecord.SingletonId, cancellationToken)
            ?? throw new InvalidOperationException("The store_settings row is missing. Apply the database migrations.");

        return CartQuoteCalculator.Calculate(merged, variants, settings.ShippingTerms, _maxOrderQuantity);
    }

    /// <summary>One query: the requested variants with everything needed to price and show them.</summary>
    private async Task<Dictionary<Guid, CartVariantSnapshot>> LoadVariantsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var rows = await (
                from v in db.ProductVariants.AsNoTracking()
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                where ids.Contains(v.Id)
                select new
                {
                    v.Id,
                    v.ProductId,
                    ProductName = p.Name,
                    ProductSlug = p.Slug,
                    v.Name,
                    v.Sku,
                    v.UnitAmount,
                    v.MeasurementUnit,
                    v.RegularPrice,
                    v.DiscountPrice,
                    IsSellable = v.IsActive
                        && v.DeletedAt == null
                        && p.IsActive
                        && p.DeletedAt == null
                        && db.Categories.Any(c => c.Id == p.CategoryId && c.IsActive && c.DeletedAt == null),
                    Stock = db.Inventory.Where(i => i.VariantId == v.Id).Select(i => (int?)i.QuantityOnHand).FirstOrDefault(),
                    Image = db.ProductImages
                        .Where(i => i.ProductId == p.Id && i.IsMain)
                        .Select(i => new { i.StorageKey, i.Width, i.Height, i.AltText })
                        .FirstOrDefault(),
                })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            r => r.Id,
            r => new CartVariantSnapshot(
                r.Id,
                r.ProductId,
                r.ProductName,
                r.ProductSlug,
                r.Name,
                r.Sku,
                r.UnitAmount,
                r.MeasurementUnit,
                r.RegularPrice,
                r.DiscountPrice,
                r.Image is { } image ? PublicImageResponse.Create(_media, image.StorageKey, image.Width, image.Height, image.AltText) : null,
                r.IsSellable,
                r.Stock ?? 0));
    }
}

/// <summary>The pure part of the cart pricing (no database), unit-tested on its own.</summary>
public static class CartQuoteCalculator
{
    /// <summary>Adds up lines of the same variant, keeping the position of the first one and its first known price.</summary>
    public static IReadOnlyList<CartItem> Merge(IEnumerable<CartItem> items)
    {
        var merged = new List<CartItem>();
        var positions = new Dictionary<Guid, int>();

        foreach (var item in items)
        {
            if (item.Quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(items), "Every cart quantity must be positive.");
            }

            if (positions.TryGetValue(item.VariantId, out var index))
            {
                var existing = merged[index];
                merged[index] = existing with
                {
                    Quantity = checked(existing.Quantity + item.Quantity),
                    KnownUnitPrice = existing.KnownUnitPrice ?? item.KnownUnitPrice,
                };
            }
            else
            {
                positions[item.VariantId] = merged.Count;
                merged.Add(item);
            }
        }

        return merged;
    }

    /// <param name="items">Merged items (see <see cref="Merge"/>).</param>
    /// <param name="variants">Snapshots of the variants that exist, by id.</param>
    /// <param name="terms">Shipping and minimum-order rules.</param>
    /// <param name="maxOrderQuantityCap">Shop cap of one variant per order (<c>Catalog:MaxOrderQuantity</c>).</param>
    public static CartQuoteResponse Calculate(
        IReadOnlyList<CartItem> items,
        IReadOnlyDictionary<Guid, CartVariantSnapshot> variants,
        ShippingTerms terms,
        int maxOrderQuantityCap)
    {
        var lines = new List<CartQuoteLine>(items.Count);
        var adjustments = new List<CartQuoteAdjustment>();

        foreach (var item in items)
        {
            var maxOrderQuantity = variants.TryGetValue(item.VariantId, out var variant) && variant.IsSellable
                ? StockRules.MaxOrderQuantity(variant.QuantityOnHand, maxOrderQuantityCap)
                : 0;

            if (variant is null || maxOrderQuantity == 0)
            {
                adjustments.Add(new CartQuoteAdjustment(item.VariantId, CartAdjustmentReason.Unavailable, item.Quantity, 0, null, null));
                continue;
            }

            var quantity = Math.Min(item.Quantity, maxOrderQuantity);
            if (quantity < item.Quantity)
            {
                // The stock decides when it is below the cap; otherwise the cap is what the customer runs into.
                var limitReason = variant.QuantityOnHand < maxOrderQuantityCap ? QuantityLimitReason.Stock : QuantityLimitReason.OrderLimit;
                adjustments.Add(new CartQuoteAdjustment(
                    item.VariantId, CartAdjustmentReason.QuantityReduced, item.Quantity, quantity, null, null, limitReason));
            }

            var unitPrice = Pricing.EffectivePrice(variant.RegularPrice, variant.DiscountPrice);
            if (item.KnownUnitPrice is { } known && known != unitPrice)
            {
                adjustments.Add(new CartQuoteAdjustment(item.VariantId, CartAdjustmentReason.PriceChanged, item.Quantity, quantity, known, unitPrice));
            }

            lines.Add(new CartQuoteLine(
                variant.VariantId,
                variant.ProductId,
                variant.ProductName,
                variant.ProductSlug,
                variant.VariantName,
                variant.Sku,
                variant.UnitAmount,
                variant.MeasurementUnit,
                variant.Image,
                unitPrice,
                variant.RegularPrice,
                variant.DiscountPrice,
                item.Quantity,
                quantity,
                Money.LineTotal(unitPrice, quantity),
                maxOrderQuantity));
        }

        var totals = CartTotals.Calculate(lines.Select(l => l.LineTotal), terms);

        return new CartQuoteResponse(
            lines,
            adjustments,
            totals.Subtotal,
            new CartQuoteShipping(
                totals.ShippingEnabled,
                totals.ShippingPrice,
                Money.Round(terms.Price),
                totals.FreeShippingThreshold,
                totals.IsShippingFree,
                totals.AmountUntilFreeShipping),
            totals.MinOrderAmount,
            totals.MeetsMinimumOrder,
            totals.AmountUntilMinimumOrder,
            totals.Total,
            totals.CanCheckout,
            Money.Currency);
    }
}
