using FluentValidation;
using Zladovce.Application.Cart;
using Zladovce.Domain.Cart;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Common;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.UnitTests.Application;

public class CartTotalsTests
{
    private static readonly ShippingTerms Standard = new(true, 450m, 6000m, 2000m);

    [Theory]
    [InlineData(5999.99, 450, false, 0.01)]
    [InlineData(6000, 0, true, null)]
    [InlineData(6000.01, 0, true, null)]
    public void Shipping_is_free_from_the_threshold_inclusive(decimal subtotal, decimal shipping, bool isFree, double? untilFree)
    {
        var totals = CartTotals.Calculate([subtotal], Standard);

        Assert.Equal(subtotal, totals.Subtotal);
        Assert.Equal(shipping, totals.ShippingPrice);
        Assert.Equal(isFree, totals.IsShippingFree);
        Assert.Equal(untilFree is null ? null : (decimal)untilFree.Value, totals.AmountUntilFreeShipping);
        Assert.Equal(subtotal + shipping, totals.Total);
        Assert.True(totals.CanCheckout);
    }

    [Theory]
    [InlineData(1999.99, false, 0.01)]
    [InlineData(2000, true, null)]
    [InlineData(2500, true, null)]
    public void Minimum_order_compares_the_subtotal_inclusive(decimal subtotal, bool meets, double? until)
    {
        var totals = CartTotals.Calculate([subtotal], Standard);

        Assert.Equal(meets, totals.MeetsMinimumOrder);
        Assert.Equal(until is null ? null : (decimal)until.Value, totals.AmountUntilMinimumOrder);
        Assert.Equal(meets, totals.CanCheckout);
        Assert.Equal(2000m, totals.MinOrderAmount);
    }

    [Fact]
    public void Minimum_ignores_shipping()
    {
        // 1700 + 450 shipping = 2150 ≥ 2000, but the subtotal is what counts.
        var totals = CartTotals.Calculate([1700m], Standard);

        Assert.Equal(2150m, totals.Total);
        Assert.False(totals.MeetsMinimumOrder);
        Assert.Equal(300m, totals.AmountUntilMinimumOrder);
        Assert.False(totals.CanCheckout);
    }

    [Fact]
    public void Lines_are_added_up_and_rounded()
    {
        var totals = CartTotals.Calculate([1180m, 1100m, 0.005m], new ShippingTerms(true, 350m, null, null));

        Assert.Equal(2280.01m, totals.Subtotal);
        Assert.Equal(350m, totals.ShippingPrice);
        Assert.Equal(2630.01m, totals.Total);
        Assert.Null(totals.AmountUntilFreeShipping);
        Assert.True(totals.MeetsMinimumOrder);
        Assert.Null(totals.AmountUntilMinimumOrder);
        Assert.True(totals.CanCheckout);
    }

    [Fact]
    public void Empty_cart_costs_nothing_and_cannot_be_ordered()
    {
        var totals = CartTotals.Calculate([], Standard);

        Assert.Equal(0m, totals.Subtotal);
        Assert.Equal(0m, totals.ShippingPrice);
        Assert.False(totals.IsShippingFree);
        Assert.Equal(6000m, totals.AmountUntilFreeShipping);
        Assert.Equal(2000m, totals.AmountUntilMinimumOrder);
        Assert.Equal(0m, totals.Total);
        Assert.False(totals.CanCheckout);
    }

    [Fact]
    public void Empty_cart_without_minimum_still_cannot_be_ordered()
    {
        var totals = CartTotals.Calculate([], new ShippingTerms(true, 450m, null, null));

        Assert.True(totals.MeetsMinimumOrder);
        Assert.False(totals.CanCheckout);
    }

    [Fact]
    public void Disabled_shipping_blocks_checkout_and_charges_nothing()
    {
        var totals = CartTotals.Calculate([3000m], Standard with { Enabled = false });

        Assert.False(totals.ShippingEnabled);
        Assert.Equal(0m, totals.ShippingPrice);
        Assert.False(totals.IsShippingFree);
        Assert.Null(totals.AmountUntilFreeShipping);
        Assert.True(totals.MeetsMinimumOrder);
        Assert.Equal(3000m, totals.Total);
        Assert.False(totals.CanCheckout);
    }

