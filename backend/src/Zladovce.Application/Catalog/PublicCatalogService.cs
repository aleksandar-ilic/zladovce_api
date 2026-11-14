using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Inventory;
using Zladovce.Application.Media;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Catalog;

/// <summary>
/// Read side of the shop. Only products that are active, not archived, in an active category and with at least one
/// active variant are visible; exact stock figures never leave this service.
/// </summary>
public interface IPublicCatalogService
{
    Task<PublicCategoryListResponse> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<PublicCategoryDetailResponse> GetCategoryAsync(string slug, CancellationToken cancellationToken);
    Task<PagedResult<PublicProductListItem>> GetProductsAsync(PublicProductListCriteria criteria, CancellationToken cancellationToken);
    Task<PublicProductResponse> GetProductAsync(string slug, CancellationToken cancellationToken);
    Task<PublicProductListResponse> GetRelatedProductsAsync(string slug, int limit, CancellationToken cancellationToken);
    Task<PublicAvailabilityResponse> GetAvailabilityAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

public sealed class PublicCatalogService(
    IZladovceDbContext db,
    IInventoryService inventory,
    IOptions<MediaOptions> mediaOptions,
    IOptions<CatalogOptions> catalogOptions) : IPublicCatalogService
{
    private readonly MediaOptions _media = mediaOptions.Value;
    private readonly int _maxOrderQuantity = catalogOptions.Value.MaxOrderQuantity;

    public async Task<PublicCategoryListResponse> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => c.IsActive && c.DeletedAt == null)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var counts = await VisibleProducts()
            .GroupBy(p => p.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count, cancellationToken);

