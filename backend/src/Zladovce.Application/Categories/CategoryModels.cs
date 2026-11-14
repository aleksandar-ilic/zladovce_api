using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Categories;

namespace Zladovce.Application.Categories;

/// <summary>Body of category create and update.</summary>
/// <param name="Name">Required, up to 120 characters.</param>
/// <param name="Slug">Optional. Create: generated from the name when empty. Update: empty keeps the current slug.</param>
/// <param name="Description">Optional plain text, up to 2000 characters.</param>
/// <param name="ImageKey">
/// Null removes the image. The current <c>image.key</c> keeps it. An upload key returned by
/// <c>POST /admin/media/upload-url</c> (purpose <c>category</c>) replaces it.
/// </param>
/// <param name="SortOrder">Position in lists, 0 or greater.</param>
/// <param name="IsActive">Inactive categories and their products are hidden from the shop.</param>
/// <param name="SeoTitle">Optional, up to 120 characters.</param>
/// <param name="SeoDescription">Optional, up to 320 characters.</param>
public sealed record SaveCategoryRequest(
    string? Name,
    string? Slug,
    string? Description,
    string? ImageKey,
    int SortOrder = 0,
    bool IsActive = true,
    string? SeoTitle = null,
    string? SeoDescription = null);

/// <summary>Body of <c>PUT /api/v1/admin/categories/order</c>.</summary>
/// <param name="Ids">Every category that is not archived, exactly once, in the new order.</param>
public sealed record ReorderCategoriesRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Image of a category.</summary>
/// <param name="Key">Storage key; send it back as <c>imageKey</c> to keep the image.</param>
/// <param name="BaseUrl">Folder of the renditions. A rendition is at <c>{baseUrl}/{width}.webp</c>.</param>
/// <param name="Widths">Available rendition widths in pixels, ascending.</param>
/// <param name="Width">Width of the original in pixels.</param>
/// <param name="Height">Height of the original in pixels.</param>
public sealed record CategoryImageResponse(string Key, string BaseUrl, IReadOnlyList<int> Widths, int Width, int Height)
{
    public static CategoryImageResponse? From(MediaOptions options, Category category) =>
        category is { ImageKey: { } key, ImageWidth: { } width, ImageHeight: { } height }
            ? new CategoryImageResponse(key, options.UrlFor(key), RenditionPlan.WidthsFor(width), width, height)
            : null;
}

/// <summary>A category as the administration sees it.</summary>
/// <param name="ProductCount">Products in the category that are not archived (active or not).</param>
public sealed record AdminCategoryResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    CategoryImageResponse? Image,
    int SortOrder,
    bool IsActive,
    string? SeoTitle,
    string? SeoDescription,
    int ProductCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminCategoryResponse From(MediaOptions options, Category category, int productCount) => new(
        category.Id,
        category.Name,
        category.Slug,
        category.Description,
        CategoryImageResponse.From(options, category),
        category.SortOrder,
        category.IsActive,
        category.SeoTitle,
        category.SeoDescription,
        productCount,
        category.CreatedAt,
        category.UpdatedAt);
}

/// <summary>All categories that are not archived, ordered by sort order and name.</summary>
public sealed record AdminCategoryListResponse(IReadOnlyList<AdminCategoryResponse> Items);

public sealed class SaveCategoryRequestValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(120);
        RuleFor(r => r.Slug).ValidSlug();
        RuleFor(r => r.Description).MaximumLength(2000);
        RuleFor(r => r.ImageKey).MaximumLength(200);
        RuleFor(r => r.SortOrder).InclusiveBetween(0, 100_000);
        RuleFor(r => r.SeoTitle).MaximumLength(120);
        RuleFor(r => r.SeoDescription).MaximumLength(320);
    }
}

public sealed class ReorderCategoriesRequestValidator : AbstractValidator<ReorderCategoriesRequest>
{
    public ReorderCategoriesRequestValidator()
    {
        RuleFor(r => r.Ids)
            .NotNull()
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Svaka kategorija sme da se navede samo jednom.");
    }
}
