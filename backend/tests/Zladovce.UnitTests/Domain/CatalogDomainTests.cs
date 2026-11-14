using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Common;
using Zladovce.Domain.Inventory;

namespace Zladovce.UnitTests.Domain;

public class PricingTests
{
    [Fact]
    public void Effective_price_is_the_discount_price_when_present() =>
        Assert.Equal(590m, Pricing.EffectivePrice(650m, 590m));

    [Fact]
    public void Effective_price_is_the_regular_price_without_discount() =>
        Assert.Equal(650m, Pricing.EffectivePrice(650m, null));

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(100.0, 100.0)]
    [InlineData(100.0, 120.0)]
    [InlineData(100.0, 0.0)]
    [InlineData(100.001, null)]
    [InlineData(100.0, 50.555)]
    public void Invalid_price_pairs_are_refused(double regular, double? discount)
    {
        var violation = Pricing.FindViolation((decimal)regular, discount is null ? null : (decimal)discount);
        Assert.NotNull(violation);
        Assert.Throws<DomainException>(() => Pricing.EnsureValid((decimal)regular, discount is null ? null : (decimal)discount));
    }

    [Theory]
    [InlineData(650.0, null)]
    [InlineData(650.0, 649.99)]
    [InlineData(0.01, null)]
    public void Valid_price_pairs_are_accepted(double regular, double? discount) =>
        Assert.Null(Pricing.FindViolation((decimal)regular, discount is null ? null : (decimal)discount));

    [Fact]
    public void Listing_price_is_the_lowest_effective_price_with_range_flag()
    {
        var price = ListingPrice.From([(650m, 590m), (1100m, null)])!;

        Assert.Equal(590m, price.Price);
        Assert.Equal(650m, price.RegularPrice);
        Assert.True(price.HasDiscount);
        Assert.True(price.HasPriceRange);
    }

    [Fact]
    public void Listing_price_without_discount_has_no_regular_price_and_no_range_for_equal_prices()
    {
        var price = ListingPrice.From([(500m, null), (500m, null)])!;

        Assert.Equal(500m, price.Price);
        Assert.Null(price.RegularPrice);
        Assert.False(price.HasPriceRange);
    }

    [Fact]
    public void Listing_price_prefers_the_discounted_variant_among_equal_prices()
    {
        var price = ListingPrice.From([(500m, null), (600m, 500m)])!;

        Assert.Equal(500m, price.Price);
        Assert.Equal(600m, price.RegularPrice);
        Assert.False(price.HasPriceRange);
    }

    [Fact]
    public void Listing_price_of_no_variants_is_null() => Assert.Null(ListingPrice.From([]));
}

public class SlugTests
{
    [Theory]
    [InlineData("Džem od šljiva", "dzem-od-sljiva")]
    [InlineData("Čokoladni ćevap", "cokoladni-cevap")]
    [InlineData("Đumbir & med", "djumbir-med")]
    [InlineData("ŽUTA  paprika!!", "zuta-paprika")]
    [InlineData("  Crème brûlée 2 ", "creme-brulee-2")]
    [InlineData("---", "")]
    public void Generated_slugs_are_transliterated_ascii(string text, string expected) =>
        Assert.Equal(expected, Slug.Generate(text));

    [Fact]
    public void Generated_slug_is_limited_in_length() =>
        Assert.True(Slug.Generate(new string('a', 300)).Length <= Slug.MaxLength);

    [Theory]
    [InlineData("dzem-od-sljiva", true)]
    [InlineData("a1", true)]
    [InlineData("Dzem", false)]
    [InlineData("džem", false)]
    [InlineData("-dzem", false)]
    [InlineData("dzem-", false)]
    [InlineData("dzem--sljiva", false)]
    [InlineData("dzem sljiva", false)]
    [InlineData("", false)]
    public void Slug_format_is_validated(string value, bool valid) => Assert.Equal(valid, Slug.IsValid(value));
}