    [Fact]
    public void Zero_shipping_price_is_free_without_a_threshold_hint()
    {
        var totals = CartTotals.Calculate([100m], new ShippingTerms(true, 0m, 6000m, null));

        Assert.Equal(0m, totals.ShippingPrice);
        Assert.True(totals.IsShippingFree);
        Assert.Null(totals.AmountUntilFreeShipping);
        Assert.Equal(100m, totals.Total);
    }

    [Fact]
    public void Negative_line_totals_are_refused() =>
        Assert.Throws<DomainException>(() => CartTotals.Calculate([10m, -1m], Standard));

    [Fact]
    public void Store_settings_expose_their_terms_and_agree_with_the_calculation()
    {
        var settings = StoreSettingsRecord.CreateDefault(DateTimeOffset.UnixEpoch);
        settings.SetShippingPricing(true, 450m, 6000m, 1500m, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Equal(new ShippingTerms(true, 450m, 6000m, 1500m), settings.ShippingTerms);
        foreach (var subtotal in new[] { 1500m, 5999.99m, 6000m, 9000m })
        {
            Assert.Equal(settings.ShippingCostFor(subtotal), CartTotals.Calculate([subtotal], settings.ShippingTerms).ShippingPrice);
        }
    }

    [Theory]
    [InlineData(590, 2, 1180)]
    [InlineData(0.335, 3, 1.01)]
    [InlineData(100, 0, 0)]
    public void Line_total_is_price_times_quantity_rounded(decimal price, int quantity, decimal expected) =>
        Assert.Equal(expected, Money.LineTotal(price, quantity));
}

public class CartQuoteCalculatorTests
{
    private const int Cap = 20;
    private static readonly ShippingTerms Terms = new(true, 450m, 6000m, null);

    private static CartVariantSnapshot Variant(
        decimal regular = 650m, decimal? discount = null, int stock = 10, bool sellable = true, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), "Ajvar", "ajvar", "370 g", "AJ-370", 370m, MeasurementUnit.Gram,
            regular, discount, null, sellable, stock);

    private static CartQuoteResponse Quote(IEnumerable<CartItem> items, params CartVariantSnapshot[] variants) =>
        CartQuoteCalculator.Calculate(
            CartQuoteCalculator.Merge(items), variants.ToDictionary(v => v.VariantId), Terms, Cap);

    [Fact]
    public void Discounted_and_regular_variants_are_priced_from_the_snapshot()
    {
        var discounted = Variant(650m, 590m);
        var regular = Variant(1100m);

        var quote = Quote([new(discounted.VariantId, 2), new(regular.VariantId, 1)], discounted, regular);

        Assert.Empty(quote.Adjustments);
        Assert.Equal([discounted.VariantId, regular.VariantId], quote.Lines.Select(l => l.VariantId));
        var first = quote.Lines[0];
        Assert.Equal(590m, first.UnitPrice);
        Assert.Equal(650m, first.RegularPrice);
        Assert.Equal(590m, first.DiscountPrice);
        Assert.Equal(1180m, first.LineTotal);
        Assert.Equal(2, first.Quantity);
        Assert.Equal(10, first.MaxOrderQuantity);
        Assert.Null(quote.Lines[1].DiscountPrice);
        Assert.Equal(1100m, quote.Lines[1].LineTotal);
        Assert.Equal(2280m, quote.Subtotal);
        Assert.Equal(450m, quote.Shipping.Price);
        Assert.Equal(450m, quote.Shipping.BasePrice);
        Assert.Equal(3720m, quote.Shipping.AmountUntilFreeShipping);
        Assert.Equal(2730m, quote.Total);
        Assert.True(quote.CanCheckout);
        Assert.Equal("RSD", quote.Currency);
    }

