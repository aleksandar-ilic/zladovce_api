using Zladovce.Domain.Common;

namespace Zladovce.Domain.Catalog;

/// <summary>
/// Price rules. Prices are RSD with two decimals and live on variants only; the listing price of a product is derived.
/// </summary>
public static class Pricing
{
    /// <summary>Largest value that fits <c>numeric(12,2)</c>.</summary>
    public const decimal MaxPrice = 9_999_999_999.99m;

    /// <summary>The price a customer pays: the discount price when there is one, otherwise the regular price.</summary>
    public static decimal EffectivePrice(decimal regularPrice, decimal? discountPrice) => discountPrice ?? regularPrice;

    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    /// <summary>Null when the pair is acceptable; otherwise the reason (developer text).</summary>
    public static string? FindViolation(decimal regularPrice, decimal? discountPrice)
    {
        if (regularPrice <= 0)
        {
            return "Regular price must be greater than zero.";
        }

        if (regularPrice > MaxPrice || !HasAtMostTwoDecimals(regularPrice))
        {
            return "Regular price must have at most two decimals and fit the money type.";
        }

        if (discountPrice is { } discount)
        {
            if (discount <= 0)
            {
                return "Discount price must be greater than zero.";
            }

            if (!HasAtMostTwoDecimals(discount))
            {
                return "Discount price must have at most two decimals.";
            }

            if (discount >= regularPrice)
            {
                return "Discount price must be lower than the regular price.";
            }
        }

        return null;
    }

    public static void EnsureValid(decimal regularPrice, decimal? discountPrice)
    {
        if (FindViolation(regularPrice, discountPrice) is { } violation)
        {
            throw new DomainException(violation);
        }
    }
}

/// <summary>
/// The price shown for a product in listings: the lowest effective price among its sellable variants.
/// </summary>
/// <param name="Price">Lowest effective price.</param>
/// <param name="RegularPrice">Regular price of that cheapest variant when it is discounted; otherwise null.</param>
/// <param name="HasPriceRange">True when sellable variants have different effective prices ("od …").</param>
public sealed record ListingPrice(decimal Price, decimal? RegularPrice, bool HasPriceRange)
{
    public bool HasDiscount => RegularPrice is not null;

    /// <summary>Returns null when there is no variant to price.</summary>
    public static ListingPrice? From(IEnumerable<(decimal RegularPrice, decimal? DiscountPrice)> variants)
    {
        (decimal Regular, decimal? Discount)? cheapest = null;
        var distinctPrices = new HashSet<decimal>();

        foreach (var (regular, discount) in variants)
        {
            var effective = Pricing.EffectivePrice(regular, discount);
            distinctPrices.Add(effective);

            if (cheapest is not { } current)
            {
                cheapest = (regular, discount);
                continue;
            }

            var currentEffective = Pricing.EffectivePrice(current.Regular, current.Discount);

            // Among equally priced variants prefer the discounted one, so the saving is shown.
            if (effective < currentEffective || (effective == currentEffective && current.Discount is null && discount is not null))
            {
                cheapest = (regular, discount);
            }
        }

        if (cheapest is not { } best)
        {
            return null;
        }

        return new ListingPrice(
            Pricing.EffectivePrice(best.Regular, best.Discount),
            best.Discount is null ? null : best.Regular,
            distinctPrices.Count > 1);
    }
}
