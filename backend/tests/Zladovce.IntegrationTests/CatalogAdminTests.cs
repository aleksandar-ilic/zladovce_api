using System.Net;
using System.Text.Json;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CatalogAdminTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _api;

    public CatalogAdminTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _api = new AdminApi(_client);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------------- Categories ----------------

    [Fact]
    public async Task Category_is_created_with_a_generated_slug_and_listed_with_its_product_count()
    {
        var token = Unique.Token();
        var category = await _api.CreateCategoryAsync($"Džemovi i šljive {token}");

        Assert.Equal($"dzemovi-i-sljive-{token}", category.GetProperty("slug").GetString());
        Assert.Equal(0, category.GetProperty("productCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, category.GetProperty("image").ValueKind);

        await _api.CreateProductAsync(category.Id());

        var list = await _api.GetAsync("/api/v1/admin/categories");
        var listed = list.Items().Single(c => c.Id() == category.Id());
        Assert.Equal(1, listed.GetProperty("productCount").GetInt32());

        var read = await _api.GetAsync($"/api/v1/admin/categories/{category.Id()}");
        Assert.Equal(1, read.GetProperty("productCount").GetInt32());
    }

    [Fact]
    public async Task Generated_category_slug_gets_a_suffix_but_an_explicit_duplicate_is_a_conflict()
    {
        var name = $"Med {Unique.Token()}";
        var first = await _api.CreateCategoryAsync(name);
        var second = await _api.CreateCategoryAsync(name);

        Assert.Equal(first.GetProperty("slug").GetString() + "-2", second.GetProperty("slug").GetString());

        var duplicate = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/categories",
            AdminApi.Category("Drugi", slug: first.GetProperty("slug").GetString()));
        var problem = await ResponseAssertions.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "SLUG_TAKEN");
        Assert.Equal(["slug"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        // Renaming another category to the taken slug is refused the same way.
        var rename = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/categories/{second.Id()}",
            AdminApi.Category(name, slug: first.GetProperty("slug").GetString()));
        problem = await ResponseAssertions.AssertProblemAsync(rename, HttpStatusCode.Conflict, "SLUG_TAKEN");
        Assert.Equal(["slug"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));
    }

    [Fact]
    public async Task Category_validation_errors_are_reported_per_field()
    {
        var response = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/categories", AdminApi.Category("", slug: "Loš Slug", sortOrder: -1));

        var body = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("slug", out _));
        Assert.True(errors.TryGetProperty("sortOrder", out _));
    }

    [Fact]
    public async Task Category_update_changes_values_and_keeps_slug_when_empty()
    {
        var category = await _api.CreateCategoryAsync();
        var updated = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}", new
        {
            name = "Novi naziv",
            slug = (string?)null,
            description = "Opis",
            imageKey = (string?)null,
            sortOrder = 7,
            isActive = false,
            seoTitle = "SEO naslov",
            seoDescription = "SEO opis",
        });

        Assert.Equal("Novi naziv", updated.GetProperty("name").GetString());
        Assert.Equal(category.GetProperty("slug").GetString(), updated.GetProperty("slug").GetString());
        Assert.Equal(7, updated.GetProperty("sortOrder").GetInt32());
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.Equal("SEO naslov", updated.GetProperty("seoTitle").GetString());

        var missing = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/categories/{Guid.CreateVersion7()}", AdminApi.Category("x"));
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Category_with_products_cannot_be_archived_until_they_are_archived()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());

        var refused = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{category.Id()}");
        await ResponseAssertions.AssertProblemAsync(refused, HttpStatusCode.Conflict, "CATEGORY_HAS_PRODUCTS");

        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/archive");
        var archived = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{category.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);

        var gone = await _api.SendAsync(HttpMethod.Get, $"/api/v1/admin/categories/{category.Id()}");
        await ResponseAssertions.AssertProblemAsync(gone, HttpStatusCode.NotFound, "NOT_FOUND");

        // The slug of an archived category is free again.
        var reused = await _api.CreateCategoryAsync("x", slug: category.GetProperty("slug").GetString());
        Assert.Equal(category.GetProperty("slug").GetString(), reused.GetProperty("slug").GetString());

        // A product of an archived category cannot be restored.
        var restore = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/restore");
        await ResponseAssertions.AssertProblemAsync(restore, HttpStatusCode.Conflict, "CATEGORY_ARCHIVED");
    }

    [Fact]
    public async Task Categories_can_be_reordered_only_as_a_complete_list()
    {
        await _api.CreateCategoryAsync();
        await _api.CreateCategoryAsync();
        var ids = (await _api.GetAsync("/api/v1/admin/categories")).Items().Select(c => c.Id()).ToList();
        ids.Reverse();

        var reordered = await _api.JsonAsync(HttpMethod.Put, "/api/v1/admin/categories/order", new { ids });
        Assert.Equal(ids, reordered.Items().Select(c => c.Id()));
        Assert.Equal(Enumerable.Range(0, ids.Count), reordered.Items().Select(c => c.GetProperty("sortOrder").GetInt32()));

        var partial = await _api.SendAsync(HttpMethod.Put, "/api/v1/admin/categories/order", new { ids = ids.Take(1) });
        await ResponseAssertions.AssertProblemAsync(partial, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    // ---------------- Products ----------------

    [Fact]
    public async Task Product_is_created_with_variants_stock_and_initial_movements()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var product = await _api.CreateProductAsync(
            category.Id(),
            $"Džem od šljiva {token}",
            variants:
            [
                AdminApi.Variant($"A-{token}", 650m, 590m, stock: 12, threshold: 3, name: "370 g"),
                AdminApi.Variant($"B-{token}", 1100m, null, stock: 0, threshold: 1, name: "720 g"),
            ]);

        Assert.Equal($"dzem-od-sljiva-{token}", product.GetProperty("slug").GetString());
        var variants = product.GetProperty("variants").EnumerateArray().ToArray();
        Assert.Equal(["370 g", "720 g"], variants.Select(v => v.GetProperty("name").GetString()));
        Assert.Equal(590m, variants[0].GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(12, variants[0].GetProperty("quantityOnHand").GetInt32());
        Assert.Equal("ok", variants[0].GetProperty("stockStatus").GetString());
        Assert.Equal("out", variants[1].GetProperty("stockStatus").GetString());
        Assert.Equal("g", variants[0].GetProperty("measurementUnit").GetString());
        Assert.True(product.GetProperty("version").GetUInt32() > 0);

        var movements = await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM inventory_movements WHERE variant_id = @v AND movement_type = 'InitialStock' AND quantity_delta = 12 AND quantity_after = 12",
            ("v", variants[0].Id()));
        Assert.Equal(1L, movements);
        Assert.Equal(0L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM inventory_movements WHERE variant_id = @v", ("v", variants[1].Id())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM inventory WHERE variant_id = @v AND quantity_on_hand = 0 AND low_stock_threshold = 1", ("v", variants[1].Id())));
    }

    [Fact]
    public async Task Product_description_is_sanitised()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var product = await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/products", new
        {
            categoryId = category.Id(),
            name = "Sanitize",
            sku = $"S-{token}",
            description = "<p onclick=\"x()\">Tekst</p><script>alert(1)</script>",
            variants = new[] { AdminApi.Variant($"SV-{token}") },
        }, HttpStatusCode.Created);

        Assert.Equal("<p>Tekst</p>", product.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Product_create_validation_and_conflicts()
    {
        var category = await _api.CreateCategoryAsync();

        var noVariants = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "x", sku = $"X-{Unique.Token()}", variants = Array.Empty<object>() });
        var problem = await ResponseAssertions.AssertProblemAsync(noVariants, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("variants", out _));

        var badPrice = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "x", sku = $"X-{Unique.Token()}", variants = new[] { AdminApi.Variant($"V-{Unique.Token()}", 100m, 150m) } });
        problem = await ResponseAssertions.AssertProblemAsync(badPrice, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("variants[0].discountPrice", out _));

        var unknownCategory = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = Guid.CreateVersion7(), name = "x", sku = $"X-{Unique.Token()}", variants = new[] { AdminApi.Variant($"V-{Unique.Token()}") } });
        problem = await ResponseAssertions.AssertProblemAsync(unknownCategory, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("categoryId", out _));

        var existing = await _api.CreateProductAsync(category.Id());
        var existingVariantSku = existing.GetProperty("variants")[0].GetProperty("sku").GetString()!;

        var duplicateVariantSku = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "y", sku = $"Y-{Unique.Token()}", variants = new[] { AdminApi.Variant(existingVariantSku) } });
        problem = await ResponseAssertions.AssertProblemAsync(duplicateVariantSku, HttpStatusCode.Conflict, "SKU_TAKEN");
        Assert.Equal(["variants[0].sku"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        var duplicateProductSku = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "z", sku = existing.GetProperty("sku").GetString(), variants = new[] { AdminApi.Variant($"V-{Unique.Token()}") } });
        problem = await ResponseAssertions.AssertProblemAsync(duplicateProductSku, HttpStatusCode.Conflict, "SKU_TAKEN");
        Assert.Equal(["sku"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        var duplicateSlug = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "z", slug = existing.GetProperty("slug").GetString(), sku = $"Z-{Unique.Token()}", variants = new[] { AdminApi.Variant($"V-{Unique.Token()}") } });
        problem = await ResponseAssertions.AssertProblemAsync(duplicateSlug, HttpStatusCode.Conflict, "SLUG_TAKEN");
        Assert.Equal(["slug"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        var unknownUnit = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products",
            new { categoryId = category.Id(), name = "z", sku = $"Z-{Unique.Token()}", variants = new[] { new { name = "x", sku = $"V-{Unique.Token()}", unitAmount = 1, measurementUnit = "lb", regularPrice = 1 } } });
        await ResponseAssertions.AssertProblemAsync(unknownUnit, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Admin_search_ignores_diacritics_and_case_and_finds_variant_skus()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var product = await _api.CreateProductAsync(category.Id(), $"Džem od višanja {token}", variants: [AdminApi.Variant($"VS-{token}-370")]);

        foreach (var q in new[] { $"dzem od visanja {token}", $"DŽEM OD VIŠANJA {token}", $"vs-{token}-370" })
        {
            var result = await _api.GetAsync($"/api/v1/admin/products?q={Uri.EscapeDataString(q)}");
            Assert.Equal([product.Id()], result.Items().Select(p => p.Id()));
        }

        var percent = await _api.GetAsync($"/api/v1/admin/products?q={Uri.EscapeDataString("%")}&pageSize=1");
        Assert.True(percent.GetProperty("totalItems").GetInt32() >= 0);
    }

    [Fact]
    public async Task Admin_list_filters_by_category_status_and_archived_and_pages()
    {
        var category = await _api.CreateCategoryAsync();
        var active = await _api.CreateProductAsync(category.Id(), "B aktivan");
        var inactive = await _api.CreateProductAsync(category.Id(), "A neaktivan", isActive: false);
        var archived = await _api.CreateProductAsync(category.Id(), "C arhiviran");
        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{archived.Id()}/archive");

        var all = await _api.GetAsync($"/api/v1/admin/products?category={category.Id()}&sort=name");
        Assert.Equal([inactive.Id(), active.Id()], all.Items().Select(p => p.Id()));
        Assert.Equal(2, all.GetProperty("totalItems").GetInt32());

        var onlyActive = await _api.GetAsync($"/api/v1/admin/products?category={category.Id()}&status=active");
        Assert.Equal([active.Id()], onlyActive.Items().Select(p => p.Id()));

        var onlyArchived = await _api.GetAsync($"/api/v1/admin/products?category={category.Id()}&archived=true");
        Assert.Equal([archived.Id()], onlyArchived.Items().Select(p => p.Id()));
        Assert.True(onlyArchived.Items()[0].GetProperty("isArchived").GetBoolean());

        var paged = await _api.GetAsync($"/api/v1/admin/products?category={category.Id()}&sort=name&page=2&pageSize=1");
        Assert.Equal([active.Id()], paged.Items().Select(p => p.Id()));
        Assert.Equal(2, paged.GetProperty("totalPages").GetInt32());

        var listItem = all.Items().Single(p => p.Id() == active.Id());
        Assert.Equal(500m, listItem.GetProperty("priceFrom").GetDecimal());
        Assert.Equal(10, listItem.GetProperty("totalStock").GetInt32());

        var invalid = await _api.SendAsync(HttpMethod.Get, "/api/v1/admin/products?status=deleted&sort=x&pageSize=500");
        var problem = await ResponseAssertions.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("pageSize", out _));
    }

    [Fact]
    public async Task Product_update_uses_optimistic_concurrency()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());
        var version = product.GetProperty("version").GetUInt32();

        object Body(uint v, string name) => new
        {
            categoryId = category.Id(),
            name,
            slug = (string?)null,
            sku = product.GetProperty("sku").GetString(),
            shortDescription = "Novo",
            description = "<p>Novo</p>",
            isActive = true,
            isFeatured = true,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            version = v,
        };

        var updated = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", Body(version, "Prva izmena"));
        Assert.Equal("Prva izmena", updated.GetProperty("name").GetString());
        Assert.True(updated.GetProperty("isFeatured").GetBoolean());
        Assert.Equal(product.GetProperty("slug").GetString(), updated.GetProperty("slug").GetString());
        Assert.NotEqual(version, updated.GetProperty("version").GetUInt32());

        var stale = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", Body(version, "Zakasnela izmena"));
        await ResponseAssertions.AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENT_EDIT");

        var noVersion = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}",
            new { categoryId = category.Id(), name = "x", sku = "X" });
        await ResponseAssertions.AssertProblemAsync(noVersion, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var missing = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{Guid.CreateVersion7()}", Body(version, "x"));
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Archive_and_restore_product()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());

        var archived = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/archive");
        Assert.True(archived.GetProperty("isArchived").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, archived.GetProperty("archivedAt").ValueKind);

        var change = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/variants", AdminApi.Variant($"N-{Unique.Token()}"));
        await ResponseAssertions.AssertProblemAsync(change, HttpStatusCode.Conflict, "PRODUCT_ARCHIVED");

        var restored = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/restore");
        Assert.False(restored.GetProperty("isArchived").GetBoolean());

        var missing = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{Guid.CreateVersion7()}/archive");
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Restore_is_refused_when_the_slug_was_reused_meanwhile()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());
        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/archive");

        await _api.JsonAsync(HttpMethod.Post, "/api/v1/admin/products", new
        {
            categoryId = category.Id(),
            name = "Novi",
            slug = product.GetProperty("slug").GetString(),
            sku = $"R-{Unique.Token()}",
            variants = new[] { AdminApi.Variant($"RV-{Unique.Token()}") },
        }, HttpStatusCode.Created);

        var restore = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/restore");
        var problem = await ResponseAssertions.AssertProblemAsync(restore, HttpStatusCode.Conflict, "SLUG_TAKEN");
        Assert.True(problem.GetProperty("errors").TryGetProperty("slug", out _));
    }

    [Fact]
    public async Task Sku_conflicts_point_at_every_taken_variant_by_its_index_in_the_request()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var existing = await _api.CreateProductAsync(category.Id(), variants:
            [AdminApi.Variant($"T1-{token}"), AdminApi.Variant($"T2-{token}", name: "720 g")]);

        var response = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products", new
        {
            categoryId = category.Id(),
            name = "Novi",
            sku = $"NEW-{token}",
            variants = new[]
            {
                AdminApi.Variant($"FREE-{token}"),
                AdminApi.Variant($"T2-{token}", name: "720 g"),
                AdminApi.Variant($"T1-{token}", name: "1 kg"),
            },
        });

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "SKU_TAKEN");
        var errors = problem.GetProperty("errors");
        Assert.Equal(["variants[1].sku", "variants[2].sku"], errors.EnumerateObject().Select(e => e.Name).Order());
        Assert.Contains($"T2-{token}", errors.GetProperty("variants[1].sku")[0].GetString());

        // Nothing was created.
        var listed = await _api.GetAsync($"/api/v1/admin/products?q=NEW-{token}");
        Assert.Empty(listed.Items());

        // Base SKU on update and variant SKU on add both point at "sku".
        var other = await _api.CreateProductAsync(category.Id());
        var update = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{other.Id()}", new
        {
            categoryId = category.Id(),
            name = "x",
            slug = (string?)null,
            shortDescription = (string?)null,
            description = (string?)null,
            sku = existing.GetProperty("sku").GetString(),
            isActive = true,
            isFeatured = false,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            version = other.GetProperty("version").GetUInt32(),
        });
        problem = await ResponseAssertions.AssertProblemAsync(update, HttpStatusCode.Conflict, "SKU_TAKEN");
        Assert.Equal(["sku"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        var add = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/products/{other.Id()}/variants", AdminApi.Variant($"T1-{token}"));
        problem = await ResponseAssertions.AssertProblemAsync(add, HttpStatusCode.Conflict, "SKU_TAKEN");
        Assert.Equal(["sku"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));
    }

    // ---------------- Required members ("send every field") ----------------

    [Fact]
    public async Task Missing_required_members_are_validation_errors_with_their_field_paths()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());
        var variant = product.GetProperty("variants")[0];

        // Product update replaces every value: an omitted isFeatured or seoTitle is no longer silently reset.
        var update = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", new
        {
            categoryId = category.Id(),
            name = "x",
            sku = product.GetProperty("sku").GetString(),
            isActive = true,
            version = product.GetProperty("version").GetUInt32(),
        });
        var problem = await ResponseAssertions.AssertProblemAsync(update, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(
            ["description", "isFeatured", "seoDescription", "seoTitle", "shortDescription", "slug"],
            problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order());
        Assert.Equal(
            product.GetProperty("name").GetString(),
            (await _api.GetAsync($"/api/v1/admin/products/{product.Id()}")).GetProperty("name").GetString());

        // Nested variants of a new product: the path carries the index; validator messages are not repeated for those fields.
        var create = await _api.SendAsync(HttpMethod.Post, "/api/v1/admin/products", new
        {
            categoryId = category.Id(),
            name = "y",
            sku = $"REQ-{Unique.Token()}",
            variants = new object[]
            {
                AdminApi.Variant($"RV-{Unique.Token()}"),
                new { name = "1 kg", sku = $"RV-{Unique.Token()}", unitAmount = 1, measurementUnit = "kg", regularPrice = 900 },
            },
        });
        problem = await ResponseAssertions.AssertProblemAsync(create, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem.GetProperty("errors");
        Assert.Equal(
            ["variants[1].discountPrice", "variants[1].initialStock", "variants[1].isActive", "variants[1].lowStockThreshold"],
            errors.EnumerateObject().Select(e => e.Name).Order());
        Assert.Single(errors.GetProperty("variants[1].isActive").EnumerateArray());

        // An empty body lists everything that must be sent.
        var empty = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{variant.Id()}", new { });
        problem = await ResponseAssertions.AssertProblemAsync(empty, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(
            ["discountPrice", "isActive", "measurementUnit", "name", "regularPrice", "sku", "unitAmount"],
            problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order());

        // Category: omitting imageKey no longer removes the image silently (null is still accepted, see the media tests).
        var categoryUpdate = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}", new
        {
            name = "z",
            slug = (string?)null,
            description = (string?)null,
            sortOrder = 1,
            isActive = true,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
        });
        problem = await ResponseAssertions.AssertProblemAsync(categoryUpdate, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(["imageKey"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        // Image order without mainImageId, alt text without altText.
        var order = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/images/order", new { imageIds = Array.Empty<Guid>() });
        problem = await ResponseAssertions.AssertProblemAsync(order, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(["mainImageId"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        var alt = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/images/{Guid.CreateVersion7()}", new { });
        problem = await ResponseAssertions.AssertProblemAsync(alt, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(["altText"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));
    }

    // ---------------- Variants ----------------

    [Fact]
    public async Task Variants_are_added_updated_and_archived_with_the_rules()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());
        var firstId = product.GetProperty("variants")[0].Id();
        var token = Unique.Token();

        var withSecond = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/variants",
            AdminApi.Variant($"N-{token}", 900m, stock: 4, name: "1 kg"), HttpStatusCode.Created);
        var second = withSecond.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("sku").GetString() == $"N-{token}");
        Assert.Equal(1, second.GetProperty("sortOrder").GetInt32());
        Assert.Equal(4, second.GetProperty("quantityOnHand").GetInt32());

        var updated = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{second.Id()}", new
        {
            name = "1 kg tegla",
            sku = $"N-{token}",
            unitAmount = 1,
            measurementUnit = "kg",
            regularPrice = 950,
            discountPrice = 899.99,
            isActive = true,
        });
        var changed = updated.GetProperty("variants").EnumerateArray().Single(v => v.Id() == second.Id());
        Assert.Equal("kg", changed.GetProperty("measurementUnit").GetString());
        Assert.Equal(899.99m, changed.GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(1, changed.GetProperty("sortOrder").GetInt32());

        var duplicate = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{second.Id()}", new
        {
            name = "x",
            sku = product.GetProperty("variants")[0].GetProperty("sku").GetString(),
            unitAmount = 1,
            measurementUnit = "kg",
            regularPrice = 950,
            discountPrice = (decimal?)null,
            isActive = true,
        });
        var duplicateProblem = await ResponseAssertions.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "SKU_TAKEN");
        Assert.Equal(["sku"], duplicateProblem.GetProperty("errors").EnumerateObject().Select(e => e.Name));

        // Deactivate the first: allowed, the second stays active.
        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{firstId}", new
        {
            name = "370 g",
            sku = product.GetProperty("variants")[0].GetProperty("sku").GetString(),
            unitAmount = 370,
            measurementUnit = "g",
            regularPrice = 500,
            discountPrice = (decimal?)null,
            isActive = false,
        });

        // The second is now the last active variant of an active product.
        var lastActive = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/products/{product.Id()}/variants/{second.Id()}");
        await ResponseAssertions.AssertProblemAsync(lastActive, HttpStatusCode.Conflict, "LAST_ACTIVE_VARIANT");

        var deactivate = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{second.Id()}", new
        {
            name = "1 kg",
            sku = $"N-{token}",
            unitAmount = 1,
            measurementUnit = "kg",
            regularPrice = 950,
            discountPrice = (decimal?)null,
            isActive = false,
        });
        await ResponseAssertions.AssertProblemAsync(deactivate, HttpStatusCode.Conflict, "LAST_ACTIVE_VARIANT");

        // The inactive first variant can be archived; then the second is the last variant of all.
        var afterArchive = await _api.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/products/{product.Id()}/variants/{firstId}");
        Assert.Equal([second.Id()], afterArchive.GetProperty("variants").EnumerateArray().Select(v => v.Id()));

        var missing = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/admin/products/{product.Id()}/variants/{firstId}");
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Product_cannot_be_activated_without_an_active_variant()
    {
        var category = await _api.CreateCategoryAsync();
        var token = Unique.Token();
        var product = await _api.CreateProductAsync(category.Id(), isActive: false, variants: [AdminApi.Variant($"I-{token}", isActive: false)]);

        var activate = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", new
        {
            categoryId = category.Id(),
            name = product.GetProperty("name").GetString(),
            slug = (string?)null,
            shortDescription = (string?)null,
            description = (string?)null,
            sku = product.GetProperty("sku").GetString(),
            isActive = true,
            isFeatured = false,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            version = product.GetProperty("version").GetUInt32(),
        });
        await ResponseAssertions.AssertProblemAsync(activate, HttpStatusCode.Conflict, "NO_ACTIVE_VARIANT");
    }

    // ---------------- Audit ----------------

    [Fact]
    public async Task Changes_are_audited_with_old_and_new_values_and_the_admin()
    {
        var category = await _api.CreateCategoryAsync();
        var product = await _api.CreateProductAsync(category.Id());

        await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", new
        {
            categoryId = category.Id(),
            name = "Audit naziv",
            slug = (string?)null,
            sku = product.GetProperty("sku").GetString(),
            shortDescription = "Kratak opis",
            description = "<p>Opis</p>",
            isActive = true,
            isFeatured = false,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            version = product.GetProperty("version").GetUInt32(),
        });
        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{product.Id()}/archive");

        var adminId = await _postgres.ScalarAsync<Guid>("SELECT id FROM admin_users WHERE email = 'katalog.owner@example.com'");

        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Category.Created' AND entity_id = @id AND admin_user_id = @a",
            ("id", category.Id().ToString()), ("a", adminId)));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Product.Created' AND entity_id = @id", ("id", product.Id().ToString())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'ProductVariant.Created' AND entity_id = @id",
            ("id", product.GetProperty("variants")[0].Id().ToString())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Product.Archived' AND entity_id = @id", ("id", product.Id().ToString())));

        var changes = await _postgres.ScalarAsync<string>(
            "SELECT changes::text FROM audit_logs WHERE action = 'Product.Updated' AND entity_id = @id", ("id", product.Id().ToString()));
        var json = JsonDocument.Parse(changes!).RootElement;
        Assert.Equal("Audit naziv", json.GetProperty("name").GetProperty("new").GetString());
        Assert.Equal(product.GetProperty("name").GetString(), json.GetProperty("name").GetProperty("old").GetString());
        Assert.False(json.TryGetProperty("updatedAt", out _));
    }
}