        return new PublicCategoryListResponse([.. categories.Select(c => new PublicCategoryResponse(
            c.Id, c.Name, c.Slug, c.Description, ToImage(c), counts.GetValueOrDefault(c.Id)))]);
    }

    public async Task<PublicCategoryDetailResponse> GetCategoryAsync(string slug, CancellationToken cancellationToken)
    {
        var category = await db.Categories
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Slug == slug && c.IsActive && c.DeletedAt == null, cancellationToken)
            ?? throw new NotFoundException();

        var count = await VisibleProducts().CountAsync(p => p.CategoryId == category.Id, cancellationToken);

        return new PublicCategoryDetailResponse(
            category.Id, category.Name, category.Slug, category.Description, ToImage(category), count,
            new PublicSeo(category.SeoTitle, category.SeoDescription));
    }

    public async Task<PagedResult<PublicProductListItem>> GetProductsAsync(PublicProductListCriteria criteria, CancellationToken cancellationToken)
    {
        var products = VisibleProducts();

        if (!string.IsNullOrWhiteSpace(criteria.CategorySlug))
        {
            var slug = criteria.CategorySlug.Trim();
            products = products.Where(p => db.Categories.Any(c => c.Id == p.CategoryId && c.Slug == slug));
        }

        if (DbSearch.ContainsPattern(criteria.Search) is { } pattern)
        {
            products = products.Where(p => EF.Functions.Like(DbSearch.Unaccent(p.Name).ToLower(), DbSearch.Unaccent(pattern).ToLower()));
        }

        if (criteria.Featured is { } featured)
        {
            products = products.Where(p => p.IsFeatured == featured);
        }

        // The listing price in SQL: the lowest effective price of the variants on sale (same rule as ListingPrice in the domain).
        var priced = products.Select(p => new
        {
            p.Id,
            p.Name,
            p.CreatedAt,
            Price = p.Variants.Where(v => v.IsActive && v.DeletedAt == null).Min(v => v.DiscountPrice ?? v.RegularPrice),
            InStock = p.Variants.Any(v => v.IsActive && v.DeletedAt == null
                && db.Inventory.Any(i => i.VariantId == v.Id && i.QuantityOnHand > 0)),
        });

        if (criteria.MinPrice is { } minPrice)
        {
            priced = priced.Where(p => p.Price >= minPrice);
        }

        if (criteria.MaxPrice is { } maxPrice)
        {
            priced = priced.Where(p => p.Price <= maxPrice);
        }

        if (criteria.InStock == true)
        {
            priced = priced.Where(p => p.InStock);
        }

        var total = await priced.CountAsync(cancellationToken);

        var ordered = criteria.Sort switch
        {
            PublicProductSort.PriceAscending => priced.OrderBy(p => p.Price).ThenBy(p => p.Name).ThenBy(p => p.Id),
            PublicProductSort.PriceDescending => priced.OrderByDescending(p => p.Price).ThenBy(p => p.Name).ThenBy(p => p.Id),
            PublicProductSort.Name => priced.OrderBy(p => p.Name).ThenBy(p => p.Id),

            // Bestselling needs order data (Phase 5); until then it is the newest-first order.
            _ => priced.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
        };

        var ids = await ordered
            .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var items = await LoadListItemsAsync(ids, cancellationToken);
        return PagedResult<PublicProductListItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<PublicProductResponse> GetProductAsync(string slug, CancellationToken cancellationToken)
    {
        var product = await VisibleProducts()
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .SingleOrDefaultAsync(p => p.Slug == slug, cancellationToken)
            ?? throw new NotFoundException();

        var category = await db.Categories.AsNoTracking()
            .Where(c => c.Id == product.CategoryId)
            .Select(c => new PublicProductCategory(c.Name, c.Slug))
            .SingleAsync(cancellationToken);

        var variants = SellableVariants(product);
        var stock = await inventory.GetStockAsync([.. variants.Select(v => v.Id)], cancellationToken);
        var price = ListingPrice.From(variants.Select(v => (v.RegularPrice, v.DiscountPrice)))!;

        var variantResponses = variants.Select(v =>
        {
            var quantity = stock.GetValueOrDefault(v.Id)?.QuantityOnHand ?? 0;
            return new PublicVariantResponse(
                v.Id, v.Name, v.Sku, v.UnitAmount, v.MeasurementUnit, v.EffectivePrice, v.RegularPrice, v.DiscountPrice,
                quantity > 0, StockRules.MaxOrderQuantity(quantity, _maxOrderQuantity));
        }).ToList();

        return new PublicProductResponse(
            product.Id,
            product.Name,
            product.Slug,
            product.ShortDescription,
            product.Description,
            category,
            product.IsFeatured,
            price.Price,
            price.RegularPrice,
            price.HasPriceRange,
            variantResponses.Any(v => v.InStock),
            variantResponses,
            [.. product.OrderedImages.OrderByDescending(i => i.IsMain).Select(i => PublicImageResponse.From(_media, i))],
            new PublicSeo(product.SeoTitle, product.SeoDescription),
            product.UpdatedAt);
    }

    public async Task<PublicProductListResponse> GetRelatedProductsAsync(string slug, int limit, CancellationToken cancellationToken)
    {
        var product = await VisibleProducts()
            .Where(p => p.Slug == slug)
            .Select(p => new { p.Id, p.CategoryId })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        var ids = await VisibleProducts()
            .Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id)
            .OrderByDescending(p => p.IsFeatured).ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(limit)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        return new PublicProductListResponse(await LoadListItemsAsync(ids, cancellationToken));
    }

    public async Task<PublicAvailabilityResponse> GetAvailabilityAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new PublicAvailabilityResponse([]);
        }

        var visibleProductIds = VisibleProducts().Select(p => p.Id);
        var variants = await db.ProductVariants
            .AsNoTracking()
            .Where(v => variantIds.Contains(v.Id) && v.IsActive && v.DeletedAt == null && visibleProductIds.Contains(v.ProductId))
            .OrderBy(v => v.Id)
            .ToListAsync(cancellationToken);

        var stock = await inventory.GetStockAsync([.. variants.Select(v => v.Id)], cancellationToken);

        return new PublicAvailabilityResponse([.. variants.Select(v =>
        {
            var quantity = stock.GetValueOrDefault(v.Id)?.QuantityOnHand ?? 0;
            return new PublicAvailabilityItem(
                v.Id, v.ProductId, v.EffectivePrice, v.RegularPrice, v.DiscountPrice,
                quantity > 0, StockRules.MaxOrderQuantity(quantity, _maxOrderQuantity));
        })]);
    }

    /// <summary>The visibility rule of the shop.</summary>
    private IQueryable<Product> VisibleProducts() =>
        db.Products
            .AsNoTracking()
            .Where(p => p.IsActive
                && p.DeletedAt == null
                && db.Categories.Any(c => c.Id == p.CategoryId && c.IsActive && c.DeletedAt == null)
                && p.Variants.Any(v => v.IsActive && v.DeletedAt == null));

    private static List<ProductVariant> SellableVariants(Product product) =>
        [.. product.CurrentVariants.Where(v => v.IsSellable)];

    /// <summary>Loads grid items for already selected (visible) product ids, keeping their order.</summary>
    private async Task<IReadOnlyList<PublicProductListItem>> LoadListItemsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var products = await db.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var categoryIds = products.Values.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new PublicProductCategory(c.Name, c.Slug), cancellationToken);

        var stock = await inventory.GetStockAsync(
            [.. products.Values.SelectMany(SellableVariants).Select(v => v.Id)], cancellationToken);

        var items = new List<PublicProductListItem>(ids.Count);
        foreach (var id in ids)
        {
            if (!products.TryGetValue(id, out var product))
            {
                continue;
            }

            var variants = SellableVariants(product);
            if (ListingPrice.From(variants.Select(v => (v.RegularPrice, v.DiscountPrice))) is not { } price)
            {
                continue;
            }

            items.Add(new PublicProductListItem(
                product.Id,
                product.Name,
                product.Slug,
                product.ShortDescription,
                categories[product.CategoryId],
                product.IsFeatured,
                price.Price,
                price.RegularPrice,
                price.HasPriceRange,
                variants.Any(v => (stock.GetValueOrDefault(v.Id)?.QuantityOnHand ?? 0) > 0),
                product.MainImage is { } main ? PublicImageResponse.From(_media, main) : null));
        }

        return items;
    }

    private PublicImageResponse? ToImage(Category category) =>
        category is { ImageKey: { } key, ImageWidth: { } width, ImageHeight: { } height }
            ? new PublicImageResponse(_media.UrlFor(key), RenditionPlan.WidthsFor(width), width, height, category.Name)
            : null;
}