public class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    private static ProductValues Values(bool isActive = true) =>
        new(CategoryId, "Džem", "dzem", "Kratko", "<p>Opis</p>", "DZ", isActive, false, null, null);

    private static VariantValues Variant(string sku, bool isActive = true, decimal price = 500m, decimal? discount = null, int sortOrder = 0) =>
        new("370 g", sku, 370m, MeasurementUnit.Gram, price, discount, isActive, sortOrder);

    [Fact]
    public void Product_needs_at_least_one_variant() =>
        Assert.Throws<DomainException>(() => Product.Create(Values(), [], Now));

    [Fact]
    public void Active_product_needs_an_active_variant() =>
        Assert.Throws<DomainException>(() => Product.Create(Values(), [Variant("A", isActive: false)], Now));

    [Fact]
    public void Inactive_product_may_have_only_inactive_variants()
    {
        var product = Product.Create(Values(isActive: false), [Variant("A", isActive: false)], Now);
        Assert.Single(product.Variants);
    }

    [Fact]
    public void Variant_skus_must_differ_ignoring_case() =>
        Assert.Throws<DomainException>(() => Product.Create(Values(), [Variant("abc"), Variant("ABC")], Now));

    [Fact]
    public void Variant_with_invalid_price_is_refused() =>
        Assert.Throws<DomainException>(() => Product.Create(Values(), [Variant("A", price: 500m, discount: 600m)], Now));

    [Fact]
    public void Variant_amount_must_be_positive_with_three_decimals()
    {
        Assert.Throws<DomainException>(() => Product.Create(Values(), [new VariantValues("x", "A", 0m, MeasurementUnit.Gram, 1m, null, true, 0)], Now));
        Assert.Throws<DomainException>(() => Product.Create(Values(), [new VariantValues("x", "A", 1.0001m, MeasurementUnit.Gram, 1m, null, true, 0)], Now));
    }

    [Fact]
    public void Last_variant_cannot_be_archived()
    {
        var product = Product.Create(Values(isActive: false), [Variant("A")], Now);
        Assert.Equal(VariantRule.LastVariant, product.CheckVariantArchive(product.Variants[0].Id));
        Assert.Throws<DomainException>(() => product.ArchiveVariant(product.Variants[0].Id, Now));
    }

    [Fact]
    public void Last_active_variant_of_an_active_product_cannot_be_archived_or_deactivated()
    {
        var product = Product.Create(Values(), [Variant("A"), Variant("B", isActive: false)], Now);
        var active = product.Variants.Single(v => v.Sku == "A").Id;

        Assert.Equal(VariantRule.LastActiveVariant, product.CheckVariantArchive(active));
        Assert.Equal(VariantRule.LastActiveVariant, product.CheckVariantDeactivation(active));
        Assert.Throws<DomainException>(() => product.UpdateVariant(active, Variant("A", isActive: false), Now));
    }

    [Fact]
    public void Other_variants_can_be_archived()
    {
        var product = Product.Create(Values(), [Variant("A"), Variant("B")], Now);
        var b = product.Variants.Single(v => v.Sku == "B").Id;

        product.ArchiveVariant(b, Now.AddMinutes(1));

        Assert.Single(product.CurrentVariants);
        Assert.Equal(VariantRule.LastVariant, product.CheckVariantArchive(product.Variants.Single(v => v.Sku == "A").Id));
    }

    [Fact]
    public void Product_cannot_be_activated_without_an_active_variant()
    {
        var product = Product.Create(Values(isActive: false), [Variant("A", isActive: false)], Now);
        Assert.Throws<DomainException>(() => product.Update(Values(isActive: true), Now));
    }

    [Fact]
    public void Archived_product_cannot_be_changed_but_can_be_restored()
    {
        var product = Product.Create(Values(), [Variant("A")], Now);
        product.Archive(Now);

        Assert.True(product.IsArchived);
        Assert.Throws<DomainException>(() => product.Update(Values(), Now));
        Assert.Throws<DomainException>(() => product.AddVariant(Variant("B"), Now));

        product.Restore(Now);
        Assert.False(product.IsArchived);
    }

    [Fact]
    public void First_image_is_main_and_removing_it_promotes_the_next()
    {
        var product = Product.Create(Values(), [Variant("A")], Now);
        var first = product.AddImage(Guid.CreateVersion7(), "media/products/1", null, 1000, 800, Now);
        var second = product.AddImage(Guid.CreateVersion7(), "media/products/2", "alt", 1000, 800, Now);

        Assert.True(first.IsMain);
        Assert.False(second.IsMain);
        Assert.Equal(1, second.SortOrder);

        product.RemoveImage(first.Id, Now);

        Assert.True(second.IsMain);
        Assert.Equal(0, second.SortOrder);
    }

    [Fact]
    public void Reorder_sets_positions_and_the_main_image()
    {
        var product = Product.Create(Values(), [Variant("A")], Now);
        var a = product.AddImage(Guid.CreateVersion7(), "k/a", null, 10, 10, Now);
        var b = product.AddImage(Guid.CreateVersion7(), "k/b", null, 10, 10, Now);
        var c = product.AddImage(Guid.CreateVersion7(), "k/c", null, 10, 10, Now);

        product.ReorderImages([c.Id, a.Id, b.Id], b.Id, Now);

        Assert.Equal([c.Id, a.Id, b.Id], product.OrderedImages.Select(i => i.Id));
        Assert.Equal(b.Id, product.MainImage!.Id);

        product.ReorderImages([a.Id, b.Id, c.Id], null, Now);
        Assert.Equal(a.Id, product.MainImage!.Id);
    }

    [Fact]
    public void Reorder_must_list_every_image_once()
    {
        var product = Product.Create(Values(), [Variant("A")], Now);
        var a = product.AddImage(Guid.CreateVersion7(), "k/a", null, 10, 10, Now);
        product.AddImage(Guid.CreateVersion7(), "k/b", null, 10, 10, Now);

        Assert.Throws<DomainException>(() => product.ReorderImages([a.Id], null, Now));
        Assert.Throws<DomainException>(() => product.ReorderImages([a.Id, a.Id], null, Now));
    }

    [Fact]
    public void Image_limit_is_enforced()
    {
        var product = Product.Create(Values(), [Variant("A")], Now);
        for (var i = 0; i < Product.MaxImages; i++)
        {
            product.AddImage(Guid.CreateVersion7(), $"k/{i}", null, 10, 10, Now);
        }

        Assert.Throws<DomainException>(() => product.AddImage(Guid.CreateVersion7(), "k/x", null, 10, 10, Now));
    }

    [Fact]
    public void Variant_effective_price_follows_the_discount()
    {
        var product = Product.Create(Values(), [Variant("A", price: 650m, discount: 590m)], Now);
        var variant = product.Variants[0];

        Assert.Equal(590m, variant.EffectivePrice);
        Assert.True(variant.HasDiscount);
    }
}

