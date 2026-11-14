using Zladovce.Domain.Common;

namespace Zladovce.Domain.Cart;

/// <summary>
/// Shipping and minimum-order rules of the store at one moment (from the store settings). Money is RSD with two decimals.
/// </summary>
/// <param name="Enabled">False when the store currently does not ship; then no order can be placed.</param>
/// <param name="Price">Shipping price charged when shipping is not free.</param>
/// <param name="FreeShippingThreshold">Subtotal from which shipping is free (inclusive); null = never free by threshold.</param>
/// <param name="MinOrderAmount">Smallest subtotal that can be ordered (inclusive); null = no minimum.</param>
public sealed record ShippingTerms(bool Enabled, decimal Price, decimal? FreeShippingThreshold, decimal? MinOrderAmount);

/// <summary>Totals of a priced cart. All money is rounded to two decimals.</summary>
/// <param name="Subtotal">Sum of the line totals.</param>
/// <param name="ShippingEnabled">Copied from the terms.</param>
/// <param name="ShippingPrice">Shipping actually charged: 0 when free, when shipping is disabled or when the cart is empty.</param>
/// <param name="FreeShippingThreshold">Copied from the terms.</param>
/// <param name="IsShippingFree">True when the cart has lines, shipping is enabled and nothing is charged for it.</param>
/// <param name="AmountUntilFreeShipping">How much more subtotal makes shipping free; null when there is no threshold, it is reached, shipping costs nothing anyway or shipping is disabled.</param>
/// <param name="MinOrderAmount">Copied from the terms.</param>
/// <param name="MeetsMinimumOrder">True when there is no minimum or the subtotal reaches it.</param>
/// <param name="AmountUntilMinimumOrder">How much more subtotal reaches the minimum; null when it is met.</param>
/// <param name="Total">Subtotal plus the shipping charged.</param>
/// <param name="CanCheckout">Shipping is enabled, the cart has lines and the minimum is met.</param>
public sealed record CartTotals(
    decimal Subtotal,
    bool ShippingEnabled,
    decimal ShippingPrice,
    decimal? FreeShippingThreshold,
    bool IsShippingFree,
    decimal? AmountUntilFreeShipping,
    decimal? MinOrderAmount,
    bool MeetsMinimumOrder,
    decimal? AmountUntilMinimumOrder,
    decimal Total,
    bool CanCheckout)
{
    /// <summary>
    /// The one place where cart and order totals are computed (the cart quote and, from Phase 5, checkout).
    /// Shipping is charged unless the subtotal is at or above the free-shipping threshold; the minimum order compares the
    /// subtotal (without shipping).
    /// </summary>
    public static CartTotals Calculate(IEnumerable<decimal> lineTotals, ShippingTerms terms)
    {
        ArgumentNullException.ThrowIfNull(lineTotals);
        ArgumentNullException.ThrowIfNull(terms);

        var hasLines = false;
        var subtotal = 0m;
        foreach (var lineTotal in lineTotals)
        {
            if (lineTotal < 0)
            {
                throw new DomainException("A line total cannot be negative.");
            }

            hasLines = true;
            subtotal += lineTotal;
        }

        subtotal = Money.Round(subtotal);

        var reachesFreeShipping = terms.FreeShippingThreshold is { } threshold && subtotal >= threshold;
        var shipping = !terms.Enabled || !hasLines || reachesFreeShipping ? 0m : Money.Round(terms.Price);

        decimal? untilFree = terms.Enabled && terms.Price > 0 && terms.FreeShippingThreshold is { } t && subtotal < t
            ? Money.Round(t - subtotal)
            : null;

        var meetsMinimum = terms.MinOrderAmount is not { } minimum || subtotal >= minimum;
        decimal? untilMinimum = terms.MinOrderAmount is { } min && subtotal < min ? Money.Round(min - subtotal) : null;

        return new CartTotals(
            subtotal,
            terms.Enabled,
            shipping,
            terms.FreeShippingThreshold,
            terms.Enabled && hasLines && shipping == 0m,
            untilFree,
            terms.MinOrderAmount,
            meetsMinimum,
            untilMinimum,
            Money.Round(subtotal + shipping),
            terms.Enabled && hasLines && meetsMinimum);
    }
}
