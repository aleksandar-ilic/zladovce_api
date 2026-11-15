using System.Net;
using System.Text.Json;
using Npgsql;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CourierAndSettingsTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _owner;
    private readonly AdminApi _staff;

    public CourierAndSettingsTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _owner = new AdminApi(_client, "podesavanja.owner@example.com", "Owner");
        _staff = new AdminApi(_client, "podesavanja.staff@example.com", "Staff");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Store settings are a singleton shared by every test: back to the state of the first migration.
        await _postgres.ExecuteAsync(
            """
            UPDATE store_settings SET store_name = 'Zladovce', contact_email = NULL, phone = NULL, business_address = NULL,
                shipping_enabled = true, shipping_price = 0, free_shipping_threshold = NULL, min_order_amount = NULL,
                estimated_delivery_text = NULL, delivery_info = NULL, facebook_url = NULL, instagram_url = NULL, other_links = '[]',
                default_seo_title = NULL, default_seo_description = NULL, updated_by_admin_id = NULL
            WHERE id = 1
            """);
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static object Courier(string name, string? template = null, bool isActive = true, int sortOrder = 0) =>
        new { name, trackingUrlTemplate = template, isActive, sortOrder };

    [Fact]
    public async Task Couriers_are_created_listed_updated_and_deactivated()
    {
        var name = $"Brza pošta {Unique.Token()}";
        var created = await _owner.JsonAsync(HttpMethod.Post, "/api/v1/admin/couriers",
            Courier($"  {name} ", "https://brza.example/track/{trackingNumber}", sortOrder: 3), HttpStatusCode.Created);
        Assert.Equal(name, created.GetProperty("name").GetString());
        Assert.Equal("https://brza.example/track/{trackingNumber}", created.GetProperty("trackingUrlTemplate").GetString());
        Assert.Equal(3, created.GetProperty("sortOrder").GetInt32());

        var duplicate = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/couriers", Courier(name.ToUpperInvariant()));
        var problem = await ResponseAssertions.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "COURIER_NAME_TAKEN");
        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));

        var badTemplate = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/couriers", Courier($"X {Unique.Token()}", "http://x.example/{trackingNumber}"));
        var badProblem = await ResponseAssertions.AssertProblemAsync(badTemplate, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("Adresa za praćenje mora počinjati sa https:// i sadržati {trackingNumber} (najviše 500 znakova).",
            badProblem.GetProperty("errors").GetProperty("trackingUrlTemplate")[0].GetString());
        var noPlaceholder = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/couriers", Courier($"X {Unique.Token()}", "https://x.example/track"));
        await ResponseAssertions.AssertProblemAsync(noPlaceholder, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var missing = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/couriers", new { name = "Bez polja" });
        var missingProblem = await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(missingProblem.GetProperty("errors").TryGetProperty("isActive", out _));

        var deactivated = await _owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/couriers/{created.Id()}", Courier(name, null, isActive: false, sortOrder: 1));
        Assert.False(deactivated.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, deactivated.GetProperty("trackingUrlTemplate").ValueKind);

        var all = await _staff.GetAsync("/api/v1/admin/couriers");
        Assert.Contains(all.Items(), c => c.Id() == created.Id());
        var active = await _staff.GetAsync("/api/v1/admin/couriers?activeOnly=true");
        Assert.DoesNotContain(active.Items(), c => c.Id() == created.Id());
        Assert.All(active.Items(), c => Assert.True(c.GetProperty("isActive").GetBoolean()));

        Assert.Equal(name, (await _staff.GetAsync($"/api/v1/admin/couriers/{created.Id()}")).GetProperty("name").GetString());
        await ResponseAssertions.AssertProblemAsync(
            await _staff.SendAsync(HttpMethod.Get, $"/api/v1/admin/couriers/{Guid.CreateVersion7()}"), HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'CourierCompany.Created' AND entity_id = @id", ("id", created.Id().ToString())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'CourierCompany.Updated' AND entity_id = @id", ("id", created.Id().ToString())));
    }

    [Fact]
    public async Task Staff_may_read_couriers_but_not_change_them_or_the_settings()
    {
        var create = await _staff.SendAsync(HttpMethod.Post, "/api/v1/admin/couriers", Courier($"Staff {Unique.Token()}"));
        await ResponseAssertions.AssertProblemAsync(create, HttpStatusCode.Forbidden, "FORBIDDEN");

        var courier = await _owner.JsonAsync(HttpMethod.Post, "/api/v1/admin/couriers", Courier($"Owner {Unique.Token()}"), HttpStatusCode.Created);
        var update = await _staff.SendAsync(HttpMethod.Put, $"/api/v1/admin/couriers/{courier.Id()}", Courier("Promena"));
        await ResponseAssertions.AssertProblemAsync(update, HttpStatusCode.Forbidden, "FORBIDDEN");

        await ResponseAssertions.AssertProblemAsync(await _staff.SendAsync(HttpMethod.Get, "/api/v1/admin/settings"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ResponseAssertions.AssertProblemAsync(await _staff.SendAsync(HttpMethod.Put, "/api/v1/admin/settings", new { }), HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/admin/settings")).StatusCode);
    }

    private static Dictionary<string, object?> Settings(uint version) => new()
    {
        ["version"] = version,
        ["storeName"] = "  Zladovce Test ",
        ["logoKey"] = null,
        ["contactEmail"] = "info@zladovce.example",
        ["phone"] = "+381 64 000 0000",
        ["businessAddress"] = "Selo bb, 21000 Novi Sad",
        ["shippingEnabled"] = true,
        ["shippingPrice"] = 390m,
        ["freeShippingThreshold"] = 5000m,
        ["minOrderAmount"] = 1000m,
        ["estimatedDeliveryText"] = "2–4 radna dana",
        ["deliveryInfo"] = "Dostava na teritoriji Srbije.",
        ["facebookUrl"] = "https://facebook.example/zladovce",
        ["instagramUrl"] = null,
        ["otherLinks"] = new[] { new { label = "TikTok", url = "https://tiktok.example/@zladovce" } },
        ["defaultSeoTitle"] = "Zladovce – domaći džemovi",
        ["defaultSeoDescription"] = "Domaći proizvodi sa sela.",
    };

    [Fact]
    public async Task Owner_reads_and_replaces_the_settings_with_concurrency_and_audit()
    {
        var current = await _owner.GetAsync("/api/v1/admin/settings");
        var version = current.GetProperty("version").GetUInt32();
        Assert.True(current.TryGetProperty("logoKey", out _));

        var saved = await _owner.JsonAsync(HttpMethod.Put, "/api/v1/admin/settings", Settings(version));

        Assert.Equal("Zladovce Test", saved.GetProperty("storeName").GetString());
        Assert.Equal(390m, saved.GetProperty("shippingPrice").GetDecimal());
        Assert.Equal(5000m, saved.GetProperty("freeShippingThreshold").GetDecimal());
        Assert.Equal(1000m, saved.GetProperty("minOrderAmount").GetDecimal());
        Assert.Equal("2–4 radna dana", saved.GetProperty("estimatedDeliveryText").GetString());
        Assert.Equal("TikTok", saved.GetProperty("otherLinks")[0].GetProperty("label").GetString());
        Assert.Equal("podesavanja.owner@example.com", saved.GetProperty("updatedByAdminName").GetString());
        Assert.NotEqual(version, saved.GetProperty("version").GetUInt32());

        var store = await _owner.GetAsync("/api/v1/store");
        Assert.Equal("Zladovce Test", store.GetProperty("storeName").GetString());
        Assert.Equal(390m, store.GetProperty("shipping").GetProperty("price").GetDecimal());

        var stale = await _owner.SendAsync(HttpMethod.Put, "/api/v1/admin/settings", Settings(version));
        await ResponseAssertions.AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENT_EDIT");

        var audit = await _postgres.ScalarAsync<string>(
            "SELECT changes::text FROM audit_logs WHERE action = 'StoreSettings.Updated' AND entity_id = '1' ORDER BY created_at DESC LIMIT 1");
        Assert.Contains("shippingPrice", audit);
        Assert.Contains("storeName", audit);
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        var version = (await _owner.GetAsync("/api/v1/admin/settings")).GetProperty("version").GetUInt32();
        var body = Settings(version);
        body["storeName"] = " ";
        body["contactEmail"] = "nije-email";
        body["shippingPrice"] = -1m;
        body["freeShippingThreshold"] = 0m;
        body["minOrderAmount"] = 10.555m;
        body["facebookUrl"] = "http://facebook.example";
        body["otherLinks"] = new[] { new { label = "", url = "ftp://x" } };

        var response = await _owner.SendAsync(HttpMethod.Put, "/api/v1/admin/settings", body);

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "storeName", "contactEmail", "shippingPrice", "freeShippingThreshold", "minOrderAmount", "facebookUrl", "otherLinks[0].label", "otherLinks[0].url" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"missing error for {field}: {errors}");
        }

        body = Settings(version);
        body.Remove("deliveryInfo");
        var missing = await _owner.SendAsync(HttpMethod.Put, "/api/v1/admin/settings", body);
        var missingProblem = await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(missingProblem.GetProperty("errors").TryGetProperty("deliveryInfo", out _));
    }
}

