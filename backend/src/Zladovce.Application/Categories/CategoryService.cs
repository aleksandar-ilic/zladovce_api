using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Media;
using Zladovce.Domain.Categories;

namespace Zladovce.Application.Categories;

public interface ICategoryService
{
    Task<AdminCategoryListResponse> ListAsync(CancellationToken cancellationToken);
    Task<AdminCategoryResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminCategoryResponse> CreateAsync(SaveCategoryRequest request, CancellationToken cancellationToken);
    Task<AdminCategoryResponse> UpdateAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken);
    Task ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminCategoryListResponse> ReorderAsync(ReorderCategoriesRequest request, CancellationToken cancellationToken);
}

public sealed class CategoryService(
    IZladovceDbContext db,
    IImageProcessor images,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider timeProvider) : ICategoryService
{
    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<AdminCategoryListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var counts = await db.Products
            .Where(p => p.DeletedAt == null)
            .GroupBy(p => p.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count, cancellationToken);

        return new AdminCategoryListResponse(
            [.. categories.Select(c => AdminCategoryResponse.From(_media, c, counts.GetValueOrDefault(c.Id)))]);
    }

    public async Task<AdminCategoryResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, cancellationToken)
            ?? throw new NotFoundException();

        return AdminCategoryResponse.From(_media, category, await CountProductsAsync(id, cancellationToken));
    }

    public async Task<AdminCategoryResponse> CreateAsync(SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, candidate => SlugTakenAsync(candidate, null, cancellationToken));

        var category = Category.Create(
            request.Name!, slug, request.Description, request.SortOrder, request.IsActive, request.SeoTitle, request.SeoDescription, now);
        await ApplyImageAsync(category, request.ImageKey, now, cancellationToken);

        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);

        return AdminCategoryResponse.From(_media, category, 0);
    }

    public async Task<AdminCategoryResponse> UpdateAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await FindAsync(id, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var slug = category.Slug;
        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug != category.Slug)
        {
            slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, candidate => SlugTakenAsync(candidate, id, cancellationToken));
        }

        category.Update(request.Name!, slug, request.Description, request.SortOrder, request.IsActive, request.SeoTitle, request.SeoDescription, now);
        var replaced = await ApplyImageAsync(category, request.ImageKey, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        if (replaced is { } old)
        {
            await images.DeleteRenditionsAsync(old.Key, old.Width, cancellationToken);
        }

        return AdminCategoryResponse.From(_media, category, await CountProductsAsync(id, cancellationToken));
    }

    public async Task ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await FindAsync(id, cancellationToken);

        if (await CountProductsAsync(id, cancellationToken) > 0)
        {
            throw new ConflictException(
                ErrorTexts.CategoryHasProductsDetail, ErrorCodes.CategoryHasProducts, ErrorTexts.CategoryHasProductsTitle);
        }

        category.Archive(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdminCategoryListResponse> ReorderAsync(ReorderCategoriesRequest request, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.Where(c => c.DeletedAt == null).ToListAsync(cancellationToken);
        var ids = request.Ids!;

        if (ids.Count != categories.Count || ids.Any(id => categories.All(c => c.Id != id)))
        {
            throw new RequestValidationException("ids", "Redosled mora da sadrži sve kategorije, svaku tačno jednom.");
        }

        var now = timeProvider.GetUtcNow();
        for (var position = 0; position < ids.Count; position++)
        {
            categories.First(c => c.Id == ids[position]).SetSortOrder(position, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ListAsync(cancellationToken);
    }

    /// <summary>Applies the <c>imageKey</c> of a request. Returns the image that was replaced or removed, to delete after saving.</summary>
    private async Task<(string Key, int Width)?> ApplyImageAsync(
        Category category, string? imageKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        (string Key, int Width)? previous = category is { ImageKey: { } key, ImageWidth: { } width } ? (key, width) : null;

        if (string.IsNullOrWhiteSpace(imageKey))
        {
            category.RemoveImage(now);
            return previous;
        }

        if (imageKey == category.ImageKey)
        {
            return null;
        }

        var image = await images.GetProcessedAsync(imageKey, MediaPurpose.Category, "imageKey", cancellationToken);
        if (await db.Categories.AnyAsync(c => c.ImageKey == image.StorageKey && c.Id != category.Id, cancellationToken))
        {
            throw new ConflictException(
                ErrorTexts.ImageAlreadyAttachedDetail, ErrorCodes.ImageAlreadyAttached, ErrorTexts.ImageAlreadyAttachedTitle);
        }

        category.SetImage(image.StorageKey, image.Width, image.Height, now);
        return previous;
    }

    private async Task<Category> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, cancellationToken)
        ?? throw new NotFoundException();

    private Task<int> CountProductsAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Products.CountAsync(p => p.CategoryId == categoryId && p.DeletedAt == null, cancellationToken);

    private Task<bool> SlugTakenAsync(string slug, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Categories.AnyAsync(c => c.Slug == slug && c.DeletedAt == null && c.Id != exceptId, cancellationToken);
}
