using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;

namespace Zladovce.Application.Catalog;

/// <summary>
/// The visibility rule of the shop, shared by the public catalog, the cart quote and the SEO routes:
/// a category is visible when it is active and not archived; a product when it is active, not archived, in a visible
/// category and has at least one active, not archived variant.
/// </summary>
public static class CatalogVisibility
{
    public static IQueryable<Category> VisibleCategories(IZladovceDbContext db) =>
        db.Categories.AsNoTracking().Where(c => c.IsActive && c.DeletedAt == null);

    public static IQueryable<Product> VisibleProducts(IZladovceDbContext db) =>
        db.Products
            .AsNoTracking()
            .Where(p => p.IsActive
                && p.DeletedAt == null
                && db.Categories.Any(c => c.Id == p.CategoryId && c.IsActive && c.DeletedAt == null)
                && p.Variants.Any(v => v.IsActive && v.DeletedAt == null));
}