    [Fact]
    public void Quantity_is_capped_by_stock_and_by_the_shop_cap()
    {
        var lowStock = Variant(stock: 3);
        var plenty = Variant(stock: 500);

        var quote = Quote([new(lowStock.VariantId, 5), new(plenty.VariantId, 30)], lowStock, plenty);

        Assert.Equal([3, 20], quote.Lines.Select(l => l.Quantity));
        Assert.Equal([5, 30], quote.Lines.Select(l => l.RequestedQuantity));
        Assert.Equal([3, 20], quote.Lines.Select(l => l.MaxOrderQuantity));
        Assert.All(quote.Adjustments, a => Assert.Equal(CartAdjustmentReason.QuantityReduced, a.Reason));
        Assert.Equal([(5, 3), (30, 20)], quote.Adjustments.Select(a => (a.RequestedQuantity, a.AvailableQuantity)));
        Assert.Equal(23 * 650m, quote.Subtotal);
    }

    [Fact]
    public void Quantity_equal_to_the_maximum_is_not_an_adjustment()
    {
        var variant = Variant(stock: 4);
        var quote = Quote([new(variant.VariantId, 4)], variant);

        Assert.Empty(quote.Adjustments);
        Assert.Equal(4, quote.Lines.Single().Quantity);
    }

    [Fact]
    public void Unknown_unsellable_and_out_of_stock_variants_are_removed_and_reported()
    {
        var unknown = Guid.NewGuid();
        var hidden = Variant(sellable: false);
        var empty = Variant(stock: 0);
        var fine = Variant();

        var quote = Quote(
            [new(unknown, 1), new(hidden.VariantId, 2), new(empty.VariantId, 3), new(fine.VariantId, 1)],
            hidden, empty, fine);

        Assert.Equal([fine.VariantId], quote.Lines.Select(l => l.VariantId));
        Assert.Equal([unknown, hidden.VariantId, empty.VariantId], quote.Adjustments.Select(a => a.VariantId));
        Assert.All(quote.Adjustments, a =>
        {
            Assert.Equal(CartAdjustmentReason.Unavailable, a.Reason);
            Assert.Equal(0, a.AvailableQuantity);
            Assert.Null(a.PreviousUnitPrice);
        });
        Assert.Equal([1, 2, 3], quote.Adjustments.Select(a => a.RequestedQuantity));
        Assert.Equal(650m, quote.Subtotal);
    }

    [Fact]
    public void Nothing_sellable_gives_an_empty_cart_that_cannot_be_ordered()
    {
        var quote = Quote([new(Guid.NewGuid(), 1)]);

        Assert.Empty(quote.Lines);
        Assert.Single(quote.Adjustments);
        Assert.Equal(0m, quote.Subtotal);
        Assert.Equal(0m, quote.Shipping.Price);
        Assert.Equal(0m, quote.Total);
        Assert.False(quote.CanCheckout);
    }

    [Fact]
    public void Duplicate_lines_are_merged_at_the_first_position()
    {
        var a = Variant();
        var b = Variant();

        var quote = Quote([new(a.VariantId, 2), new(b.VariantId, 1), new(a.VariantId, 3, 650m)], a, b);

        Assert.Equal([a.VariantId, b.VariantId], quote.Lines.Select(l => l.VariantId));
        Assert.Equal(5, quote.Lines[0].Quantity);
        Assert.Equal(5, quote.Lines[0].RequestedQuantity);
        Assert.Empty(quote.Adjustments);
    }

    [Fact]
    public void Merged_quantity_above_the_cap_is_reduced()
    {
        var variant = Variant(stock: 100);
        var quote = Quote([new(variant.VariantId, 15), new(variant.VariantId, 15)], variant);

        var adjustment = Assert.Single(quote.Adjustments);
        Assert.Equal((30, 20), (adjustment.RequestedQuantity, adjustment.AvailableQuantity));
    }

