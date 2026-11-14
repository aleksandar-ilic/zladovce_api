using Zladovce.Domain.Common;

namespace Zladovce.Domain.Catalog;

/// <summary>Values of a variant as entered by an administrator.</summary>
public sealed record VariantValues(
    string Name,
    string Sku,
    decimal UnitAmount,
    MeasurementUnit MeasurementUnit,
    decimal RegularPrice,
    decimal? DiscountPrice,
    bool IsActive,
    int SortOrder);

/// <summary>The sellable unit of a product (table <c>product_variants</c>): priced and stocked on its own.</summary>
public sealed class ProductVariant
{
    /// <summary>Largest value that fits <c>numeric(10,3)</c>.</summary>
    public const decimal MaxUnitAmount = 9_999_999.999m;

    private ProductVariant()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Sku { get; private set; } = null!;
    public decimal UnitAmount { get; private set; }
    public MeasurementUnit MeasurementUnit { get; private set; }
    public decimal RegularPrice { get; private set; }
    public decimal? DiscountPrice { get; private set; }
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsArchived => DeletedAt is not null;

    /// <summary>Active and not archived. Whether it can be bought also depends on the product and on stock.</summary>
    public bool IsSellable => IsActive && !IsArchived;

    public decimal EffectivePrice => Pricing.EffectivePrice(RegularPrice, DiscountPrice);

    public bool HasDiscount => DiscountPrice is not null;

    internal static ProductVariant Create(Guid productId, VariantValues values, DateTimeOffset now)
    {
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(now),
            ProductId = productId,
            CreatedAt = now,
        };
        variant.Apply(values, now);
        return variant;
    }

    internal void Apply(VariantValues values, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(values.Name))
        {
            throw new DomainException("Variant name is required.");
        }

        if (string.IsNullOrWhiteSpace(values.Sku))
        {
            throw new DomainException("Variant SKU is required.");
        }

        if (values.UnitAmount <= 0 || values.UnitAmount > MaxUnitAmount || decimal.Round(values.UnitAmount, 3) != values.UnitAmount)
        {
            throw new DomainException("Variant unit amount must be positive with at most three decimals.");
        }

        if (!Enum.IsDefined(values.MeasurementUnit))
        {
            throw new DomainException("Unknown measurement unit.");
        }

        if (values.SortOrder < 0)
        {
            throw new DomainException("Variant sort order cannot be negative.");
        }

        Pricing.EnsureValid(values.RegularPrice, values.DiscountPrice);

        Name = values.Name.Trim();
        Sku = values.Sku.Trim();
        UnitAmount = values.UnitAmount;
        MeasurementUnit = values.MeasurementUnit;
        RegularPrice = values.RegularPrice;
        DiscountPrice = values.DiscountPrice;
        IsActive = values.IsActive;
        SortOrder = values.SortOrder;
        UpdatedAt = now;
    }

    internal void Archive(DateTimeOffset now)
    {
        DeletedAt = now;
        UpdatedAt = now;
    }
}
