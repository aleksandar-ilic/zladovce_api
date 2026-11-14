using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zladovce.Domain.Common;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CheckoutTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ShopApi _shop;

    public CheckoutTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _shop = new ShopApi(_client, new AdminApi(_client));
    }

    public Task InitializeAsync() => SetShippingAsync(true, 0m, null, null);

    public async Task DisposeAsync()
    {
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

    private Task<long> CountAsync(string sql, params (string, object)[] parameters) => _postgres.ScalarAsync<long>(sql, parameters);

    [Fact]
    public async Task Places_an_order_with_snapshots_history_movements_and_stock()
    {
        await SetShippingAsync(true, 350m, 10_000m, null);
        var (product, variantId) = await _shop.ProductAsync(price: 650m, discount: 590m, stock: 8, name: "Džem od šljiva");
        var email = $"Ana.{Unique.Token()}@Example.com";
        var key = Guid.CreateVersion7();

        var placed = await _shop.PlaceAsync(
            ShopApi.Body([ShopApi.Item(variantId, 3, 590m)], firstName: " Ana ", lastName: "Jović", email: email, phone: "064/123-4567",
                city: "Čačak", street: "Gradsko šetalište 3", postalCode: "32000", deliveryNote: "Zvoniti dva puta", expectedTotal: 2120m),
            key);

        var number = placed.GetProperty("orderNumber").GetString()!;
        Assert.Matches($"^ZLD-{SerbianTime.YearOf(DateTimeOffset.UtcNow)}-\\d{{6}}$", number);
        Assert.Equal(1770m, placed.GetProperty("subtotal").GetDecimal());
        Assert.Equal(350m, placed.GetProperty("shippingPrice").GetDecimal());
        Assert.Equal(2120m, placed.GetProperty("total").GetDecimal());
        Assert.Equal("RSD", placed.GetProperty("currency").GetString());
        Assert.Equal("CashOnDelivery", placed.GetProperty("paymentMethod").GetString());
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}-[0-9A-HJKMNP-TV-Z]{6}$", placed.GetProperty("trackingCode").GetString());
        var item = Assert.Single(placed.GetProperty("items").EnumerateArray());
        Assert.Equal("Džem od šljiva", item.GetProperty("productName").GetString());
        Assert.Equal("370 g", item.GetProperty("variantName").GetString());
        Assert.Equal(590m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
        Assert.Equal(1770m, item.GetProperty("lineTotal").GetDecimal());

        var orderId = await _shop.OrderIdAsync(_postgres, number);
        Assert.Equal("Received", await _postgres.ScalarAsync<string>("SELECT status FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal("+381641234567", await _postgres.ScalarAsync<string>("SELECT contact_phone FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal("Ana", await _postgres.ScalarAsync<string>("SELECT contact_first_name FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal(email.ToLowerInvariant(), await _postgres.ScalarAsync<string>("SELECT contact_email::text FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal("Zvoniti dva puta", await _postgres.ScalarAsync<string>("SELECT delivery_note FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal(key, await _postgres.ScalarAsync<Guid>("SELECT idempotency_key FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal(350m, await _postgres.ScalarAsync<decimal>("SELECT shipping_price FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Null(await _postgres.ScalarAsync<decimal?>("SELECT collected_amount FROM orders WHERE id = @o", ("o", orderId)));

        Assert.Equal(
            $"Džem od šljiva|370 g|{product.GetProperty("variants")[0].GetProperty("sku").GetString()}|590.00|3|1770.00",
            await _postgres.ScalarAsync<string>(
                "SELECT product_name || '|' || variant_name || '|' || sku || '|' || unit_price || '|' || quantity || '|' || line_total FROM order_items WHERE order_id = @o",
                ("o", orderId)));

        Assert.Equal("|Received|", await _postgres.ScalarAsync<string>(
            "SELECT coalesce(previous_status, '') || '|' || new_status || '|' || coalesce(changed_by_admin_id::text, '') FROM order_status_history WHERE order_id = @o",
            ("o", orderId)));

        Assert.Equal(5, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal("OrderPlaced|-3|5|", await _postgres.ScalarAsync<string>(
            "SELECT movement_type || '|' || quantity_delta || '|' || quantity_after || '|' || coalesce(admin_user_id::text, '') FROM inventory_movements WHERE order_id = @o",
            ("o", orderId)));

        // One customer per email with the latest name; one immutable address per order.
        Assert.Equal("Ana|Jović|+381641234567", await _postgres.ScalarAsync<string>(
            "SELECT first_name || '|' || last_name || '|' || phone FROM customers WHERE email = @e", ("e", email.ToLowerInvariant())));
        Assert.Equal("Čačak|Gradsko šetalište 3|32000", await _postgres.ScalarAsync<string>(
            "SELECT a.city || '|' || a.street || '|' || a.postal_code FROM addresses a JOIN orders o ON o.shipping_address_id = a.id WHERE o.id = @o",
            ("o", orderId)));

        // The same buyer orders again with another name and phone: still one customer, updated; a second address row.
        await _shop.PlaceAsync(ShopApi.Body([ShopApi.Item(variantId, 1)], firstName: "Ana Marija", email: email.ToUpperInvariant(), phone: "+381 65 555 1234"));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM customers WHERE email = @e::citext", ("e", email)));
        Assert.Equal("Ana Marija|+381655551234", await _postgres.ScalarAsync<string>(
            "SELECT first_name || '|' || phone FROM customers WHERE email = @e::citext", ("e", email)));
        Assert.Equal(2L, await CountAsync(
            "SELECT count(*) FROM addresses a JOIN customers c ON c.id = a.customer_id WHERE c.email = @e::citext", ("e", email)));
    }

    [Fact]
    public async Task Shipping_is_a_snapshot_and_free_from_the_threshold()
    {
        await SetShippingAsync(true, 400m, 1000m, null);
        var (_, variantId) = await _shop.ProductAsync(price: 500m, stock: 10);

        var paid = await _shop.PlaceAsync(ShopApi.Body([ShopApi.Item(variantId, 1)]));
        var free = await _shop.PlaceAsync(ShopApi.Body([ShopApi.Item(variantId, 2)]));
        await SetShippingAsync(true, 999m, null, null);

        Assert.Equal(400m, paid.GetProperty("shippingPrice").GetDecimal());
        Assert.Equal(900m, paid.GetProperty("total").GetDecimal());
        Assert.Equal(0m, free.GetProperty("shippingPrice").GetDecimal());
        Assert.Equal(1000m, free.GetProperty("total").GetDecimal());
        Assert.Equal(400m, await _postgres.ScalarAsync<decimal>(
            "SELECT shipping_price FROM orders WHERE order_number = @n", ("n", paid.GetProperty("orderNumber").GetString()!)));
    }

    [Fact]
    public async Task Replay_with_the_same_key_returns_the_original_order_without_a_second_one()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var key = Guid.CreateVersion7();
        var body = ShopApi.Body([ShopApi.Item(variantId, 2)]);

        var first = await _shop.PlaceAsync(body, key);
        var replay = await _shop.PlaceAsync(body, key, HttpStatusCode.OK);

        Assert.Equal(first.GetProperty("orderNumber").GetString(), replay.GetProperty("orderNumber").GetString());
        Assert.Equal(first.GetProperty("total").GetDecimal(), replay.GetProperty("total").GetDecimal());
        Assert.Equal(first.GetProperty("placedAt").GetDateTimeOffset(), replay.GetProperty("placedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, replay.GetProperty("trackingCode").ValueKind);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM orders WHERE idempotency_key = @k", ("k", key)));
        Assert.Equal(3, await _shop.StockAsync(_postgres, variantId));

        // Different known prices do not change what is ordered: still a replay.
        var withPrice = ShopApi.Body([ShopApi.Item(variantId, 2, 500m)]);
        withPrice["customer"] = body["customer"];
        await _shop.PlaceAsync(withPrice, key, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_refused()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var key = Guid.CreateVersion7();
        var body = ShopApi.Body([ShopApi.Item(variantId, 1)]);
        await _shop.PlaceAsync(body, key);

        var other = ShopApi.Body([ShopApi.Item(variantId, 2)]);
        other["customer"] = body["customer"];
        var response = await _shop.PlaceRawAsync(other, key);

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED");
        Assert.False(problem.TryGetProperty("quote", out _));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM orders WHERE idempotency_key = @k", ("k", key)));
        Assert.Equal(4, await _shop.StockAsync(_postgres, variantId));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_order()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 20);
        var key = Guid.CreateVersion7();
        var body = JsonSerializer.Serialize(ShopApi.Body([ShopApi.Item(variantId, 1)]));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _shop.PlaceRawAsync(body, key)));
        var texts = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.True(responses.All(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK), string.Join("\n", texts));
        Assert.Single(texts.Select(t => JsonDocument.Parse(t).RootElement.GetProperty("orderNumber").GetString()).Distinct());
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM orders WHERE idempotency_key = @k", ("k", key)));
        Assert.Equal(19, await _shop.StockAsync(_postgres, variantId));
    }

    [Fact]
    public async Task Concurrent_orders_for_the_last_unit_sell_it_once()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 1)]), Guid.CreateVersion7())));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            // Either the stock was taken between pricing and deduction, or the quote already saw it gone.
            var text = await refused.Content.ReadAsStringAsync();
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, text);
            var problem = JsonDocument.Parse(text).RootElement;
            var code = problem.GetProperty("code").GetString();
            Assert.True(code is "INSUFFICIENT_STOCK" or "CART_CHANGED", text);
            var quote = problem.GetProperty("quote");
            Assert.Empty(quote.GetProperty("lines").EnumerateArray());
            Assert.Equal("unavailable", quote.GetProperty("adjustments")[0].GetProperty("reason").GetString());
            Assert.False(quote.GetProperty("canCheckout").GetBoolean());
            Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
        }

        Assert.Equal(0, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM order_items WHERE variant_id = @v", ("v", variantId)));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM inventory_movements WHERE variant_id = @v AND movement_type = 'OrderPlaced'", ("v", variantId)));
    }

    [Fact]
    public async Task Insufficient_stock_between_quote_and_deduction_returns_the_fresh_quote()
    {
        // Two buyers race for 3 of 4 pieces; at least one must lose with INSUFFICIENT_STOCK or CART_CHANGED and nothing is half-done.
        var (_, variantId) = await _shop.ProductAsync(stock: 4);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 3)]), Guid.CreateVersion7())));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await _shop.StockAsync(_postgres, variantId));
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            var line = Assert.Single(problem.GetProperty("quote").GetProperty("lines").EnumerateArray());
            Assert.Equal(1, line.GetProperty("maxOrderQuantity").GetInt32());
            Assert.Equal(["Dostupno najviše: 1."], problem.GetProperty("errors").GetProperty("items[0].quantity").EnumerateArray().Select(e => e.GetString()));
        }

        // Rolled back completely: one order row, its number and movements only.
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM order_items WHERE variant_id = @v", ("v", variantId)));
    }

    [Fact]
    public async Task Changed_price_is_refused_with_the_fresh_quote()
    {
        var (_, variantId) = await _shop.ProductAsync(price: 500m, stock: 5);

        var response = await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 2, 450m)]), Guid.CreateVersion7());

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "CART_CHANGED");
        Assert.Equal(["Cena je promenjena: sada 500,00 RSD."],
            problem.GetProperty("errors").GetProperty("items[0].knownUnitPrice").EnumerateArray().Select(e => e.GetString()));
        var quote = problem.GetProperty("quote");
        Assert.Equal(500m, quote.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        var adjustment = quote.GetProperty("adjustments")[0];
        Assert.Equal("priceChanged", adjustment.GetProperty("reason").GetString());
        Assert.Equal(450m, adjustment.GetProperty("previousUnitPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, adjustment.GetProperty("limitReason").ValueKind);
        Assert.Equal(1000m, quote.GetProperty("total").GetDecimal());
        Assert.Equal(JsonValueKind.Null, quote.GetProperty("lines")[0].GetProperty("discountPrice").ValueKind);
        Assert.Equal(5, await _shop.StockAsync(_postgres, variantId));

        var total = await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 2)], expectedTotal: 999m), Guid.CreateVersion7());
        var totalProblem = await ResponseAssertions.AssertProblemAsync(total, HttpStatusCode.Conflict, "CART_CHANGED");
        Assert.True(totalProblem.GetProperty("errors").TryGetProperty("expectedTotal", out _));

        // The browser accepts the new price and resubmits; the same key can be used because nothing was stored.
        var key = Guid.CreateVersion7();
        await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 2, 450m)]), key);
        await _shop.PlaceAsync(ShopApi.Body([ShopApi.Item(variantId, 2, 500m)]), key);
    }

    [Fact]
    public async Task Archived_product_or_reduced_quantity_is_refused_with_the_fresh_quote()
    {
        var (archived, archivedVariant) = await _shop.ProductAsync(stock: 5);
        var (_, lowVariant) = await _shop.ProductAsync(stock: 2);
        await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{archived.Id()}/archive");

        var response = await _shop.PlaceRawAsync(
            ShopApi.Body([ShopApi.Item(lowVariant, 3), ShopApi.Item(archivedVariant, 1)]), Guid.CreateVersion7());

        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "CART_CHANGED");
        var errors = problem.GetProperty("errors");
        Assert.Equal("Dostupno najviše: 2.", errors.GetProperty("items[0].quantity")[0].GetString());
        Assert.Equal("Proizvod više nije dostupan.", errors.GetProperty("items[1].variantId")[0].GetString());
        var adjustments = problem.GetProperty("quote").GetProperty("adjustments").EnumerateArray().ToList();
        Assert.Equal(["quantityReduced", "unavailable"], adjustments.Select(a => a.GetProperty("reason").GetString()));
        Assert.Equal("stock", adjustments[0].GetProperty("limitReason").GetString());
        Assert.Equal(2, await _shop.StockAsync(_postgres, lowVariant));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM order_items WHERE variant_id = @v", ("v", lowVariant)));
    }

    [Fact]
    public async Task Minimum_order_and_disabled_shipping_are_refused()
    {
        var (_, variantId) = await _shop.ProductAsync(price: 500m, stock: 5);

        await SetShippingAsync(true, 300m, null, 1200m);
        var minimum = await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 2)]), Guid.CreateVersion7());
        var problem = await ResponseAssertions.AssertProblemAsync(minimum, HttpStatusCode.Conflict, "MINIMUM_ORDER_NOT_MET");
        Assert.Equal("Minimalni iznos porudžbine je 1.200,00 RSD. Dodajte proizvode u vrednosti od još 200,00 RSD.", problem.GetProperty("detail").GetString());
        Assert.Equal(200m, problem.GetProperty("quote").GetProperty("amountUntilMinimumOrder").GetDecimal());
        Assert.False(problem.GetProperty("quote").GetProperty("canCheckout").GetBoolean());

        await SetShippingAsync(false, 300m, null, null);
        var disabled = await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 2)]), Guid.CreateVersion7());
        var disabledProblem = await ResponseAssertions.AssertProblemAsync(disabled, HttpStatusCode.Conflict, "SHIPPING_DISABLED");
        Assert.False(disabledProblem.GetProperty("quote").GetProperty("shipping").GetProperty("enabled").GetBoolean());

        Assert.Equal(5, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM order_items WHERE variant_id = @v", ("v", variantId)));
    }

    [Fact]
    public async Task Validation_errors_name_the_fields_in_Serbian()
    {
        var (_, variantId) = await _shop.ProductAsync();

        var noKey = await _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 1)]), key: null);
        var keyProblem = await ResponseAssertions.AssertProblemAsync(noKey, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(keyProblem.GetProperty("errors").TryGetProperty("idempotencyKey", out _));

        var invalid = await _shop.PlaceRawAsync(
            ShopApi.Body([ShopApi.Item(variantId, 0)], firstName: "", email: "ana@", phone: "+44 20 7946 0958", postalCode: "1100", acceptedTerms: false),
            Guid.CreateVersion7());
        var problem = await ResponseAssertions.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = problem.GetProperty("errors");
        Assert.Equal("Ime je obavezno.", errors.GetProperty("customer.firstName")[0].GetString());
        Assert.True(errors.TryGetProperty("customer.email", out _));
        Assert.Equal("Unesite ispravan broj telefona u Srbiji, npr. 064 123 4567 ili +381 64 123 4567.", errors.GetProperty("customer.phone")[0].GetString());
        Assert.Equal("Poštanski broj ima tačno 5 cifara.", errors.GetProperty("address.postalCode")[0].GetString());
        Assert.Equal("Morate prihvatiti uslove kupovine.", errors.GetProperty("acceptedTerms")[0].GetString());
        Assert.True(errors.TryGetProperty("items[0].quantity", out _));

        var missing = await _shop.PlaceRawAsync($$"""{"items":[{"variantId":"{{variantId}}","quantity":1}],"acceptedTerms":true}""", Guid.CreateVersion7());
        var missingProblem = await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(missingProblem.GetProperty("errors").TryGetProperty("customer", out _));
        Assert.True(missingProblem.GetProperty("errors").TryGetProperty("address", out _));

        var client = await _shop.PlaceRawAsync("""{"items":[{"variantId":"x","quantity":1}]}""", Guid.CreateVersion7());
        await ResponseAssertions.AssertProblemAsync(client, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Order_numbers_are_sequential_and_unique_under_parallel_checkouts()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 100);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            _shop.PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 1)]), Guid.CreateVersion7())));
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, text);
            numbers.Add(JsonDocument.Parse(text).RootElement.GetProperty("orderNumber").GetString()!);
        }

        var sequences = numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(12, sequences.Distinct().Count());
        Assert.Equal(sequences[0] + 11, sequences[^1]);
        Assert.Equal(88, await _shop.StockAsync(_postgres, variantId));

        var year = SerbianTime.YearOf(DateTimeOffset.UtcNow);
        Assert.Equal(
            await _postgres.ScalarAsync<int>("SELECT last_value FROM order_number_counters WHERE year = @y", ("y", year)),
            await _postgres.ScalarAsync<int>(
                "SELECT max(substring(order_number from 10)::int) FROM orders WHERE order_number LIKE @p", ("p", $"ZLD-{year}-%")));
    }

    [Fact]
    public async Task Historical_order_does_not_change_when_the_product_is_renamed_or_repriced()
    {
        var (product, variantId) = await _shop.ProductAsync(price: 500m, stock: 5, name: "Kompot od višanja");
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId, 2);
        var sku = product.GetProperty("variants")[0].GetProperty("sku").GetString();

        var current = await _shop.Admin.GetAsync($"/api/v1/admin/products/{product.Id()}");
        await _shop.Admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}", new
        {
            version = current.GetProperty("version").GetUInt32(),
            categoryId = current.GetProperty("category").Id(),
            name = "Kompot od trešanja",
            slug = current.GetProperty("slug").GetString(),
            shortDescription = "Novo",
            description = "<p>Novo</p>",
            sku = current.GetProperty("sku").GetString(),
            isActive = true,
            isFeatured = false,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
        });
        await _shop.Admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/products/{product.Id()}/variants/{variantId}", new
        {
            name = "720 g",
            sku = $"NEW-{Unique.Token()}",
            unitAmount = 720m,
            measurementUnit = "g",
            regularPrice = 900m,
            discountPrice = 800m,
            isActive = true,
        });

        var detail = await _shop.DetailAsync(orderId);
        var item = detail.GetProperty("items")[0];
        Assert.Equal("Kompot od višanja", item.GetProperty("productName").GetString());
        Assert.Equal("370 g", item.GetProperty("variantName").GetString());
        Assert.Equal(sku, item.GetProperty("sku").GetString());
        Assert.Equal(500m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1000m, item.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(1000m, detail.GetProperty("subtotal").GetDecimal());
    }

    [Fact]
    public async Task Order_creation_is_rate_limited_per_client_address()
    {
        await using var factory = new DevApiFactory
        {
            ConnectionString = _postgres.ConnectionString,
            ExtraSettings = new()
            {
                ["RateLimiting:Enabled"] = "true",
                ["RateLimiting:Policies:CreateOrder:PermitLimit"] = "2",
                ["RateLimiting:Policies:CreateOrder:WindowSeconds"] = "600",
            },
        };
        using var client = factory.CreateClient();
        var shop = new ShopApi(client, new AdminApi(client));
        var ip = $"203.0.113.{Random.Shared.Next(1, 250)}";
        var other = $"198.51.100.{Random.Shared.Next(1, 250)}";
        var token = Unique.Token();
        const string invalid = "{}";

        // Every request counts, also invalid ones; the address is the right-most X-Forwarded-For entry (API Gateway).
        Assert.Equal(HttpStatusCode.BadRequest, (await shop.PlaceRawAsync(invalid, Guid.CreateVersion7(), $"10.9.9.9, {ip}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await shop.PlaceRawAsync(invalid, Guid.CreateVersion7(), $"10.{token.Length}.1.1, {ip}")).StatusCode);
        var limited = await shop.PlaceRawAsync(invalid, Guid.CreateVersion7(), ip);

        var problem = await ResponseAssertions.AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal("Poslali ste previše zahteva. Pokušajte ponovo malo kasnije.", problem.GetProperty("detail").GetString());
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 600);

        Assert.Equal(HttpStatusCode.BadRequest, (await shop.PlaceRawAsync(invalid, Guid.CreateVersion7(), other)).StatusCode);

        // Only a hash of the address is stored.
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM rate_limit_buckets WHERE bucket_key LIKE @p", ("p", $"%{ip}%")));
        Assert.True(await CountAsync("SELECT count(*) FROM rate_limit_buckets WHERE bucket_key LIKE 'CreateOrder:%' AND count >= 3") >= 1);

        // Other endpoints are not limited.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/store")).StatusCode);
        }
    }

    [Fact]
    public async Task Rate_limit_buckets_can_be_purged()
    {
        await _postgres.ExecuteAsync(
            "INSERT INTO rate_limit_buckets (bucket_key, window_start, count) VALUES (@k, now() - interval '2 days', 1)",
            ("k", $"Test:{Unique.Token()}"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var limiter = scope.ServiceProvider.GetRequiredService<Application.Common.IRateLimiter>();

        Assert.True(await limiter.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None) >= 1);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM rate_limit_buckets WHERE window_start < now() - interval '1 day'"));
    }

    [Fact]
    public async Task Storefront_on_port_4210_may_post_orders_with_the_idempotency_key()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, ShopApi.OrdersUrl);
        request.Headers.Add("Origin", "http://localhost:4210");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:4210", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("idempotency-key", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quote_reports_why_a_quantity_was_reduced()
    {
        var (_, low) = await _shop.ProductAsync(stock: 3);
        var (_, plenty) = await _shop.ProductAsync(stock: 500);

        var response = await _client.PostAsync("/api/v1/cart/quote", new StringContent(
            JsonSerializer.Serialize(new { items = new[] { new { variantId = low, quantity = 5 }, new { variantId = plenty, quantity = 25 } } }),
            System.Text.Encoding.UTF8, "application/json"));
        var quote = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(["stock", "orderLimit"], quote.GetProperty("adjustments").EnumerateArray().Select(a => a.GetProperty("limitReason").GetString()));
    }
}
