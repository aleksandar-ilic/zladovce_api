using Zladovce.Domain.Common;

namespace Zladovce.Domain.Catalog;

/// <summary>Values of a product as entered by an administrator.</summary>
public sealed record ProductValues(
    Guid CategoryId,
    string Name,
    string Slug,
    string? ShortDescription,
    string? Description,
    string Sku,
    bool IsActive,
    bool IsFeatured,
    string? SeoTitle,
    string? SeoDescription);

/// <summary>Why a variant cannot be archived or deactivated.</summary>
public enum VariantRule
{
    Allowed,

    /// <summary>Every product keeps at least one variant.</summary>
    LastVariant,

    /// <summary>An active product keeps at least one active variant.</summary>
    LastActiveVariant,
}

/// <summary>
/// A product (table <c>products</c>) with its variants and images. Prices and stock live on the variants.
/// Archived through <see cref="DeletedAt"/>.
/// </summary>
public sealed class Product
{
    public const int MaxVariants = 50;
    public const int MaxImages = 20;

    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductImage> _images = [];

    private Product()
    {
    }

    public Guid Id { get; private set; }
    public Guid CategoryId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string ShortDescription { get; private set; } = null!;

    /// <summary>Sanitised HTML.</summary>
    public string Description { get; private set; } = null!;

    /// <summary>Base code for internal reference; every variant has its own SKU.</summary>
    public string Sku { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public bool IsFeatured { get; private set; }
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    /// <summary>All variants, archived ones included.</summary>
    public IReadOnlyList<ProductVariant> Variants => _variants;

    public IReadOnlyList<ProductImage> Images => _images;

    public bool IsArchived => DeletedAt is not null;

    /// <summary>Variants that are not archived, in display order.</summary>
    public IEnumerable<ProductVariant> CurrentVariants =>
        _variants.Where(v => !v.IsArchived).OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAt).ThenBy(v => v.Id);

