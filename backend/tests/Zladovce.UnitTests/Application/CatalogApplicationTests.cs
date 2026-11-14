using FluentValidation;
using Zladovce.Application.Catalog;
using Zladovce.Application.Categories;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Inventory;
using Zladovce.Application.Media;
using Zladovce.Domain.Catalog;

namespace Zladovce.UnitTests.Application;

public class RenditionPlanTests
{
    [Theory]
    [InlineData(3000, new[] { 400, 800, 1600 })]
    [InlineData(1600, new[] { 400, 800, 1600 })]
    [InlineData(1000, new[] { 400, 800, 1000 })]
    [InlineData(800, new[] { 400, 800 })]
    [InlineData(500, new[] { 400, 500 })]
    [InlineData(300, new[] { 300 })]
    [InlineData(1, new[] { 1 })]
    public void Widths_never_exceed_the_original(int original, int[] expected) =>
        Assert.Equal(expected, RenditionPlan.WidthsFor(original));

    [Fact]
    public void Zero_width_is_refused() => Assert.Throws<ArgumentOutOfRangeException>(() => RenditionPlan.WidthsFor(0));
}

public class MediaKeysTests
{
    [Fact]
    public void Upload_keys_round_trip()
    {
        var id = Guid.CreateVersion7();
        var key = MediaKeys.UploadKey(MediaPurpose.Category, id, "image/webp");

        Assert.Equal($"uploads/category/{id:D}.webp", key);
        Assert.True(MediaKeys.TryParseUploadKey(key, out var purpose, out var parsed));
        Assert.Equal(MediaPurpose.Category, purpose);
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("media/products/01a1d923-9e96-7f09-9170-11d7a2f5eec1.jpg")]
    [InlineData("uploads/other/01a1d923-9e96-7f09-9170-11d7a2f5eec1.jpg")]
    [InlineData("uploads/product/01a1d923-9e96-7f09-9170-11d7a2f5eec1.gif")]
    [InlineData("uploads/product/01A1D923-9E96-7F09-9170-11D7A2F5EEC1.jpg")]
    [InlineData("uploads/product/../01a1d923-9e96-7f09-9170-11d7a2f5eec1.jpg")]
    [InlineData("uploads/product/00000000-0000-0000-0000-000000000000.jpg")]
    public void Anything_else_is_not_an_upload_key(string? key) =>
        Assert.False(MediaKeys.TryParseUploadKey(key, out _, out _));

    [Fact]
    public void Storage_and_rendition_keys_follow_the_layout()
    {
        var id = Guid.Parse("01a1d923-9e96-7f09-9170-11d7a2f5eec1");
        var storage = MediaKeys.StorageKey(MediaPurpose.Product, id);

        Assert.Equal("media/products/01a1d923-9e96-7f09-9170-11d7a2f5eec1", storage);
        Assert.Equal(storage + "/800.webp", MediaKeys.RenditionKey(storage, 800));
        Assert.Equal(
            [storage + "/400.webp", storage + "/500.webp", storage + "/manifest.json"],
            MediaKeys.AllKeys(storage, 500));
        Assert.Equal("media/categories/01a1d923-9e96-7f09-9170-11d7a2f5eec1", MediaKeys.StorageKey(MediaPurpose.Category, id));
    }

    [Fact]
    public void Public_url_joins_base_url_and_key() =>
        Assert.Equal("https://cdn.example/media/x", new MediaOptions { BaseUrl = "https://cdn.example/" }.UrlFor("media/x"));
}

public class SlugAllocatorTests
{
    [Fact]
    public async Task Requested_slug_is_used_when_free() =>
        Assert.Equal("moj-slug", await SlugAllocator.AllocateAsync("moj-slug", "Ime", _ => Task.FromResult(false)));

