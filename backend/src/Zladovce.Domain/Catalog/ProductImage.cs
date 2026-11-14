using Zladovce.Domain.Common;

namespace Zladovce.Domain.Catalog;

/// <summary>An image of a product (table <c>product_images</c>). The files are WebP renditions under <see cref="StorageKey"/>.</summary>
public sealed class ProductImage
{
    private ProductImage()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }

    /// <summary>Base key of the renditions: <c>media/products/{imageId}</c>.</summary>
    public string StorageKey { get; private set; } = null!;
    public string? AltText { get; private set; }
    public bool IsMain { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>Width of the original (after its orientation was applied), in pixels.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static ProductImage Create(
        Guid id, Guid productId, string storageKey, string? altText, bool isMain, int sortOrder, int width, int height, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Image id is required.");
        }

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException("Image storage key is required.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new DomainException("Image dimensions must be positive.");
        }

        return new ProductImage
        {
            Id = id,
            ProductId = productId,
            StorageKey = storageKey,
            AltText = Text.NullIfBlank(altText),
            IsMain = isMain,
            SortOrder = sortOrder,
            Width = width,
            Height = height,
            CreatedAt = now,
        };
    }

    internal void SetAltText(string? altText) => AltText = Text.NullIfBlank(altText);

    internal void SetPosition(int sortOrder, bool isMain)
    {
        SortOrder = sortOrder;
        IsMain = isMain;
    }
}