    public IEnumerable<ProductImage> OrderedImages => _images.OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id);

    public ProductImage? MainImage => _images.FirstOrDefault(i => i.IsMain);

    public static Product Create(ProductValues values, IReadOnlyList<VariantValues> variants, DateTimeOffset now)
    {
        if (variants.Count == 0)
        {
            throw new DomainException("A product needs at least one variant.");
        }

        if (variants.Count > MaxVariants)
        {
            throw new DomainException("A product has too many variants.");
        }

        if (values.IsActive && !variants.Any(v => v.IsActive))
        {
            throw new DomainException("An active product needs at least one active variant.");
        }

        var product = new Product
        {
            Id = Guid.CreateVersion7(now),
            CreatedAt = now,
        };
        product.Apply(values, now);

        foreach (var variant in variants)
        {
            product._variants.Add(ProductVariant.Create(product.Id, variant, now));
        }

        product.EnsureDistinctVariantSkus();
        return product;
    }

    public void Update(ProductValues values, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (values.IsActive && !_variants.Any(v => v.IsSellable))
        {
            throw new DomainException("An active product needs at least one active variant.");
        }

        Apply(values, now);
    }

    public ProductVariant AddVariant(VariantValues values, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (CurrentVariants.Count() >= MaxVariants)
        {
            throw new DomainException("A product has too many variants.");
        }

        var variant = ProductVariant.Create(Id, values, now);
        _variants.Add(variant);
        EnsureDistinctVariantSkus();
        UpdatedAt = now;
        return variant;
    }

    public ProductVariant UpdateVariant(Guid variantId, VariantValues values, DateTimeOffset now)
    {
        EnsureNotArchived();
        var variant = FindCurrentVariant(variantId);
        if (!values.IsActive && CheckVariantDeactivation(variantId) != VariantRule.Allowed)
        {
            throw new DomainException("The last active variant of an active product cannot be deactivated.");
        }

        variant.Apply(values, now);
        EnsureDistinctVariantSkus();
        UpdatedAt = now;
        return variant;
    }

    public void ArchiveVariant(Guid variantId, DateTimeOffset now)
    {
        EnsureNotArchived();
        var variant = FindCurrentVariant(variantId);
        if (CheckVariantArchive(variantId) != VariantRule.Allowed)
        {
            throw new DomainException("This variant cannot be archived.");
        }

        variant.Archive(now);
        UpdatedAt = now;
    }

    /// <summary>Whether the variant may be archived. Unknown or archived variants are reported as allowed; the operation itself rejects them.</summary>
    public VariantRule CheckVariantArchive(Guid variantId)
    {
        var variant = _variants.FirstOrDefault(v => v.Id == variantId && !v.IsArchived);
        if (variant is null)
        {
            return VariantRule.Allowed;
        }

        if (!_variants.Any(v => v.Id != variantId && !v.IsArchived))
        {
            return VariantRule.LastVariant;
        }

        return IsLastSellable(variant) ? VariantRule.LastActiveVariant : VariantRule.Allowed;
    }

    /// <summary>Whether the variant may be made inactive.</summary>
    public VariantRule CheckVariantDeactivation(Guid variantId)
    {
        var variant = _variants.FirstOrDefault(v => v.Id == variantId && !v.IsArchived);
        return variant is not null && IsLastSellable(variant) ? VariantRule.LastActiveVariant : VariantRule.Allowed;
    }

    public void Archive(DateTimeOffset now)
    {
        if (IsArchived)
        {
            return;
        }

        DeletedAt = now;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        if (!IsArchived)
        {
            return;
        }

        DeletedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Adds an image. The first image of a product becomes its main image.</summary>
    public ProductImage AddImage(Guid imageId, string storageKey, string? altText, int width, int height, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (_images.Count >= MaxImages)
        {
            throw new DomainException("A product has too many images.");
        }

        if (_images.Any(i => i.Id == imageId))
        {
            throw new DomainException("The image is already attached to the product.");
        }

        var image = ProductImage.Create(
            imageId,
            Id,
            storageKey,
            altText,
            isMain: _images.Count == 0,
            sortOrder: _images.Count == 0 ? 0 : _images.Max(i => i.SortOrder) + 1,
            width,
            height,
            now);
        _images.Add(image);
        UpdatedAt = now;
        return image;
    }

    /// <summary>Removes an image. When it was the main image, the next one in order becomes main.</summary>
    public ProductImage RemoveImage(Guid imageId, DateTimeOffset now)
    {
        EnsureNotArchived();
        var image = FindImage(imageId);
        _images.Remove(image);

        var position = 0;
        var remaining = OrderedImages.ToList();
        var mainId = remaining.FirstOrDefault(i => i.IsMain)?.Id ?? remaining.FirstOrDefault()?.Id;
        foreach (var other in remaining)
        {
            other.SetPosition(position++, other.Id == mainId);
        }

        UpdatedAt = now;
        return image;
    }

    /// <summary>Sets the order of all images and which one is the main image.</summary>
    /// <param name="orderedImageIds">Every image of the product exactly once, in the new order.</param>
    /// <param name="mainImageId">The main image; null means the first in the new order.</param>
    public void ReorderImages(IReadOnlyList<Guid> orderedImageIds, Guid? mainImageId, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (orderedImageIds.Count != _images.Count
            || orderedImageIds.Distinct().Count() != orderedImageIds.Count
            || orderedImageIds.Any(id => _images.All(i => i.Id != id)))
        {
            throw new DomainException("The order must list every image of the product exactly once.");
        }

        if (orderedImageIds.Count == 0)
        {
            return;
        }

        var main = mainImageId ?? orderedImageIds[0];
        if (!orderedImageIds.Contains(main))
        {
            throw new DomainException("The main image must be one of the product images.");
        }

        for (var position = 0; position < orderedImageIds.Count; position++)
        {
            var image = _images.First(i => i.Id == orderedImageIds[position]);
            image.SetPosition(position, image.Id == main);
        }

        UpdatedAt = now;
    }

    public ProductImage SetImageAltText(Guid imageId, string? altText, DateTimeOffset now)
    {
        EnsureNotArchived();
        var image = FindImage(imageId);
        image.SetAltText(altText);
        UpdatedAt = now;
        return image;
    }

    private bool IsLastSellable(ProductVariant variant) =>
        IsActive && variant.IsSellable && !_variants.Any(v => v.Id != variant.Id && v.IsSellable);

    private ProductVariant FindCurrentVariant(Guid variantId) =>
        _variants.FirstOrDefault(v => v.Id == variantId && !v.IsArchived)
        ?? throw new DomainException("The variant does not belong to the product.");

    private ProductImage FindImage(Guid imageId) =>
        _images.FirstOrDefault(i => i.Id == imageId)
        ?? throw new DomainException("The image does not belong to the product.");

    private void EnsureDistinctVariantSkus()
    {
        var current = _variants.Where(v => !v.IsArchived).Select(v => v.Sku.ToUpperInvariant()).ToList();
        if (current.Distinct().Count() != current.Count)
        {
            throw new DomainException("Variant SKUs of a product must be distinct.");
        }
    }

    private void Apply(ProductValues values, DateTimeOffset now)
    {
        if (values.CategoryId == Guid.Empty)
        {
            throw new DomainException("Product category is required.");
        }

        if (string.IsNullOrWhiteSpace(values.Name))
        {
            throw new DomainException("Product name is required.");
        }

        if (!Common.Slug.IsValid(values.Slug))
        {
            throw new DomainException("Product slug is not valid.");
        }

        if (string.IsNullOrWhiteSpace(values.Sku))
        {
            throw new DomainException("Product SKU is required.");
        }

        CategoryId = values.CategoryId;
        Name = values.Name.Trim();
        Slug = values.Slug;
        ShortDescription = values.ShortDescription?.Trim() ?? string.Empty;
        Description = values.Description?.Trim() ?? string.Empty;
        Sku = values.Sku.Trim();
        IsActive = values.IsActive;
        IsFeatured = values.IsFeatured;
        SeoTitle = Text.NullIfBlank(values.SeoTitle);
        SeoDescription = Text.NullIfBlank(values.SeoDescription);
        UpdatedAt = now;
    }

    private void EnsureNotArchived()
    {
        if (IsArchived)
        {
            throw new DomainException("An archived product cannot be changed.");
        }
    }
}
