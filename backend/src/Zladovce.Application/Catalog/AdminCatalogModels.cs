using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Catalog;

/// <summary>Values of a variant (body of variant update; also the base of variant creation).</summary>
public record VariantRequest
{
    /// <summary>Label of the variant, for example "370 g". Required, up to 120 characters.</summary>
    public string? Name { get; init; }

    /// <summary>Unique code of the variant. Required; letters, digits, dot, hyphen, underscore; up to 64 characters.</summary>
    public string? Sku { get; init; }

    /// <summary>Amount in the measurement unit, for example 370. Greater than zero, at most three decimals.</summary>
    public decimal? UnitAmount { get; init; }

    /// <summary><c>g</c>, <c>kg</c>, <c>ml</c>, <c>l</c> or <c>kom</c>.</summary>
    public MeasurementUnit? MeasurementUnit { get; init; }

    /// <summary>Regular price in RSD. Greater than zero, at most two decimals.</summary>
    public decimal? RegularPrice { get; init; }

    /// <summary>Optional discounted price in RSD; must be lower than the regular price.</summary>
    public decimal? DiscountPrice { get; init; }

    /// <summary>Inactive variants are not offered in the shop.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Position among the variants of the product, 0 or greater. Omitted: the position in the request (create product),
    /// after the existing variants (add variant), unchanged (update variant).
    /// </summary>
    public int? SortOrder { get; init; }

    public VariantValues ToValues(int sortOrder) => new(
        Name!, Sku!, UnitAmount!.Value, MeasurementUnit!.Value, RegularPrice!.Value, DiscountPrice, IsActive, SortOrder ?? sortOrder);
}

/// <summary>A new variant: its values plus the opening stock.</summary>
public sealed record CreateVariantRequest : VariantRequest
{
    /// <summary>Quantity on stock at creation, 0 or greater. Recorded as an <c>InitialStock</c> movement when above zero.</summary>
    public int InitialStock { get; init; }

    /// <summary>The variant counts as low on stock at or below this quantity.</summary>
    public int LowStockThreshold { get; init; }
}

/// <summary>Values of a product shared by create and update.</summary>
public abstract record ProductRequest
{
    /// <summary>Category of the product. Required; must not be archived.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Required, up to 200 characters.</summary>
    public string? Name { get; init; }

    /// <summary>Optional. Create: generated from the name when empty. Update: empty keeps the current slug.</summary>
    public string? Slug { get; init; }

    /// <summary>Plain text for listings, up to 500 characters.</summary>
    public string? ShortDescription { get; init; }

    /// <summary>HTML. Sanitised on the server: only basic formatting tags and links survive.</summary>
    public string? Description { get; init; }

    /// <summary>Base code of the product for internal reference. Required, unique among products.</summary>
    public string? Sku { get; init; }

    /// <summary>Inactive products are hidden from the shop. An active product needs at least one active variant.</summary>
    public bool IsActive { get; init; } = true;

    public bool IsFeatured { get; init; }

    /// <summary>Optional, up to 120 characters.</summary>
    public string? SeoTitle { get; init; }

    /// <summary>Optional, up to 320 characters.</summary>
    public string? SeoDescription { get; init; }
}

