using System.Net;
using System.Text.Json;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PublicCatalogTests : IDisposable
{
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _api;

    public PublicCatalogTests(PostgresFixture postgres, MinioFixture minio)
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

    private async Task<JsonElement> PublicAsync(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"GET {url}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> GridAsync(string categorySlug, string extra = "") =>
        PublicAsync($"/api/v1/catalog/products?category={categorySlug}{extra}");

    [Fact]
    public async Task Only_active_non_archived_products_with_an_active_variant_in_active_categories_are_visible()
    {
        var category = await _api.CreateCategoryAsync();
        var slug = category.GetProperty("slug").GetString()!;
        var visible = await _api.CreateProductAsync(category.Id(), "Vidljiv");
        await _api.CreateProductAsync(category.Id(), "Neaktivan", isActive: false);
        var archived = await _api.CreateProductAsync(category.Id(), "Arhiviran");
        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{archived.Id()}/archive");
        var noActiveVariant = await _api.CreateProductAsync(category.Id(), "Bez varijante", isActive: false,
            variants: [AdminApi.Variant($"NV-{Unique.Token()}", isActive: false)]);

        var grid = await GridAsync(slug);
        Assert.Equal([visible.Id()], grid.Items().Select(p => p.Id()));

        var categories = await PublicAsync("/api/v1/catalog/categories");
        Assert.Equal(1, categories.Items().Single(c => c.Id() == category.Id()).GetProperty("productCount").GetInt32());

        await PublicAsync($"/api/v1/catalog/products/{archived.GetProperty("slug").GetString()}", HttpStatusCode.NotFound);
        await PublicAsync($"/api/v1/catalog/products/{noActiveVariant.GetProperty("slug").GetString()}", HttpStatusCode.NotFound);
        await PublicAsync($"/api/v1/catalog/products/{visible.GetProperty("slug").GetString()}");

        // An inactive category hides itself and its products.
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}", AdminApi.Category(category.GetProperty("name").GetString()!, isActive: false));
        Assert.Empty((await GridAsync(slug)).Items());
        await PublicAsync($"/api/v1/catalog/categories/{slug}", HttpStatusCode.NotFound);
        await PublicAsync($"/api/v1/catalog/products/{visible.GetProperty("slug").GetString()}", HttpStatusCode.NotFound);
        Assert.DoesNotContain((await PublicAsync("/api/v1/catalog/categories")).Items(), c => c.Id() == category.Id());
    }

    [Fact]
    public async Task Product_page_shows_prices_and_availability_but_never_the_exact_stock()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var product = await _api.CreateProductAsync(category.Id(), variants:
        [
            AdminApi.Variant($"A-{token}", 650m, 590m, stock: 3, name: "370 g"),
            AdminApi.Variant($"B-{token}", 1100m, null, stock: 500, name: "720 g"),
            AdminApi.Variant($"C-{token}", 2000m, null, stock: 0, name: "1 kg"),
            AdminApi.Variant($"D-{token}", 100m, null, stock: 9, isActive: false, name: "skriveno"),
        ]);

        var response = await _client.GetAsync($"/api/v1/catalog/products/{product.GetProperty("slug").GetString()}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("quantityOnHand", text);
        Assert.DoesNotContain("lowStockThreshold", text);

        var page = JsonDocument.Parse(text).RootElement;
        Assert.Equal(590m, page.GetProperty("price").GetDecimal());
        Assert.Equal(650m, page.GetProperty("originalPrice").GetDecimal());
        Assert.True(page.GetProperty("hasPriceRange").GetBoolean());
        Assert.True(page.GetProperty("inStock").GetBoolean());
        Assert.StartsWith("Kategorija", page.GetProperty("category").GetProperty("name").GetString());

        var variants = page.GetProperty("variants").EnumerateArray().ToArray();
        Assert.Equal(["370 g", "720 g", "1 kg"], variants.Select(v => v.GetProperty("name").GetString()));
        Assert.Equal(590m, variants[0].GetProperty("price").GetDecimal());
        Assert.Equal(650m, variants[0].GetProperty("regularPrice").GetDecimal());
        Assert.Equal(590m, variants[0].GetProperty("discountPrice").GetDecimal());
        Assert.Equal(3, variants[0].GetProperty("maxOrderQuantity").GetInt32());
        Assert.Equal(20, variants[1].GetProperty("maxOrderQuantity").GetInt32());
        Assert.False(variants[2].GetProperty("inStock").GetBoolean());
        Assert.Equal(0, variants[2].GetProperty("maxOrderQuantity").GetInt32());

        var availability = await PublicAsync(
            $"/api/v1/catalog/availability?variantIds={variants[0].Id()},{variants[1].Id()}&variantIds={product.GetProperty("variants")[3].Id()}");
        var items = availability.Items();
        Assert.Equal(2, items.Length);
        Assert.All(items, i => Assert.False(i.TryGetProperty("quantityOnHand", out _)));
        Assert.Equal(20, items.Single(i => i.GetProperty("variantId").GetGuid() == variants[1].Id()).GetProperty("maxOrderQuantity").GetInt32());

        var invalid = await _client.GetAsync("/api/v1/catalog/availability?variantIds=abc");
        await ResponseAssertions.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Grid_filters_sorts_and_searches_without_diacritics()
    {
        var category = await _api.CreateCategoryAsync();
        var slug = category.GetProperty("slug").GetString()!;
        var token = Unique.Token();
        var cheap = await _api.CreateProductAsync(category.Id(), $"Šljiva {token}", variants: [AdminApi.Variant($"C-{token}", 300m, stock: 0)]);
        var mid = await _api.CreateProductAsync(category.Id(), $"Džem {token}", isFeatured: true, variants: [AdminApi.Variant($"M-{token}", 800m, 600m)]);
        var dear = await _api.CreateProductAsync(category.Id(), $"Ajvar {token}", variants: [AdminApi.Variant($"D-{token}", 1500m)]);

        Assert.Equal([dear.Id(), mid.Id(), cheap.Id()], (await GridAsync(slug)).Items().Select(p => p.Id()));
        Assert.Equal([dear.Id(), mid.Id(), cheap.Id()], (await GridAsync(slug, "&sort=bestselling")).Items().Select(p => p.Id()));
        Assert.Equal([cheap.Id(), mid.Id(), dear.Id()], (await GridAsync(slug, "&sort=priceAsc")).Items().Select(p => p.Id()));
        Assert.Equal([dear.Id(), mid.Id(), cheap.Id()], (await GridAsync(slug, "&sort=priceDesc")).Items().Select(p => p.Id()));
        Assert.Equal([dear.Id(), mid.Id(), cheap.Id()], (await GridAsync(slug, "&sort=name")).Items().Select(p => p.Id()));

        Assert.Equal([mid.Id()], (await GridAsync(slug, "&minPrice=500&maxPrice=700")).Items().Select(p => p.Id()));
        Assert.Equal([dear.Id(), mid.Id()], (await GridAsync(slug, "&inStock=true")).Items().Select(p => p.Id()));
        Assert.Equal([mid.Id()], (await GridAsync(slug, "&featured=true")).Items().Select(p => p.Id()));

        var search = await PublicAsync($"/api/v1/catalog/products?q={Uri.EscapeDataString($"dzem {token}")}");
        Assert.Equal([mid.Id()], search.Items().Select(p => p.Id()));
        Assert.Equal([cheap.Id()], (await PublicAsync($"/api/v1/catalog/products?q=SLJIVA%20{token}")).Items().Select(p => p.Id()));

        var item = (await GridAsync(slug, "&featured=true")).Items()[0];
        Assert.Equal(600m, item.GetProperty("price").GetDecimal());
        Assert.Equal(800m, item.GetProperty("originalPrice").GetDecimal());
        Assert.False(item.GetProperty("hasPriceRange").GetBoolean());

        var paged = await GridAsync(slug, "&sort=priceAsc&page=2&pageSize=2");
        Assert.Equal([dear.Id()], paged.Items().Select(p => p.Id()));
        Assert.Equal(3, paged.GetProperty("totalItems").GetInt32());

        var invalid = await _client.GetAsync($"/api/v1/catalog/products?sort=random&minPrice=10&maxPrice=1");
        await ResponseAssertions.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Category_detail_and_related_products()
    {
        var category = await _api.CreateCategoryAsync();
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}", AdminApi.Category(
            category.GetProperty("name").GetString()!,
            seoTitle: "SEO kategorije",
            seoDescription: "Opis za pretraživače"));
        var first = await _api.CreateProductAsync(category.Id());
        var second = await _api.CreateProductAsync(category.Id());
        var third = await _api.CreateProductAsync(category.Id(), isFeatured: true);

        var detail = await PublicAsync($"/api/v1/catalog/categories/{category.GetProperty("slug").GetString()}");
        Assert.Equal("SEO kategorije", detail.GetProperty("seo").GetProperty("title").GetString());
        Assert.Equal(3, detail.GetProperty("productCount").GetInt32());

        var related = await PublicAsync($"/api/v1/catalog/products/{first.GetProperty("slug").GetString()}/related?limit=5");
        Assert.Equal([third.Id(), second.Id()], related.Items().Select(p => p.Id()));

        await PublicAsync("/api/v1/catalog/products/ne-postoji/related", HttpStatusCode.NotFound);
        var badLimit = await _client.GetAsync($"/api/v1/catalog/products/{first.GetProperty("slug").GetString()}/related?limit=50");
        await ResponseAssertions.AssertProblemAsync(badLimit, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }
}
