using System.Text.Json.Serialization;
using FluentValidation;
using Zladovce.Application.Catalog;
using Zladovce.Domain.Catalog;

namespace Zladovce.Application.Cart;

/// <summary>Body of <c>POST /api/v1/cart/quote</c>: the browser cart (variant ids and quantities only).</summary>
public sealed record CartQuoteRequest
{
    public const int MaxItems = 50;
    public const int MaxQuantity = 99;

    /// <summary>1–50 lines. Lines with the same variant are merged (quantities added).</summary>
    public required IReadOnlyList<CartQuoteItemRequest>? Items { get; init; }

    public IReadOnlyList<CartItem> ToItems() =>
        [.. (Items ?? []).Select(i => new CartItem(i.VariantId!.Value, i.Quantity!.Value, i.KnownUnitPrice))];
}

/// <summary>One line of the browser cart.</summary>
public sealed record CartQuoteItemRequest
{
    /// <summary>Variant to buy.</summary>
    public required Guid? VariantId { get; init; }

    /// <summary>Requested quantity, 1–99.</summary>
    public required int? Quantity { get; init; }

    /// <summary>
    /// Optional: the unit price (RSD) the customer last saw for this variant (the <c>unitPrice</c> of the previous quote,
    /// kept in the browser cart). When it differs from the current price the quote reports a <c>priceChanged</c>
    /// adjustment. It is never used for pricing.
    /// </summary>
    public decimal? KnownUnitPrice { get; init; }
}

public sealed class CartQuoteRequestValidator : AbstractValidator<CartQuoteRequest>
{
    public CartQuoteRequestValidator()
    {
        RuleFor(r => r.Items)
            .NotEmpty().WithMessage("Korpa je prazna.")
            .Must(items => items is null || items.Count <= CartQuoteRequest.MaxItems)
            .WithMessage($"Korpa može imati najviše {CartQuoteRequest.MaxItems} stavki.");

        RuleForEach(r => r.Items).NotNull().SetValidator(new CartQuoteItemRequestValidator());
    }
}

public sealed class CartQuoteItemRequestValidator : AbstractValidator<CartQuoteItemRequest>
{
    public CartQuoteItemRequestValidator()
    {
        RuleFor(i => i.VariantId)
            .NotNull()
            .Must(id => id != Guid.Empty)
            .WithMessage("Identifikator varijante je obavezan.");
        RuleFor(i => i.Quantity)
            .NotNull()
            .InclusiveBetween(1, CartQuoteRequest.MaxQuantity)
            .WithMessage($"Količina mora biti između 1 i {CartQuoteRequest.MaxQuantity}.");
        RuleFor(i => i.KnownUnitPrice)
            .GreaterThan(0)
            .LessThanOrEqualTo(Pricing.MaxPrice)
            .Must(price => price is null || Pricing.HasAtMostTwoDecimals(price.Value))
            .WithMessage("Cena može imati najviše dve decimale.");
    }
}

/// <summary>A cart line as given to <see cref="ICartPricingService"/>.</summary>
/// <param name="KnownUnitPrice">Price the customer last saw; only used to report <see cref="CartAdjustmentReason.PriceChanged"/>.</param>
public sealed record CartItem(Guid VariantId, int Quantity, decimal? KnownUnitPrice = null);

/// <summary>What the pricing needs to know about one variant, read from the database in one query.</summary>
/// <param name="IsSellable">Variant active and not archived, product visible in the shop (active, not archived, active category).</param>
/// <param name="QuantityOnHand">Stock; never published.</param>
public sealed record CartVariantSnapshot(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string ProductSlug,
    string VariantName,
    string Sku,
    decimal UnitAmount,
    MeasurementUnit MeasurementUnit,
    decimal RegularPrice,
    decimal? DiscountPrice,
    PublicImageResponse? Image,
    bool IsSellable,
    int QuantityOnHand);

public enum CartAdjustmentReason
{
    /// <summary>The variant cannot be bought (unknown, inactive, archived, hidden product or category, out of stock); the line was removed.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    /// <summary>The requested quantity was above <c>maxOrderQuantity</c>; the line was reduced to it.</summary>
    [JsonStringEnumMemberName("quantityReduced")]
    QuantityReduced,

    /// <summary>The current unit price differs from the <c>knownUnitPrice</c> sent for the line.</summary>
    [JsonStringEnumMemberName("priceChanged")]
    PriceChanged,
}