/// <summary>Body of <c>POST /api/v1/admin/products</c>.</summary>
public sealed record CreateProductRequest : ProductRequest
{
    /// <summary>At least one variant (at most 50).</summary>
    public IReadOnlyList<CreateVariantRequest>? Variants { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/admin/products/{id}</c>.</summary>
public sealed record UpdateProductRequest : ProductRequest
{
    /// <summary>The <c>version</c> of the product as last read. A stale value gives 409 <c>CONCURRENT_EDIT</c>.</summary>
    public uint? Version { get; init; }
}

/// <summary>Body of <c>POST /api/v1/admin/products/{id}/images</c>.</summary>
/// <param name="UploadKey">The <c>key</c> returned by <c>POST /admin/media/upload-url</c> (purpose <c>product</c>), after the file was uploaded.</param>
/// <param name="AltText">Optional description of the image, up to 200 characters.</param>
public sealed record AttachProductImageRequest(string? UploadKey, string? AltText);

/// <summary>Body of <c>PUT /api/v1/admin/products/{id}/images/order</c>.</summary>
/// <param name="ImageIds">Every image of the product exactly once, in the new order.</param>
/// <param name="MainImageId">The main image. Null makes the first image of the new order the main one.</param>
public sealed record ReorderProductImagesRequest(IReadOnlyList<Guid>? ImageIds, Guid? MainImageId);

/// <summary>Body of <c>PUT /api/v1/admin/products/{id}/images/{imageId}</c>.</summary>
/// <param name="AltText">Description of the image, up to 200 characters. Null or empty clears it.</param>
public sealed record UpdateProductImageRequest(string? AltText);

public enum AdminProductSort
{
    Newest,
    Oldest,
    Name,
    NameDescending,
    Updated,
}

/// <summary>Filter of the administrative product list.</summary>
public sealed record AdminProductListCriteria(
    string? Search,
    Guid? CategoryId,
    bool? IsActive,
    bool Archived,
    AdminProductSort Sort,
    PageRequest Page);

/// <summary>An image of a product.</summary>
/// <param name="BaseUrl">Folder of the renditions. A rendition is at <c>{baseUrl}/{width}.webp</c>.</param>
/// <param name="Widths">Available rendition widths in pixels, ascending.</param>
/// <param name="Width">Width of the original in pixels.</param>
/// <param name="Height">Height of the original in pixels.</param>
public sealed record ProductImageResponse(
    Guid Id,
    string? AltText,
    bool IsMain,
    int SortOrder,
    string BaseUrl,
    IReadOnlyList<int> Widths,
    int Width,
    int Height)
{
    public static ProductImageResponse From(MediaOptions options, ProductImage image) => new(
        image.Id,
        image.AltText,
        image.IsMain,
        image.SortOrder,
        options.UrlFor(image.StorageKey),
        RenditionPlan.WidthsFor(image.Width),
        image.Width,
        image.Height);
}

/// <summary>A variant with its stock, as the administration sees it.</summary>
/// <param name="EffectivePrice">The price a customer pays: the discount price when set, otherwise the regular price.</param>
/// <param name="StockStatus"><c>ok</c>, <c>low</c> or <c>out</c>.</param>
public sealed record AdminVariantResponse(
    Guid Id,
    string Name,
    string Sku,
    decimal UnitAmount,
    MeasurementUnit MeasurementUnit,
    decimal RegularPrice,
    decimal? DiscountPrice,
    decimal EffectivePrice,
    bool IsActive,
    int SortOrder,
    int QuantityOnHand,
    int LowStockThreshold,
    StockStatus StockStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminProductCategory(Guid Id, string Name);

/// <summary>Full product for the administration: values, variants with stock (archived variants excluded) and images in order.</summary>
/// <param name="Version">Send back as <c>version</c> when updating. Changes with every change of the product, its variants or images.</param>
/// <param name="ArchivedAt">Set when the product is archived.</param>
public sealed record AdminProductResponse(
    Guid Id,
    AdminProductCategory Category,
    string Name,
    string Slug,
    string ShortDescription,
    string Description,
    string Sku,
    bool IsActive,
    bool IsFeatured,
    bool IsArchived,
    string? SeoTitle,
    string? SeoDescription,
    IReadOnlyList<AdminVariantResponse> Variants,
    IReadOnlyList<ProductImageResponse> Images,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

/// <summary>One row of the administrative product list.</summary>
/// <param name="VariantCount">Variants that are not archived.</param>
/// <param name="PriceFrom">Lowest effective price among the variants that are not archived.</param>
/// <param name="HasPriceRange">True when those variants have different effective prices.</param>
/// <param name="TotalStock">Sum of the stock of those variants.</param>
/// <param name="StockStatus"><c>out</c> when every variant is out of stock, <c>low</c> when any variant is low or out, otherwise <c>ok</c>.</param>
public sealed record AdminProductListItem(
    Guid Id,
    string Name,
    string Slug,
    string Sku,
    AdminProductCategory Category,
    bool IsActive,
    bool IsFeatured,
    bool IsArchived,
    int VariantCount,
    decimal? PriceFrom,
    bool HasPriceRange,
    int TotalStock,
    StockStatus StockStatus,
    ProductImageResponse? MainImage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class CatalogValidation
{
    public const string SkuPattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";
    public const string SkuMessage = "Šifra sme da sadrži slova bez kvačica, cifre, tačku, crticu i donju crtu.";

    public static void VariantRules<T>(AbstractValidator<T> validator)
        where T : VariantRequest
    {
        validator.RuleFor(r => r.Name).NotEmpty().MaximumLength(120);
        validator.RuleFor(r => r.Sku).NotEmpty().MaximumLength(64).Matches(SkuPattern).WithMessage(SkuMessage);
        validator.RuleFor(r => r.UnitAmount)
            .NotNull()
            .GreaterThan(0)
            .LessThanOrEqualTo(ProductVariant.MaxUnitAmount)
            .Must(amount => amount is null || decimal.Round(amount.Value, 3) == amount.Value)
            .WithMessage("Količina može imati najviše tri decimale.");
        validator.RuleFor(r => r.MeasurementUnit).NotNull().IsInEnum();
        validator.RuleFor(r => r.RegularPrice)
            .NotNull()
            .GreaterThan(0)
            .LessThanOrEqualTo(Pricing.MaxPrice)
            .Must(price => price is null || Pricing.HasAtMostTwoDecimals(price.Value))
            .WithMessage("Cena može imati najviše dve decimale.");
        validator.RuleFor(r => r.DiscountPrice)
            .GreaterThan(0)
            .Must(price => price is null || Pricing.HasAtMostTwoDecimals(price.Value))
            .WithMessage("Cena može imati najviše dve decimale.");
        validator.RuleFor(r => r.DiscountPrice)
            .Must((request, discount) => discount is null || request.RegularPrice is null || discount < request.RegularPrice)
            .WithMessage("Akcijska cena mora biti niža od redovne cene.");
        validator.RuleFor(r => r.SortOrder).InclusiveBetween(0, 100_000);
    }

    public static void ProductRules<T>(AbstractValidator<T> validator)
        where T : ProductRequest
    {
        validator.RuleFor(r => r.CategoryId).NotEmpty();
        validator.RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        validator.RuleFor(r => r.Slug).ValidSlug();
        validator.RuleFor(r => r.ShortDescription).MaximumLength(500);
        validator.RuleFor(r => r.Description).MaximumLength(20_000);
        validator.RuleFor(r => r.Sku).NotEmpty().MaximumLength(64).Matches(SkuPattern).WithMessage(SkuMessage);
        validator.RuleFor(r => r.SeoTitle).MaximumLength(120);
        validator.RuleFor(r => r.SeoDescription).MaximumLength(320);
    }
}

public sealed class VariantRequestValidator : AbstractValidator<VariantRequest>
{
    public VariantRequestValidator() => CatalogValidation.VariantRules(this);
}

public sealed class CreateVariantRequestValidator : AbstractValidator<CreateVariantRequest>
{
    public CreateVariantRequestValidator()
    {
        CatalogValidation.VariantRules(this);
        RuleFor(r => r.InitialStock).InclusiveBetween(0, StockRules.MaxQuantity);
        RuleFor(r => r.LowStockThreshold).InclusiveBetween(0, StockRules.MaxQuantity);
    }
}

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        CatalogValidation.ProductRules(this);

        RuleFor(r => r.Variants)
            .NotEmpty().WithMessage("Proizvod mora imati najmanje jednu varijantu.")
            .Must(variants => variants is null || variants.Count <= Product.MaxVariants)
            .WithMessage($"Proizvod može imati najviše {Product.MaxVariants} varijanti.")
            .Must(variants => variants is null
                || variants.Where(v => !string.IsNullOrWhiteSpace(v?.Sku)).Select(v => v.Sku!.Trim().ToUpperInvariant()).Distinct().Count()
                    == variants.Count(v => !string.IsNullOrWhiteSpace(v?.Sku)))
            .WithMessage("Šifre varijanti moraju biti različite.")
            .Must((request, variants) => variants is null || variants.Count == 0 || !request.IsActive || variants.Any(v => v is { IsActive: true }))
            .WithMessage("Aktivan proizvod mora imati najmanje jednu aktivnu varijantu.");

        RuleForEach(r => r.Variants).NotNull().SetValidator(new CreateVariantRequestValidator());
    }
}

public sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        CatalogValidation.ProductRules(this);
        RuleFor(r => r.Version).NotNull();
    }
}

public sealed class AttachProductImageRequestValidator : AbstractValidator<AttachProductImageRequest>
{
    public AttachProductImageRequestValidator()
    {
        RuleFor(r => r.UploadKey).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AltText).MaximumLength(200);
    }
}

public sealed class ReorderProductImagesRequestValidator : AbstractValidator<ReorderProductImagesRequest>
{
    public ReorderProductImagesRequestValidator()
    {
        RuleFor(r => r.ImageIds)
            .NotNull()
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Svaka slika sme da se navede samo jednom.");
    }
}

public sealed class UpdateProductImageRequestValidator : AbstractValidator<UpdateProductImageRequest>
{
    public UpdateProductImageRequestValidator()
    {
        RuleFor(r => r.AltText).MaximumLength(200);
    }
}