    [Fact]
    public async Task Requested_slug_that_is_taken_is_a_conflict()
    {
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            SlugAllocator.AllocateAsync("moj-slug", "Ime", _ => Task.FromResult(true)));
        Assert.Equal(ErrorCodes.SlugTaken, exception.Code);
        Assert.Equal(["slug"], exception.Errors!.Keys);
    }

    [Fact]
    public async Task Generated_slug_gets_a_suffix_when_taken()
    {
        var taken = new HashSet<string> { "dzem", "dzem-2" };
        Assert.Equal("dzem-3", await SlugAllocator.AllocateAsync(null, "Džem", s => Task.FromResult(taken.Contains(s))));
    }

    [Fact]
    public async Task Name_without_letters_cannot_produce_a_slug()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            SlugAllocator.AllocateAsync(null, "!!!", _ => Task.FromResult(false)));
        Assert.True(exception.Errors.ContainsKey("name"));
    }
}

public class HtmlContentSanitizerTests
{
    private readonly HtmlContentSanitizer _sanitizer = new();

    [Fact]
    public void Scripts_handlers_and_styles_are_removed()
    {
        var clean = _sanitizer.Sanitize(
            "<p onclick=\"x()\" style=\"color:red\">Tekst <strong>jak</strong></p><script>alert(1)</script><img src=x onerror=y>");

        Assert.Equal("<p>Tekst <strong>jak</strong></p>", clean);
    }

