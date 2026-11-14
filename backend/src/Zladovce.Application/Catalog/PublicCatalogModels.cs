using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Catalog;

namespace Zladovce.Application.Catalog;

/// <summary>Configuration section <c>Catalog</c>.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>Largest quantity of one variant a customer may order at once, whatever the stock.</summary>
    public int MaxOrderQuantity { get; set; } = 20;
}

public enum PublicProductSort
{
    Newest,
    PriceAscending,
    PriceDescending,
    Name,

    /// <summary>Accepted for forward compatibility; ordered like <see cref="Newest"/> until orders exist (Phase 5).</summary>
    Bestselling,
}

/// <summary>Filter of the public product grid.</summary>
public sealed record PublicProductListCriteria(
    string? CategorySlug,
    string? Search,
    decimal? MinPrice,
    decimal? MaxPrice,
    bool? InStock,
    bool? Featured,
    PublicProductSort Sort,
    PageRequest Page);

/// <summary>An image for the shop.</summary>
/// <param name="BaseUrl">Folder of the renditions. A rendition is at <c>{baseUrl}/{width}.webp</c>.</param>
/// <param name="Widths">Available rendition widths in pixels, ascending; use them for <c>srcset</c>.</param>
/// <param name="Width">Width of the original in pixels (for the aspect ratio).</param>
/// <param name="Height">Height of the original in pixels.</param>
public sealed record PublicImageResponse(string BaseUrl, IReadOnlyList<int> Widths, int Width, int Height, string? AltText)
{
    public static PublicImageResponse From(MediaOptions options, ProductImage image) =>
        Create(options, image.StorageKey, image.Width, image.Height, image.AltText);

    public static PublicImageResponse Create(MediaOptions options, string storageKey, int width, int height, string? altText) =>
        new(options.UrlFor(storageKey), RenditionPlan.WidthsFor(width), width, height, altText);
}

public sealed record PublicSeo(string? Title, string? Description);

/// <summary>An active category of the shop.</summary>
/// <param name="ProductCount">Products of the category that are visible in the shop.</param>
public sealed record PublicCategoryResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    PublicImageResponse? Image,
    int ProductCount);

public sealed record PublicCategoryListResponse(IReadOnlyList<PublicCategoryResponse> Items);

/// <summary>A category with its SEO data.</summary>
public sealed record PublicCategoryDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    PublicImageResponse? Image,
    int ProductCount,
    PublicSeo Seo);

public sealed record PublicProductCategory(string Name, string Slug);

/// <summary>A product in a grid.</summary>
/// <param name="Price">Lowest price among the variants on sale (RSD).</param>
/// <param name="OriginalPrice">Regular price of that cheapest variant when it is discounted; otherwise null.</param>
/// <param name="HasPriceRange">True when variants have different prices: show the price as "od …".</param>
/// <param name="InStock">True when at least one variant can be bought.</param>
/// <param name="Image">Main image, when the product has images.</param>
public sealed record PublicProductListItem(
    Guid Id,
    string Name,
    string Slug,
    string ShortDescription,
    PublicProductCategory Category,
    bool IsFeatured,
    decimal Price,
    decimal? OriginalPrice,
    bool HasPriceRange,
    bool InStock,
    PublicImageResponse? Image);

/// <summary>Related products of a product.</summary>
public sealed record PublicProductListResponse(IReadOnlyList<PublicProductListItem> Items);

/// <summary>A variant on sale.</summary>
/// <param name="Price">The price to pay (RSD): the discount price when there is one, otherwise the regular price.</param>
/// <param name="RegularPrice">Regular price.</param>
/// <param name="DiscountPrice">Discounted price, when there is one.</param>
/// <param name="InStock">Whether the variant can be bought now.</param>
/// <param name="MaxOrderQuantity">Largest quantity that can be ordered now; 0 when out of stock. The exact stock is not published.</param>
public sealed record PublicVariantResponse(
    Guid Id,
    string Name,
    string Sku,
    decimal UnitAmount,
    MeasurementUnit MeasurementUnit,
    decimal Price,
    decimal RegularPrice,
    decimal? DiscountPrice,
    bool InStock,
    int MaxOrderQuantity);

/// <summary>Product page data.</summary>
/// <param name="Description">Sanitised HTML.</param>
/// <param name="Price">Lowest price among the variants (RSD).</param>
/// <param name="OriginalPrice">Regular price of the cheapest variant when it is discounted; otherwise null.</param>
/// <param name="HasPriceRange">True when variants have different prices.</param>
/// <param name="InStock">True when at least one variant can be bought.</param>
/// <param name="Variants">Variants on sale, in display order.</param>
/// <param name="Images">Images in display order; the main image has <c>isMain</c> in the administration and comes first here.</param>
public sealed record PublicProductResponse(
    Guid Id,
    string Name,
    string Slug,
    string ShortDescription,
    string Description,
    PublicProductCategory Category,
    bool IsFeatured,
    decimal Price,
    decimal? OriginalPrice,
    bool HasPriceRange,
    bool InStock,
    IReadOnlyList<PublicVariantResponse> Variants,
    IReadOnlyList<PublicImageResponse> Images,
    PublicSeo Seo,
    DateTimeOffset UpdatedAt);

/// <summary>Live price and availability of one variant.</summary>
public sealed record PublicAvailabilityItem(
    Guid VariantId,
    Guid ProductId,
    decimal Price,
    decimal RegularPrice,
    decimal? DiscountPrice,
    bool InStock,
    int MaxOrderQuantity);

/// <summary>Live price and availability. A requested variant that is missing from <see cref="Items"/> is no longer on sale.</summary>
public sealed record PublicAvailabilityResponse(IReadOnlyList<PublicAvailabilityItem> Items);
