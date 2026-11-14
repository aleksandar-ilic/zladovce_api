using Zladovce.Domain.Cart;
using Zladovce.Domain.Common;

namespace Zladovce.Domain.StoreSettings;

/// <summary>
/// The single store settings record (table <c>store_settings</c>, always <c>id = 1</c>).
/// Money values are RSD with two decimals.
/// </summary>
public sealed class StoreSettingsRecord
{
    public const int SingletonId = 1;
    public const string DefaultStoreName = "Zladovce";

    private StoreSettingsRecord()
    {
    }

    public int Id { get; private set; }

    // Identity
    public string StoreName { get; private set; } = null!;
    public string? LogoKey { get; private set; }
    public string? ContactEmail { get; private set; }
    public string? Phone { get; private set; }
    public string? BusinessAddress { get; private set; }

    // Shipping
    public bool ShippingEnabled { get; private set; }
    public decimal ShippingPrice { get; private set; }
    public decimal? FreeShippingThreshold { get; private set; }
    public decimal? MinOrderAmount { get; private set; }
    public string? EstimatedDeliveryText { get; private set; }
    public string? DeliveryInfo { get; private set; }

    // Social
    public string? FacebookUrl { get; private set; }
    public string? InstagramUrl { get; private set; }
    public IReadOnlyList<StoreLink> OtherLinks { get; private set; } = [];

    // SEO
    public string? DefaultSeoTitle { get; private set; }
    public string? DefaultSeoDescription { get; private set; }

    // Meta
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedByAdminId { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    /// <summary>The neutral record created by the first migration: only the store name is set.</summary>
    public static StoreSettingsRecord CreateDefault(DateTimeOffset now) => new()
    {
        Id = SingletonId,
        StoreName = DefaultStoreName,
        ShippingEnabled = true,
        ShippingPrice = 0m,
        UpdatedAt = now,
    };

    public void Rename(string storeName, Guid adminId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(storeName))
        {
            throw new DomainException("Store name is required.");
        }

        StoreName = storeName.Trim();
        Touch(adminId, now);
    }

    /// <summary>
    /// Changes shipping pricing. The price cannot be negative; the free-shipping threshold and the
    /// minimum order amount are either absent (null) or strictly positive.
    /// </summary>
    public void SetShippingPricing(
        bool enabled,
        decimal price,
        decimal? freeShippingThreshold,
        decimal? minOrderAmount,
        Guid adminId,
        DateTimeOffset now)
    {
        EnsureMoney(price, allowZero: true, "Shipping price");
        if (freeShippingThreshold is { } threshold)
        {
            EnsureMoney(threshold, allowZero: false, "Free shipping threshold");
        }

        if (minOrderAmount is { } minimum)
        {
            EnsureMoney(minimum, allowZero: false, "Minimum order amount");
        }

        ShippingEnabled = enabled;
        ShippingPrice = price;
        FreeShippingThreshold = freeShippingThreshold;
        MinOrderAmount = minOrderAmount;
        Touch(adminId, now);
    }

    /// <summary>The shipping and minimum-order rules as a value, for cart and order totals (<see cref="CartTotals"/>).</summary>
    public ShippingTerms ShippingTerms => new(ShippingEnabled, ShippingPrice, FreeShippingThreshold, MinOrderAmount);

    /// <summary>Shipping cost for an order subtotal according to the current settings.</summary>
    public decimal ShippingCostFor(decimal subtotal)
    {
        if (subtotal < 0)
        {
            throw new DomainException("Subtotal cannot be negative.");
        }

        return FreeShippingThreshold is { } threshold && subtotal >= threshold ? 0m : ShippingPrice;
    }

    private void Touch(Guid adminId, DateTimeOffset now)
    {
        if (adminId == Guid.Empty)
        {
            throw new DomainException("Store settings changes require an administrator.");
        }

        UpdatedByAdminId = adminId;
        UpdatedAt = now;
    }

    private static void EnsureMoney(decimal value, bool allowZero, string name)
    {
        if (value < 0 || (!allowZero && value == 0))
        {
            throw new DomainException($"{name} must be {(allowZero ? "zero or greater" : "greater than zero")}.");
        }

        if (decimal.Round(value, 2) != value)
        {
            throw new DomainException($"{name} cannot have more than two decimals.");
        }

        if (value > 9_999_999_999.99m)
        {
            throw new DomainException($"{name} is too large.");
        }
    }
}