    [Fact]
    public void Javascript_links_lose_their_href() =>
        Assert.DoesNotContain("javascript", _sanitizer.Sanitize("<a href=\"javascript:alert(1)\">x</a>"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Blank_input_gives_empty_string() => Assert.Equal(string.Empty, _sanitizer.Sanitize("  "));
}

public class CatalogValidatorTests
{
    private static CreateVariantRequest ValidVariant(string sku = "DZ-370") => new()
    {
        Name = "370 g",
        Sku = sku,
        UnitAmount = 370m,
        MeasurementUnit = MeasurementUnit.Gram,
        RegularPrice = 650m,
        DiscountPrice = 590m,
        IsActive = true,
        InitialStock = 10,
        LowStockThreshold = 2,
    };

    private static CreateProductRequest ValidProduct() => new()
    {
        CategoryId = Guid.CreateVersion7(),
        Name = "Džem",
        Sku = "DZ",
        Variants = [ValidVariant()],
    };

    private static Dictionary<string, string[]> Errors<T>(IValidator<T> validator, T request) =>
        validator.Validate(request).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    [Fact]
    public void Valid_product_passes() => Assert.Empty(Errors(new CreateProductRequestValidator(), ValidProduct()));

    [Fact]
    public void Product_without_variants_fails() =>
        Assert.Contains("Variants", Errors(new CreateProductRequestValidator(), ValidProduct() with { Variants = [] }).Keys);

    [Fact]
    public void Duplicate_variant_skus_in_one_request_fail() =>
        Assert.Contains("Variants", Errors(new CreateProductRequestValidator(), ValidProduct() with { Variants = [ValidVariant("A"), ValidVariant("a")] }).Keys);

    [Fact]
    public void Active_product_with_only_inactive_variants_fails() =>
        Assert.Contains("Variants", Errors(new CreateProductRequestValidator(), ValidProduct() with { Variants = [ValidVariant() with { IsActive = false }] }).Keys);

    [Fact]
    public void Variant_errors_are_reported_per_index()
    {
        var errors = Errors(new CreateProductRequestValidator(), ValidProduct() with
        {
            Variants = [ValidVariant() with { RegularPrice = 0m, DiscountPrice = null, InitialStock = -1, Sku = "ne valja" }],
        });

        Assert.Contains("Variants[0].RegularPrice", errors.Keys);
        Assert.Contains("Variants[0].InitialStock", errors.Keys);
        Assert.Contains("Variants[0].Sku", errors.Keys);
    }

    [Theory]
    [InlineData(650.0, 650.0)]
    [InlineData(650.0, 700.0)]
    [InlineData(650.0, 10.123)]
    public void Discount_must_be_lower_with_two_decimals(double regular, double discount) =>
        Assert.Contains("DiscountPrice", Errors(new VariantRequestValidator(), new VariantRequest
        {
            Name = "x",
            Sku = "X",
            UnitAmount = 1m,
            MeasurementUnit = MeasurementUnit.Piece,
            RegularPrice = (decimal)regular,
            DiscountPrice = (decimal)discount,
            IsActive = true,
        }).Keys);

    [Fact]
    public void Unit_amount_and_measurement_unit_are_required()
    {
        var errors = Errors(new VariantRequestValidator(), new VariantRequest
        {
            Name = "x",
            Sku = "X",
            UnitAmount = null,
            MeasurementUnit = null,
            RegularPrice = 1m,
            DiscountPrice = null,
            IsActive = true,
        });
        Assert.Contains("UnitAmount", errors.Keys);
        Assert.Contains("MeasurementUnit", errors.Keys);
    }

    [Fact]
    public void Update_requires_version() =>
        Assert.Contains("Version", Errors(new UpdateProductRequestValidator(), new UpdateProductRequest
        {
            Version = null,
            CategoryId = Guid.CreateVersion7(),
            Name = "x",
            Slug = null,
            ShortDescription = null,
            Description = null,
            Sku = "X",
            IsActive = true,
            IsFeatured = false,
            SeoTitle = null,
            SeoDescription = null,
        }).Keys);

    [Fact]
    public void Invalid_slug_is_refused() =>
        Assert.Contains("Slug", Errors(new CreateProductRequestValidator(), ValidProduct() with { Slug = "Džem" }).Keys);

    [Fact]
    public void Category_name_is_required_and_slug_validated()
    {
        var errors = Errors(new SaveCategoryRequestValidator(), new SaveCategoryRequest
        {
            Name = null!,
            Slug = "Bad Slug",
            Description = null,
            ImageKey = null,
            SortOrder = 0,
            IsActive = true,
            SeoTitle = null,
            SeoDescription = null,
        });
        Assert.Contains("Name", errors.Keys);
        Assert.Contains("Slug", errors.Keys);
    }

    [Fact]
    public void Adjustment_needs_non_zero_delta_and_reason()
    {
        var errors = Errors(new AdjustStockRequestValidator(), new AdjustStockRequest(0, " "));
        Assert.Contains("QuantityDelta", errors.Keys);
        Assert.Contains("Reason", errors.Keys);
        Assert.Empty(Errors(new AdjustStockRequestValidator(), new AdjustStockRequest(-3, "Rastur")));
    }

    [Fact]
    public void Threshold_must_not_be_negative() =>
        Assert.Contains("LowStockThreshold", Errors(new SetLowStockThresholdRequestValidator(), new SetLowStockThresholdRequest { LowStockThreshold = -1 }).Keys);

    [Theory]
    [InlineData("image/gif", 100, "product")]
    [InlineData("image/jpeg", 0, "product")]
    [InlineData("image/jpeg", 10 * 1024 * 1024 + 1, "product")]
    [InlineData("image/jpeg", 100, "blog")]
    public void Upload_requests_are_limited(string contentType, long size, string purpose) =>
        Assert.NotEmpty(Errors(new CreateUploadUrlRequestValidator(), new CreateUploadUrlRequest("a.jpg", contentType, size, purpose)));

    [Fact]
    public void Valid_upload_request_passes() =>
        Assert.Empty(Errors(new CreateUploadUrlRequestValidator(), new CreateUploadUrlRequest("a.png", "image/png", 1234, "category")));

    [Fact]
    public void Variant_sort_order_defaults_to_the_given_position()
    {
        Assert.Equal(3, ValidVariant().ToValues(3).SortOrder);
        Assert.Equal(7, (ValidVariant() with { SortOrder = 7 }).ToValues(3).SortOrder);
    }
}
