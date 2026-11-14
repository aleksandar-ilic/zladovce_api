using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;

namespace Zladovce.Application.Seo;

/// <summary>A page of the shop: its slug and when its content last changed (for prerendering and <c>lastmod</c> in the sitemap).</summary>
public sealed record SeoRoute(string Slug, DateTimeOffset UpdatedAt);

/// <summary>
/// Response of <c>GET /api/v1/seo/routes</c>: every page the storefront prerenders, with the same visibility as the public
/// catalog. Sorted by slug.
/// </summary>
/// <param name="Categories">Visible categories (<c>/prodavnica/{slug}</c> in the shop).</param>
/// <param name="Products">Visible products (<c>/proizvod/{slug}</c> in the shop).</param>
public sealed record SeoRoutesResponse(IReadOnlyList<SeoRoute> Categories, IReadOnlyList<SeoRoute> Products);

public interface ISeoRoutesService
{
    Task<SeoRoutesResponse> GetRoutesAsync(CancellationToken cancellationToken);
}

public sealed class SeoRoutesService(IZladovceDbContext db) : ISeoRoutesService
{
    public async Task<SeoRoutesResponse> GetRoutesAsync(CancellationToken cancellationToken)
    {
        var categories = await CatalogVisibility.VisibleCategories(db)
            .OrderBy(c => c.Slug)
            .Select(c => new SeoRoute(c.Slug, c.UpdatedAt))
            .ToListAsync(cancellationToken);

        var products = await CatalogVisibility.VisibleProducts(db)
            .OrderBy(p => p.Slug)
            .Select(p => new SeoRoute(p.Slug, p.UpdatedAt))
            .ToListAsync(cancellationToken);

        // Phase 7 (content): published blog posts and blog categories are added here as new members of the response
        // (for example `blogPosts`), with the same { slug, updatedAt } shape. Static pages are known to the storefront.
        return new SeoRoutesResponse(categories, products);
    }
}