public class CategoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Category_requires_name_and_valid_slug()
    {
        Assert.Throws<DomainException>(() => Category.Create(" ", "a", null, 0, true, null, null, Now));
        Assert.Throws<DomainException>(() => Category.Create("A", "Not valid", null, 0, true, null, null, Now));
        Assert.Throws<DomainException>(() => Category.Create("A", "a", null, -1, true, null, null, Now));
    }

    [Fact]
    public void Blank_optional_texts_are_stored_as_null()
    {
        var category = Category.Create(" Džemovi ", "dzemovi", "  ", 3, true, "", null, Now);

        Assert.Equal("Džemovi", category.Name);
        Assert.Null(category.Description);
        Assert.Null(category.SeoTitle);
    }

    [Fact]
    public void Archived_category_cannot_be_changed()
    {
        var category = Category.Create("A", "a", null, 0, true, null, null, Now);
        category.Archive(Now);

        Assert.Throws<DomainException>(() => category.Update("B", "b", null, 0, true, null, null, Now));
    }

    [Fact]
    public void Image_is_set_and_removed_together_with_its_size()
    {
        var category = Category.Create("A", "a", null, 0, true, null, null, Now);
        category.SetImage("media/categories/x", 1200, 800, Now);
        Assert.Equal(1200, category.ImageWidth);

        category.RemoveImage(Now);
        Assert.Null(category.ImageKey);
        Assert.Null(category.ImageWidth);
        Assert.Null(category.ImageHeight);
    }
}

public class StockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 5, StockStatus.Out)]
    [InlineData(1, 5, StockStatus.Low)]
    [InlineData(5, 5, StockStatus.Low)]
    [InlineData(6, 5, StockStatus.Ok)]
    [InlineData(1, 0, StockStatus.Ok)]
    [InlineData(0, 0, StockStatus.Out)]
    public void Status_compares_stock_with_threshold(int quantity, int threshold, StockStatus expected) =>
        Assert.Equal(expected, StockRules.StatusOf(quantity, threshold));

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(3, 20, 3)]
    [InlineData(500, 20, 20)]
    [InlineData(5, 0, 0)]
    public void Max_order_quantity_is_stock_limited_by_cap(int quantity, int cap, int expected) =>
        Assert.Equal(expected, StockRules.MaxOrderQuantity(quantity, cap));

    [Fact]
    public void Inventory_rejects_negative_values()
    {
        Assert.Throws<DomainException>(() => InventoryItem.Create(Guid.CreateVersion7(), -1, 0, Now));
        Assert.Throws<DomainException>(() => InventoryItem.Create(Guid.CreateVersion7(), 0, -1, Now));
    }

    [Fact]
    public void Manual_adjustment_needs_a_reason()
    {
        Assert.Throws<DomainException>(() =>
            InventoryMovement.Create(Guid.CreateVersion7(), InventoryMovementType.ManualAdjustment, 2, 5, "  ", null, null, Now));

        var movement = InventoryMovement.Create(Guid.CreateVersion7(), InventoryMovementType.ManualAdjustment, -2, 3, " Rastur ", null, null, Now);
        Assert.Equal("Rastur", movement.Reason);
    }

    [Fact]
    public void Movement_must_change_quantity_and_keep_stock_non_negative()
    {
        Assert.Throws<DomainException>(() =>
            InventoryMovement.Create(Guid.CreateVersion7(), InventoryMovementType.InitialStock, 0, 0, null, null, null, Now));
        Assert.Throws<DomainException>(() =>
            InventoryMovement.Create(Guid.CreateVersion7(), InventoryMovementType.OrderPlaced, -1, -1, null, null, null, Now));
    }

    [Theory]
    [InlineData(InventoryMovementType.InitialStock, -1)]
    [InlineData(InventoryMovementType.OrderPlaced, 1)]
    [InlineData(InventoryMovementType.OrderCancelled, -1)]
    [InlineData(InventoryMovementType.ReturnRestock, -1)]
    public void Movement_sign_must_match_its_type(InventoryMovementType type, int delta) =>
        Assert.Throws<DomainException>(() => InventoryMovement.Create(Guid.CreateVersion7(), type, delta, 5, null, null, null, Now));
}
