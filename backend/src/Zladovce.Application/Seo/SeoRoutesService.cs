using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;
using Zladovce.Application.Content;

namespace Zladovce.Application.Seo;

/// <summary>A page of the shop: its slug and when its content last changed (for prerendering and <c>lastmod</c> in the sitemap).</summary>
public sealed record SeoRoute(string Slug, DateTimeOffset UpdatedAt);

/// <summary>
/// Response of <c>GET /api/v1/seo/routes</c>: every page the storefront prerenders, with the same visibility as the public
/// catalog. Sorted by slug.
/// </summary>
/// <param name="Categories">Visible categories (<c>/prodavnica/{slug}</c> in the shop).</param>
/// <param name="Products">Visible products (<c>/proizvod/{slug}</c> in the shop).</param>
/// <param name="BlogPosts">Public blog posts (Published, publication time reached, not archived).</param>
/// <param name="BlogCategories">Blog categories with at least one public post; <c>updatedAt</c> is the later of the category change and its newest public post.</param>
/// <param name="Pages">CMS pages that have content, by key (<c>home</c>, <c>about</c>, <c>privacy</c>, <c>terms</c>, <c>delivery</c>); a page without content has no route.</param>
public sealed record SeoRoutesResponse(
    IReadOnlyList<SeoRoute> Categories,
    IReadOnlyList<SeoRoute> Products,
    IReadOnlyList<SeoRoute> BlogPosts,
    IReadOnlyList<SeoRoute> BlogCategories,
    IReadOnlyList<SeoPageRoute> Pages);

/// <summary>A CMS page with content.</summary>
public sealed record SeoPageRoute(string Key, DateTimeOffset UpdatedAt);

public interface ISeoRoutesService
{
    Task<SeoRoutesResponse> GetRoutesAsync(CancellationToken cancellationToken);
}

public sealed class SeoRoutesService(IZladovceDbContext db, TimeProvider timeProvider) : ISeoRoutesService
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

        var now = timeProvider.GetUtcNow();
        var posts = await BlogVisibility.PublicPosts(db, now)
            .Select(p => new { p.Slug, p.UpdatedAt, p.PublishedAt, p.CategoryId })
            .ToListAsync(cancellationToken);
        var blogCategories = await db.BlogCategories.AsNoTracking().ToListAsync(cancellationToken);
        var pages = await db.CmsPages.AsNoTracking()
            .Where(p => p.ContentJson != null)
            .Select(p => new SeoPageRoute(p.Key, p.UpdatedAt))
            .ToListAsync(cancellationToken);

        // A post counts as changed when it became public, also when that was a scheduled time.
        var blogPosts = posts
            .Select(p => new SeoRoute(p.Slug, p.PublishedAt > p.UpdatedAt ? p.PublishedAt.Value : p.UpdatedAt))
            .OrderBy(r => r.Slug, StringComparer.Ordinal)
            .ToList();
        var categoryRoutes = blogCategories
            .Select(c => (Category: c, Posts: posts.Where(p => p.CategoryId == c.Id).ToList()))
            .Where(x => x.Posts.Count > 0)
            .Select(x => new SeoRoute(x.Category.Slug, new[] { x.Category.UpdatedAt }.Concat(x.Posts.Select(p => p.PublishedAt > p.UpdatedAt ? p.PublishedAt.Value : p.UpdatedAt)).Max()))
            .OrderBy(r => r.Slug, StringComparer.Ordinal)
            .ToList();

        return new SeoRoutesResponse(categories, products, blogPosts, categoryRoutes, [.. pages.OrderBy(p => p.Key, StringComparer.Ordinal)]);
    }
}