    [Fact]
    public void A_different_known_price_is_reported_and_the_current_price_is_used()
    {
        var cheaper = Variant(650m, 590m);
        var same = Variant(1100m);
        var dearer = Variant(1200m, stock: 2);

        var quote = Quote(
            [new(cheaper.VariantId, 1, 650m), new(same.VariantId, 1, 1100m), new(dearer.VariantId, 3, 1100m)],
            cheaper, same, dearer);

        Assert.Equal([590m, 1100m, 1200m], quote.Lines.Select(l => l.UnitPrice));
        var changes = quote.Adjustments.Where(a => a.Reason == CartAdjustmentReason.PriceChanged).ToList();
        Assert.Equal([cheaper.VariantId, dearer.VariantId], changes.Select(c => c.VariantId));
        Assert.Equal((650m, 590m), (changes[0].PreviousUnitPrice, changes[0].UnitPrice));
        Assert.Equal((1100m, 1200m), (changes[1].PreviousUnitPrice, changes[1].UnitPrice));
        Assert.Equal(2, changes[1].AvailableQuantity);

        // The dearer line is also capped: both reasons are reported, quantity first.
        Assert.Equal(
            [CartAdjustmentReason.QuantityReduced, CartAdjustmentReason.PriceChanged],
            quote.Adjustments.Where(a => a.VariantId == dearer.VariantId).Select(a => a.Reason));
    }

    [Fact]
    public void Known_price_of_an_unavailable_variant_is_not_a_price_change()
    {
        var gone = Variant(stock: 0);
        var quote = Quote([new(gone.VariantId, 1, 1m)], gone);

        Assert.Equal(CartAdjustmentReason.Unavailable, Assert.Single(quote.Adjustments).Reason);
    }

    [Fact]
    public void Free_shipping_threshold_applies_to_the_capped_subtotal()
    {
        var variant = Variant(300m, stock: 100);

        // 20 × 300 = 6000 reaches the threshold exactly; 30 requested are capped to 20.
        var quote = Quote([new(variant.VariantId, 30)], variant);

        Assert.Equal(6000m, quote.Subtotal);
        Assert.True(quote.Shipping.IsFree);
        Assert.Equal(0m, quote.Shipping.Price);
        Assert.Null(quote.Shipping.AmountUntilFreeShipping);
        Assert.Equal(6000m, quote.Total);
    }

    [Fact]
    public void Merge_refuses_non_positive_quantities() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CartQuoteCalculator.Merge([new(Guid.NewGuid(), 0)]));
}

public class CartQuoteRequestValidatorTests
{
    private static readonly CartQuoteRequestValidator Validator = new();

    private static CartQuoteItemRequest Item(int? quantity = 1, Guid? id = null, decimal? known = null) =>
        new() { VariantId = id ?? Guid.NewGuid(), Quantity = quantity, KnownUnitPrice = known };

    private static List<string> Errors(params CartQuoteItemRequest[] items) =>
        [.. Validator.Validate(new CartQuoteRequest { Items = items }).Errors.Select(e => e.PropertyName)];

    [Fact]
    public void A_normal_cart_is_valid()
    {
        Assert.Empty(Errors(Item(1), Item(99, known: 590.5m)));
        Assert.Empty(Errors([.. Enumerable.Range(0, 50).Select(_ => Item())]));
    }

    [Fact]
    public void Empty_null_and_oversized_carts_are_refused()
    {
        Assert.Equal(["Items"], Errors());
        Assert.Contains("Items", Validator.Validate(new CartQuoteRequest { Items = null }).Errors.Select(e => e.PropertyName));
        Assert.Equal(["Items"], Errors([.. Enumerable.Range(0, 51).Select(_ => Item())]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(null)]
    public void Quantity_must_be_1_to_99(int? quantity) =>
        Assert.Equal(["Items[0].Quantity"], Errors(Item(quantity)).Distinct());

    [Fact]
    public void Empty_variant_id_is_refused() =>
        Assert.Equal(["Items[0].VariantId"], Errors(Item(id: Guid.Empty)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1.005)]
    public void Known_price_must_be_a_positive_amount_with_two_decimals(double known) =>
        Assert.Equal(["Items[0].KnownUnitPrice"], Errors(Item(known: (decimal)known)).Distinct());

    [Fact]
    public void Validator_is_found_by_assembly_scan() =>
        Assert.IsAssignableFrom<IValidator<CartQuoteRequest>>(Validator);
}