/// <summary>A priced cart line.</summary>
/// <param name="Image">Main image of the product, when it has one.</param>
/// <param name="UnitPrice">Price per piece the customer pays (RSD): the discount price when there is one, otherwise the regular price.</param>
/// <param name="RegularPrice">Regular price per piece.</param>
/// <param name="DiscountPrice">Discounted price per piece, when there is one.</param>
/// <param name="RequestedQuantity">Quantity in the request (duplicate lines added up).</param>
/// <param name="Quantity">Quantity priced: the requested quantity, at most <c>maxOrderQuantity</c>.</param>
/// <param name="LineTotal"><c>unitPrice × quantity</c>.</param>
/// <param name="MaxOrderQuantity">Largest quantity that can be ordered now (stock limited by the shop cap); never the exact stock.</param>
public sealed record CartQuoteLine(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string ProductSlug,
    string VariantName,
    string Sku,
    decimal UnitAmount,
    MeasurementUnit MeasurementUnit,
    PublicImageResponse? Image,
    decimal UnitPrice,
    decimal RegularPrice,
    decimal? DiscountPrice,
    int RequestedQuantity,
    int Quantity,
    decimal LineTotal,
    int MaxOrderQuantity);

/// <summary>A difference between the browser cart and what can be bought now.</summary>
/// <param name="Reason"><c>unavailable</c>, <c>quantityReduced</c> or <c>priceChanged</c>. One variant can have both <c>quantityReduced</c> and <c>priceChanged</c>.</param>
/// <param name="RequestedQuantity">Quantity in the request (duplicate lines added up).</param>
/// <param name="AvailableQuantity">Quantity in the quote: 0 for <c>unavailable</c>, <c>maxOrderQuantity</c> for <c>quantityReduced</c>, the line quantity for <c>priceChanged</c>.</param>
/// <param name="PreviousUnitPrice"><c>priceChanged</c> only: the <c>knownUnitPrice</c> that was sent; otherwise null.</param>
/// <param name="UnitPrice"><c>priceChanged</c> only: the current unit price; otherwise null.</param>
public sealed record CartQuoteAdjustment(
    Guid VariantId,
    CartAdjustmentReason Reason,
    int RequestedQuantity,
    int AvailableQuantity,
    decimal? PreviousUnitPrice,
    decimal? UnitPrice);

/// <summary>Shipping of the quoted cart.</summary>
/// <param name="Enabled">False when the store currently does not ship: nothing can be ordered (<c>canCheckout</c> is false).</param>
/// <param name="Price">Shipping charged for this cart (RSD): 0 when free, when shipping is disabled or when the cart is empty.</param>
/// <param name="BasePrice">Configured shipping price (what is charged below the free-shipping threshold).</param>
/// <param name="FreeShippingThreshold">Subtotal from which shipping is free (inclusive); null when there is no free shipping.</param>
/// <param name="IsFree">True when the cart has lines, shipping is enabled and costs nothing.</param>
/// <param name="AmountUntilFreeShipping">Subtotal still missing for free shipping; null when not applicable (no threshold, reached, shipping costs nothing or is disabled).</param>
public sealed record CartQuoteShipping(
    bool Enabled,
    decimal Price,
    decimal BasePrice,
    decimal? FreeShippingThreshold,
    bool IsFree,
    decimal? AmountUntilFreeShipping);

/// <summary>Response of <c>POST /api/v1/cart/quote</c>: the cart priced from the database.</summary>
/// <param name="Lines">Sellable lines in request order (duplicates merged at the first occurrence).</param>
/// <param name="Adjustments">Differences from the request, in request order.</param>
/// <param name="Subtotal">Sum of the line totals (RSD).</param>
/// <param name="MinOrderAmount">Minimum subtotal for an order; null when there is none.</param>
/// <param name="MeetsMinimumOrder">True when there is no minimum or the subtotal reaches it.</param>
/// <param name="AmountUntilMinimumOrder">Subtotal still missing for the minimum; null when it is met.</param>
/// <param name="Total">Subtotal plus shipping charged.</param>
/// <param name="CanCheckout">False when shipping is disabled, there are no lines or the minimum is not met.</param>
/// <param name="Currency">Always <c>RSD</c>.</param>
public sealed record CartQuoteResponse(
    IReadOnlyList<CartQuoteLine> Lines,
    IReadOnlyList<CartQuoteAdjustment> Adjustments,
    decimal Subtotal,
    CartQuoteShipping Shipping,
    decimal? MinOrderAmount,
    bool MeetsMinimumOrder,
    decimal? AmountUntilMinimumOrder,
    decimal Total,
    bool CanCheckout,
    string Currency);