[Collection(PostgresCollection.Name)]
public class BestsellerTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ShopApi _shop;

    public BestsellerTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _shop = new ShopApi(_client, new AdminApi(_client));
    }

    public Task InitializeAsync() => _postgres.ExecuteAsync(
        "UPDATE store_settings SET shipping_enabled = true, shipping_price = 0, free_shipping_threshold = NULL, min_order_amount = NULL WHERE id = 1");

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Bestsellers_count_only_delivered_orders_of_visible_products_in_the_window()
    {
        var courier = await _shop.CreateCourierAsync();
        var category = await _shop.Admin.CreateCategoryAsync();
        async Task<(JsonElement Product, Guid Variant)> InCategoryAsync()
        {
            var product = await _shop.Admin.CreateProductAsync(category.Id(), variants: AdminApi.Variant($"B-{Unique.Token()}", 300m, stock: 100));
            return (product, product.GetProperty("variants")[0].Id());
        }

        var (sold, soldVariant) = await InCategoryAsync();
        var (more, moreVariant) = await InCategoryAsync();
        var (undelivered, undeliveredVariant) = await InCategoryAsync();
        var (hidden, hiddenVariant) = await InCategoryAsync();
        var (old, oldVariant) = await InCategoryAsync();
        var (_, _) = await InCategoryAsync();

        // Nothing delivered yet in this category: none of these is a bestseller.
        var empty = await _shop.Admin.GetAsync("/api/v1/catalog/bestsellers?limit=24");
        Assert.DoesNotContain(empty.Items(), i => i.Id() == sold.Id() || i.Id() == undelivered.Id());

        var a = await _shop.PlaceOrderAsync(_postgres, soldVariant, 3);
        var b = await _shop.PlaceOrderAsync(_postgres, moreVariant, 5);
        var b2 = await _shop.PlaceOrderAsync(_postgres, moreVariant, 1);
        await _shop.PlaceOrderAsync(_postgres, undeliveredVariant, 15);
        var h = await _shop.PlaceOrderAsync(_postgres, hiddenVariant, 9);
        var o = await _shop.PlaceOrderAsync(_postgres, oldVariant, 20);
        var cancelled = await _shop.PlaceOrderAsync(_postgres, soldVariant, 15);
        foreach (var id in new[] { a.Id, b.Id, b2.Id, h.Id, o.Id })
        {
            await _shop.DeliverAsync(id, courier.Id());
        }

        await _shop.ChangeStatusAsync(cancelled.Id, "Cancelled");
        await _postgres.ExecuteAsync(
            "UPDATE order_status_history SET created_at = now() - interval '100 days' WHERE order_id = @o AND new_status = 'Delivered'", ("o", o.Id));
        await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{hidden.Id()}/archive");

        var bestsellers = await _shop.Admin.GetAsync("/api/v1/catalog/bestsellers?limit=24");
        var ids = bestsellers.Items().Select(i => i.Id()).ToList();
        Assert.True(ids.IndexOf(more.Id()) >= 0 && ids.IndexOf(sold.Id()) > ids.IndexOf(more.Id()), string.Join(",", ids));
        Assert.DoesNotContain(undelivered.Id(), ids);
        Assert.DoesNotContain(hidden.Id(), ids);
        Assert.DoesNotContain(old.Id(), ids);
        Assert.Equal(300m, bestsellers.Items().First(i => i.Id() == more.Id()).GetProperty("price").GetDecimal());

        var limited = await _shop.Admin.GetAsync("/api/v1/catalog/bestsellers?limit=1");
        Assert.Single(limited.Items());
        await ResponseAssertions.AssertProblemAsync(await _client.GetAsync("/api/v1/catalog/bestsellers?limit=0"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ResponseAssertions.AssertProblemAsync(await _client.GetAsync("/api/v1/catalog/bestsellers?limit=25"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        // The product list sorts by the same metric inside a category; products without sales follow, newest first.
        var sorted = await _shop.Admin.GetAsync($"/api/v1/catalog/products?category={category.GetProperty("slug").GetString()}&sort=bestselling");
        var order = sorted.Items().Select(i => i.Id()).ToList();
        Assert.Equal(more.Id(), order[0]);
        Assert.Equal(sold.Id(), order[1]);
        Assert.Equal(5, order.Count); // the archived one is hidden
        var rest = sorted.Items().Skip(2).Select(i => i.Id()).ToList();
        Assert.Contains(old.Id(), rest);
        Assert.Contains(undelivered.Id(), rest);
    }
}

[Collection(PostgresCollection.Name)]
public class OrderSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Stock_movements_reference_existing_orders()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            """
            INSERT INTO inventory_movements (id, variant_id, movement_type, quantity_delta, quantity_after, order_id, created_at)
            SELECT @id, variant_id, 'OrderPlaced', -1, 0, @order, now() FROM inventory LIMIT 1
            """,
            ("id", Guid.CreateVersion7()), ("order", Guid.CreateVersion7())));

        Assert.True(exception.SqlState is PostgresErrorCodes.ForeignKeyViolation, exception.Message);
    }

    [Fact]
    public async Task Database_enforces_order_totals_line_totals_and_statuses()
    {
        var order = await postgres.InsertPlaceholderOrderAsync();

        var total = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsync("UPDATE orders SET total = total + 1 WHERE id = @o", ("o", order)));
        Assert.Equal("ck_orders_total", total.ConstraintName);

        var status = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsync("UPDATE orders SET status = 'Lost' WHERE id = @o", ("o", order)));
        Assert.Equal("ck_orders_status", status.ConstraintName);

        var collected = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsync("UPDATE orders SET collected_amount = 10 WHERE id = @o", ("o", order)));
        Assert.Equal("ck_orders_collected", collected.ConstraintName);

        var number = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsync("UPDATE orders SET order_number = 'ZLD-26-1' WHERE id = @o", ("o", order)));
        Assert.Equal("ck_orders_order_number", number.ConstraintName);

        var line = await Assert.ThrowsAsync<PostgresException>(() => postgres.ExecuteAsync(
            """
            INSERT INTO order_items (id, order_id, variant_id, product_name, variant_name, sku, unit_price, quantity, line_total)
            SELECT @id, @o, variant_id, 'P', 'V', 'S', 100, 2, 150 FROM inventory LIMIT 1
            """,
            ("id", Guid.CreateVersion7()), ("o", order)));
        Assert.Equal("ck_order_items_line_total", line.ConstraintName);

        var postal = await Assert.ThrowsAsync<PostgresException>(() =>
            postgres.ExecuteAsync("UPDATE addresses SET postal_code = '1100' WHERE id = (SELECT shipping_address_id FROM orders WHERE id = @o)", ("o", order)));
        Assert.Equal("ck_addresses_postal_code", postal.ConstraintName);
    }

    [Theory]
    [InlineData("orders", "subtotal", "numeric")]
    [InlineData("orders", "collected_amount", "numeric")]
    [InlineData("orders", "contact_email", "citext")]
    [InlineData("orders", "idempotency_key", "uuid")]
    [InlineData("orders", "request_hash", "bytea")]
    [InlineData("orders", "placed_at", "timestamptz")]
    [InlineData("customers", "email", "citext")]
    [InlineData("courier_companies", "name", "citext")]
    [InlineData("rate_limit_buckets", "window_start", "timestamptz")]
    public async Task Order_columns_use_the_designed_types(string table, string column, string expected) =>
        Assert.Equal(expected, await postgres.ScalarAsync<string>(
            "SELECT udt_name::text FROM information_schema.columns WHERE table_name = @t AND column_name = @c", ("t", table), ("c", column)));
}
