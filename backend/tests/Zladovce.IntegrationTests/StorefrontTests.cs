using System.Net;
using System.Text;
using System.Text.Json;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CartQuoteTests : IAsyncLifetime
{
    private const string QuoteUrl = "/api/v1/cart/quote";

    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _api;

    public CartQuoteTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _api = new AdminApi(_client);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Store settings are a singleton shared by every test: back to the state of the first migration.
        await SetShippingAsync(true, 0m, null, null);
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task SetShippingAsync(bool enabled, decimal price, decimal? freeThreshold, decimal? minimum) =>
        _postgres.ExecuteAsync(
            "UPDATE store_settings SET shipping_enabled = @enabled, shipping_price = @price, " +
            "free_shipping_threshold = @threshold, min_order_amount = @minimum WHERE id = 1",
            ("enabled", enabled), ("price", price),
            ("threshold", (object?)freeThreshold ?? DBNull.Value), ("minimum", (object?)minimum ?? DBNull.Value));

    private Task<HttpResponseMessage> PostAsync(string json) =>
        _client.PostAsync(QuoteUrl, new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<(JsonElement Body, string Text)> QuoteAsync(params object[] items)
    {
        var response = await PostAsync(JsonSerializer.Serialize(new { items }));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"quote: {(int)response.StatusCode} {text}");
        return (JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    private static object Line(Guid variantId, int quantity, decimal? knownUnitPrice = null) =>
        knownUnitPrice is null ? new { variantId, quantity } : new { variantId, quantity, knownUnitPrice };

    private static Guid VariantId(JsonElement product, int index) => product.GetProperty("variants")[index].Id();

    private static JsonElement[] Lines(JsonElement quote) => [.. quote.GetProperty("lines").EnumerateArray()];

    private static JsonElement[] Adjustments(JsonElement quote) => [.. quote.GetProperty("adjustments").EnumerateArray()];

    private static decimal Money(JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    private async Task<JsonElement> ProductAsync(params object[] variants)
    {
        var category = await _api.CreateCategoryAsync();
        return await _api.CreateProductAsync(category.Id(), variants: variants);
    }

    [Fact]
    public async Task Prices_two_variants_with_a_discount_from_the_database()
    {
        await SetShippingAsync(true, 350m, 10_000m, null);
        var token = Unique.Token();
        var product = await ProductAsync(
            AdminApi.Variant($"A-{token}", 650m, 590m, stock: 8, name: "370 g"),
            AdminApi.Variant($"B-{token}", 1100m, null, stock: 500, name: "720 g"));
        var imageId = Guid.CreateVersion7();
        await _postgres.ExecuteAsync(
            "INSERT INTO product_images (id, product_id, storage_key, alt_text, is_main, sort_order, width, height, created_at) " +
            "VALUES (@id, @product, @key, 'Tegla', true, 0, 1000, 800, now())",
            ("id", imageId), ("product", product.Id()), ("key", $"media/products/{imageId}"));

        var (quote, text) = await QuoteAsync(Line(VariantId(product, 0), 2), Line(VariantId(product, 1), 1));

        Assert.DoesNotContain("quantityOnHand", text);
        Assert.DoesNotContain("\"stock\"", text);
        Assert.DoesNotContain("500", text);
        Assert.Empty(Adjustments(quote));

        var lines = Lines(quote);
        Assert.Equal([VariantId(product, 0), VariantId(product, 1)], lines.Select(l => l.GetProperty("variantId").GetGuid()));
        var first = lines[0];
        Assert.Equal(product.Id(), first.GetProperty("productId").GetGuid());
        Assert.Equal(product.GetProperty("name").GetString(), first.GetProperty("productName").GetString());
        Assert.Equal(product.GetProperty("slug").GetString(), first.GetProperty("productSlug").GetString());
        Assert.Equal("370 g", first.GetProperty("variantName").GetString());
        Assert.Equal($"A-{token}", first.GetProperty("sku").GetString(), ignoreCase: true);
        Assert.Equal(370m, Money(first, "unitAmount"));
        Assert.Equal("g", first.GetProperty("measurementUnit").GetString());
        Assert.Equal(590m, Money(first, "unitPrice"));
        Assert.Equal(650m, Money(first, "regularPrice"));
        Assert.Equal(590m, Money(first, "discountPrice"));
        Assert.Equal(2, first.GetProperty("requestedQuantity").GetInt32());
        Assert.Equal(2, first.GetProperty("quantity").GetInt32());
        Assert.Equal(1180m, Money(first, "lineTotal"));
        Assert.Equal(8, first.GetProperty("maxOrderQuantity").GetInt32());

        var image = first.GetProperty("image");
        Assert.EndsWith($"/media/products/{imageId}", image.GetProperty("baseUrl").GetString());
        Assert.Equal([400, 800, 1000], image.GetProperty("widths").EnumerateArray().Select(w => w.GetInt32()));
        Assert.Equal(1000, image.GetProperty("width").GetInt32());
        Assert.Equal("Tegla", image.GetProperty("altText").GetString());

        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("discountPrice").ValueKind);
        Assert.Equal(20, lines[1].GetProperty("maxOrderQuantity").GetInt32());

        Assert.Equal(2280m, Money(quote, "subtotal"));
        var shipping = quote.GetProperty("shipping");
        Assert.True(shipping.GetProperty("enabled").GetBoolean());
        Assert.Equal(350m, Money(shipping, "price"));
        Assert.Equal(350m, Money(shipping, "basePrice"));
        Assert.Equal(10_000m, Money(shipping, "freeShippingThreshold"));
        Assert.False(shipping.GetProperty("isFree").GetBoolean());
        Assert.Equal(7720m, Money(shipping, "amountUntilFreeShipping"));
        Assert.Equal(JsonValueKind.Null, quote.GetProperty("minOrderAmount").ValueKind);
        Assert.True(quote.GetProperty("meetsMinimumOrder").GetBoolean());
        Assert.Equal(JsonValueKind.Null, quote.GetProperty("amountUntilMinimumOrder").ValueKind);
        Assert.Equal(2630m, Money(quote, "total"));
        Assert.True(quote.GetProperty("canCheckout").GetBoolean());
        Assert.Equal("RSD", quote.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Free_shipping_starts_exactly_at_the_threshold()
    {
        await SetShippingAsync(true, 450m, 3000m, null);
        var token = Unique.Token();
        var product = await ProductAsync(
            AdminApi.Variant($"A-{token}", 1500m, stock: 50),
            AdminApi.Variant($"B-{token}", 1499.99m, stock: 50, name: "720 g"));

        var (below, _) = await QuoteAsync(Line(VariantId(product, 0), 1), Line(VariantId(product, 1), 1));
        Assert.Equal(2999.99m, Money(below, "subtotal"));
        Assert.Equal(450m, Money(below.GetProperty("shipping"), "price"));
        Assert.False(below.GetProperty("shipping").GetProperty("isFree").GetBoolean());
        Assert.Equal(0.01m, Money(below.GetProperty("shipping"), "amountUntilFreeShipping"));
        Assert.Equal(3449.99m, Money(below, "total"));

        var (exact, _) = await QuoteAsync(Line(VariantId(product, 0), 2));
        Assert.Equal(3000m, Money(exact, "subtotal"));
        Assert.Equal(0m, Money(exact.GetProperty("shipping"), "price"));
        Assert.True(exact.GetProperty("shipping").GetProperty("isFree").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exact.GetProperty("shipping").GetProperty("amountUntilFreeShipping").ValueKind);
        Assert.Equal(3000m, Money(exact, "total"));
    }

    [Fact]
    public async Task Minimum_order_amount_is_compared_with_the_subtotal()
    {
        await SetShippingAsync(true, 450m, null, 2000m);
        var product = await ProductAsync(AdminApi.Variant($"M-{Unique.Token()}", 1000m, stock: 50));

        var (below, _) = await QuoteAsync(Line(VariantId(product, 0), 1));
        Assert.Equal(2000m, Money(below, "minOrderAmount"));
        Assert.False(below.GetProperty("meetsMinimumOrder").GetBoolean());
        Assert.Equal(1000m, Money(below, "amountUntilMinimumOrder"));
        Assert.Equal(1450m, Money(below, "total"));
        Assert.False(below.GetProperty("canCheckout").GetBoolean());

        var (exact, _) = await QuoteAsync(Line(VariantId(product, 0), 2));
        Assert.True(exact.GetProperty("meetsMinimumOrder").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exact.GetProperty("amountUntilMinimumOrder").ValueKind);
        Assert.True(exact.GetProperty("canCheckout").GetBoolean());
    }

    [Fact]
    public async Task Disabled_shipping_prevents_checkout()
    {
        await SetShippingAsync(false, 450m, null, null);
        var product = await ProductAsync(AdminApi.Variant($"S-{Unique.Token()}", 1000m));

        var (quote, _) = await QuoteAsync(Line(VariantId(product, 0), 1));

        Assert.Single(Lines(quote));
        Assert.False(quote.GetProperty("shipping").GetProperty("enabled").GetBoolean());
        Assert.Equal(0m, Money(quote.GetProperty("shipping"), "price"));
        Assert.Equal(1000m, Money(quote, "total"));
        Assert.False(quote.GetProperty("canCheckout").GetBoolean());
    }

    [Fact]
    public async Task Variants_that_cannot_be_bought_are_removed_and_reported_as_unavailable()
    {
        var token = Unique.Token();

        var inactiveProduct = await ProductAsync(AdminApi.Variant($"IP-{token}"));
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{inactiveProduct.Id()}", new
        {
            version = inactiveProduct.GetProperty("version").GetUInt32(),
            categoryId = inactiveProduct.GetProperty("category").Id(),
            name = inactiveProduct.GetProperty("name").GetString(),
            slug = inactiveProduct.GetProperty("slug").GetString(),
            shortDescription = "Kratak opis",
            description = "<p>Opis</p>",
            sku = inactiveProduct.GetProperty("sku").GetString(),
            isActive = false,
            isFeatured = false,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
        });

        var withArchivedVariant = await ProductAsync(AdminApi.Variant($"AV1-{token}"), AdminApi.Variant($"AV2-{token}", name: "1 kg"));
        await _api.JsonAsync(HttpMethod.Delete,
            $"/api/v1/admin/products/{withArchivedVariant.Id()}/variants/{VariantId(withArchivedVariant, 1)}");

        var inactiveCategory = await _api.CreateCategoryAsync();
        var inHiddenCategory = await _api.CreateProductAsync(inactiveCategory.Id(), variants: [AdminApi.Variant($"IC-{token}")]);
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{inactiveCategory.Id()}",
            AdminApi.Category(inactiveCategory.GetProperty("name").GetString()!, isActive: false));

        var outOfStock = await ProductAsync(AdminApi.Variant($"OS-{token}", stock: 0));
        var inactiveVariant = await ProductAsync(AdminApi.Variant($"IV1-{token}"), AdminApi.Variant($"IV2-{token}", isActive: false, name: "1 kg"));
        var fine = await ProductAsync(AdminApi.Variant($"OK-{token}", 700m));
        var unknown = Guid.CreateVersion7();

        var (quote, _) = await QuoteAsync(
            Line(VariantId(inactiveProduct, 0), 1),
            Line(VariantId(withArchivedVariant, 1), 2),
            Line(VariantId(inHiddenCategory, 0), 3),
            Line(VariantId(outOfStock, 0), 4),
            Line(VariantId(inactiveVariant, 1), 5),
            Line(unknown, 6),
            Line(VariantId(fine, 0), 1));

        Assert.Equal([VariantId(fine, 0)], Lines(quote).Select(l => l.GetProperty("variantId").GetGuid()));
        var adjustments = Adjustments(quote);
        Assert.Equal(
            [VariantId(inactiveProduct, 0), VariantId(withArchivedVariant, 1), VariantId(inHiddenCategory, 0),
                VariantId(outOfStock, 0), VariantId(inactiveVariant, 1), unknown],
            adjustments.Select(a => a.GetProperty("variantId").GetGuid()));
        Assert.All(adjustments, a =>
        {
            Assert.Equal("unavailable", a.GetProperty("reason").GetString());
            Assert.Equal(0, a.GetProperty("availableQuantity").GetInt32());
            Assert.Equal(JsonValueKind.Null, a.GetProperty("previousUnitPrice").ValueKind);
        });
        Assert.Equal([1, 2, 3, 4, 5, 6], adjustments.Select(a => a.GetProperty("requestedQuantity").GetInt32()));
        Assert.Equal(700m, Money(quote, "subtotal"));
    }

    [Fact]
    public async Task Quantities_are_capped_by_max_order_quantity_without_revealing_the_stock()
    {
        var token = Unique.Token();
        var product = await ProductAsync(
            AdminApi.Variant($"L-{token}", 100m, stock: 3),
            AdminApi.Variant($"H-{token}", 200m, stock: 777, name: "720 g"));

        var (quote, text) = await QuoteAsync(Line(VariantId(product, 0), 5), Line(VariantId(product, 1), 30));

        Assert.DoesNotContain("777", text);
        var lines = Lines(quote);
        Assert.Equal([3, 20], lines.Select(l => l.GetProperty("quantity").GetInt32()));
        Assert.Equal([5, 30], lines.Select(l => l.GetProperty("requestedQuantity").GetInt32()));
        Assert.Equal([300m, 4000m], lines.Select(l => Money(l, "lineTotal")));
        var adjustments = Adjustments(quote);
        Assert.All(adjustments, a => Assert.Equal("quantityReduced", a.GetProperty("reason").GetString()));
        Assert.Equal([3, 20], adjustments.Select(a => a.GetProperty("availableQuantity").GetInt32()));
        Assert.Equal(4300m, Money(quote, "subtotal"));
    }

    [Fact]
    public async Task A_known_price_that_changed_is_reported()
    {
        var token = Unique.Token();
        var product = await ProductAsync(
            AdminApi.Variant($"P1-{token}", 650m, 590m),
            AdminApi.Variant($"P2-{token}", 1100m, name: "720 g"));

        var (unchanged, _) = await QuoteAsync(Line(VariantId(product, 0), 1, 590m), Line(VariantId(product, 1), 1, 1100m));
        Assert.Empty(Adjustments(unchanged));

        // The administrator raises the price of the second variant after the customer saw it.
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{VariantId(product, 1)}", new
        {
            name = "720 g", sku = $"P2-{token}", unitAmount = 720m, measurementUnit = "g",
            regularPrice = 1250m, discountPrice = (decimal?)null, isActive = true,
        });

        var (quote, _) = await QuoteAsync(Line(VariantId(product, 0), 1, 650m), Line(VariantId(product, 1), 2, 1100m));

        var adjustments = Adjustments(quote);
        Assert.All(adjustments, a => Assert.Equal("priceChanged", a.GetProperty("reason").GetString()));
        Assert.Equal([(650m, 590m), (1100m, 1250m)],
            adjustments.Select(a => (Money(a, "previousUnitPrice"), Money(a, "unitPrice"))));
        Assert.Equal(2, adjustments[1].GetProperty("availableQuantity").GetInt32());
        Assert.Equal([590m, 1250m], Lines(quote).Select(l => Money(l, "unitPrice")));
        Assert.Equal(3090m, Money(quote, "subtotal"));
    }

    [Fact]
    public async Task Duplicate_lines_are_merged()
    {
        var product = await ProductAsync(AdminApi.Variant($"D-{Unique.Token()}", 300m, stock: 50));
        var id = VariantId(product, 0);

        var (quote, _) = await QuoteAsync(Line(id, 2), Line(id, 3));

        var line = Assert.Single(Lines(quote));
        Assert.Equal(5, line.GetProperty("quantity").GetInt32());
        Assert.Equal(5, line.GetProperty("requestedQuantity").GetInt32());
        Assert.Equal(1500m, Money(line, "lineTotal"));
        Assert.Empty(Adjustments(quote));
    }

    [Fact]
    public async Task Invalid_requests_are_refused_with_field_paths()
    {
        var id = Guid.CreateVersion7();

        async Task<JsonElement> Invalid(string json, string code = "VALIDATION_FAILED")
        {
            var response = await PostAsync(json);
            return await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        }

        static string[] Fields(JsonElement problem) => [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

        Assert.Equal(["items"], Fields(await Invalid("""{ "items": [] }""")));
        Assert.Equal(["items"], Fields(await Invalid("{}")));
        Assert.Equal(["items"], Fields(await Invalid("""{ "items": null }""")));
        Assert.Equal(["items[0].quantity"], Fields(await Invalid($$"""{ "items": [{ "variantId": "{{id}}", "quantity": 0 }] }""")));
        Assert.Equal(["items[1].quantity"], Fields(await Invalid(
            $$"""{ "items": [{ "variantId": "{{id}}", "quantity": 1 }, { "variantId": "{{id}}", "quantity": 100 }] }""")));
        Assert.Equal(["items[0].quantity"], Fields(await Invalid($$"""{ "items": [{ "variantId": "{{id}}" }] }""")));
        Assert.Equal(["items[0].variantId"], Fields(await Invalid("""{ "items": [{ "variantId": "00000000-0000-0000-0000-000000000000", "quantity": 1 }] }""")));
        Assert.Equal(["items[0].knownUnitPrice"], Fields(await Invalid($$"""{ "items": [{ "variantId": "{{id}}", "quantity": 1, "knownUnitPrice": -1 }] }""")));

        var tooMany = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 51).Select(_ => new { variantId = Guid.NewGuid(), quantity = 1 }) });
        Assert.Equal(["items"], Fields(await Invalid(tooMany)));

        await Invalid("""{ "items": [{ "variantId": "not-a-uuid", "quantity": 1 }] }""", "BAD_REQUEST");
        await Invalid($$"""{ "items": [{ "variantId": "{{id}}", "quantity": "2" }] }""", "BAD_REQUEST");
        await Invalid("""{ "items": [""", "BAD_REQUEST");

        // 50 lines is the maximum and is accepted, even for unknown variants.
        var fifty = JsonSerializer.Serialize(new { items = Enumerable.Range(0, 50).Select(_ => new { variantId = Guid.NewGuid(), quantity = 99 }) });
        var ok = await PostAsync(fifty);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Storefront_origin_may_post_json_to_the_quote()
    {
        var preflight = new HttpRequestMessage(HttpMethod.Options, QuoteUrl);
        preflight.Headers.Add("Origin", "http://localhost:4200");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await _client.SendAsync(preflight);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
        Assert.Equal("http://localhost:4200", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        Assert.Contains("content-type", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);

        var post = new HttpRequestMessage(HttpMethod.Post, QuoteUrl)
        {
            Content = new StringContent($$"""{ "items": [{ "variantId": "{{Guid.NewGuid()}}", "quantity": 1 }] }""", Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("Origin", "http://localhost:4200");
        var actual = await _client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Equal("http://localhost:4200", actual.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var foreign = new HttpRequestMessage(HttpMethod.Options, QuoteUrl);
        foreign.Headers.Add("Origin", "https://evil.example");
        foreign.Headers.Add("Access-Control-Request-Method", "POST");
        Assert.False((await _client.SendAsync(foreign)).Headers.Contains("Access-Control-Allow-Origin"));
    }
}

[Collection(PostgresCollection.Name)]
public class SeoRoutesTests : IDisposable
{
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _api;

    public SeoRoutesTests(PostgresFixture postgres, MinioFixture minio)
    {
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _api = new AdminApi(_client);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Lists_only_what_the_public_catalog_shows()
    {
        var category = await _api.CreateCategoryAsync();
        var hiddenCategory = await _api.CreateCategoryAsync(isActive: false);
        var archivedCategory = await _api.CreateCategoryAsync();
        await _api.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{archivedCategory.Id()}", expected: HttpStatusCode.NoContent);

        var visible = await _api.CreateProductAsync(category.Id());
        var inactive = await _api.CreateProductAsync(category.Id(), isActive: false);
        var archived = await _api.CreateProductAsync(category.Id());
        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{archived.Id()}/archive");
        var inHiddenCategory = await _api.CreateProductAsync(hiddenCategory.Id());

        var response = await _client.GetAsync("/api/v1/seo/routes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var routes = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var categorySlugs = routes.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToList();
        Assert.Contains(category.GetProperty("slug").GetString(), categorySlugs);
        Assert.DoesNotContain(hiddenCategory.GetProperty("slug").GetString(), categorySlugs);
        Assert.DoesNotContain(archivedCategory.GetProperty("slug").GetString(), categorySlugs);
        Assert.Equal(categorySlugs.Order(StringComparer.Ordinal), categorySlugs);

        var products = routes.GetProperty("products").EnumerateArray().ToList();
        var productSlugs = products.Select(p => p.GetProperty("slug").GetString()).ToList();
        Assert.Contains(visible.GetProperty("slug").GetString(), productSlugs);
        Assert.DoesNotContain(inactive.GetProperty("slug").GetString(), productSlugs);
        Assert.DoesNotContain(archived.GetProperty("slug").GetString(), productSlugs);
        Assert.DoesNotContain(inHiddenCategory.GetProperty("slug").GetString(), productSlugs);

        var entry = products.Single(p => p.GetProperty("slug").GetString() == visible.GetProperty("slug").GetString());
        Assert.Equal(["slug", "updatedAt"], entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal(visible.GetProperty("updatedAt").GetDateTimeOffset(), entry.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(["categories", "products", "blogPosts", "blogCategories", "pages"], routes.EnumerateObject().Select(p => p.Name));
    }
}
