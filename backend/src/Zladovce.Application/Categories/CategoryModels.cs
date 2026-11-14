using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Categories;

namespace Zladovce.Application.Categories;

/// <summary>
/// Body of category create and update. Every member must be sent (nullable ones may be <c>null</c>);
/// a missing member gives 400 <c>VALIDATION_FAILED</c>.
/// </summary>
public sealed record SaveCategoryRequest
{
    /// <summary>Required, up to 120 characters.</summary>
    public required string Name { get; init; }

    /// <summary>Create: <c>null</c> or empty generates the slug from the name. Update: <c>null</c> or empty keeps the current slug.</summary>
    public required string? Slug { get; init; }

    /// <summary>Plain text, up to 2000 characters; <c>null</c> = none.</summary>
    public required string? Description { get; init; }

    /// <summary>
    /// <c>null</c> removes the image. The current <c>image.key</c> keeps it. An upload key returned by
    /// <c>POST /admin/media/upload-url</c> (purpose <c>category</c>) replaces it.
    /// </summary>
    public required string? ImageKey { get; init; }

    /// <summary>Position in lists, 0 or greater.</summary>
    public required int SortOrder { get; init; }

    /// <summary>Inactive categories and their products are hidden from the shop.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Up to 120 characters; <c>null</c> = none.</summary>
    public required string? SeoTitle { get; init; }

    /// <summary>Up to 320 characters; <c>null</c> = none.</summary>
    public required string? SeoDescription { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/admin/categories/order</c>.</summary>
/// <param name="Ids">Every category that is not archived, exactly once, in the new order.</param>
public sealed record ReorderCategoriesRequest(IReadOnlyList<Guid> Ids);

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
