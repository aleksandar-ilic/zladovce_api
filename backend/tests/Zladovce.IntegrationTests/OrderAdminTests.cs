using System.Net;
using System.Text.Json;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class OrderAdminTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ShopApi _shop;
    private readonly AdminApi _staff;

    public OrderAdminTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _shop = new ShopApi(_client, new AdminApi(_client, "narudzbine.owner@example.com", "Owner"));
        _staff = new AdminApi(_client, "narudzbine.staff@example.com", "Staff");
    }

    public Task InitializeAsync() => _postgres.ExecuteAsync(
        "UPDATE store_settings SET shipping_enabled = true, shipping_price = 0, free_shipping_threshold = NULL, min_order_amount = NULL WHERE id = 1");

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string[] Statuses(JsonElement detail) =>
        [.. detail.GetProperty("allowedNextStatuses").EnumerateArray().Select(s => s.GetProperty("status").GetString()!)];

    private Task<long> MovementsAsync(Guid orderId, string type) => _postgres.ScalarAsync<long>(
        "SELECT count(*) FROM inventory_movements WHERE order_id = @o AND movement_type = @t", ("o", orderId), ("t", type));

    [Fact]
    public async Task Full_lifecycle_with_history_shipment_payment_and_audit()
    {
        var courier = await _shop.CreateCourierAsync(template: "https://kurir.example/pracenje?broj={trackingNumber}");
        var (_, variantId) = await _shop.ProductAsync(price: 750m, stock: 10);
        var (orderId, number) = await _shop.PlaceOrderAsync(_postgres, variantId, 2);

        var received = await _shop.DetailAsync(orderId);
        Assert.Equal(number, received.GetProperty("orderNumber").GetString());
        Assert.Equal("Received", received.GetProperty("status").GetString());
        Assert.Equal(["Accepted", "Rejected", "Cancelled"], Statuses(received));
        var rejected = received.GetProperty("allowedNextStatuses")[1];
        Assert.Equal("Automatic", rejected.GetProperty("stockReturn").GetString());
        Assert.False(rejected.GetProperty("requiresShipment").GetBoolean());
        Assert.Equal("+381641234567", received.GetProperty("customer").GetProperty("phone").GetString());
        Assert.Equal("Novi Sad", received.GetProperty("shippingAddress").GetProperty("city").GetString());
        Assert.False(received.GetProperty("payment").GetProperty("canRecord").GetBoolean());
        var first = Assert.Single(received.GetProperty("timeline").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previousStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("changedByAdminName").ValueKind);

        var accepted = await _shop.ChangeStatusAsync(orderId, "Accepted", internalNote: "Proveren telefon", customerMessage: "Hvala na porudžbini!", asAdmin: _staff);
        Assert.Equal(["Preparing", "Cancelled"], Statuses(accepted));
        var entry = accepted.GetProperty("timeline")[1];
        Assert.Equal("Received", entry.GetProperty("previousStatus").GetString());
        Assert.Equal("Accepted", entry.GetProperty("newStatus").GetString());
        Assert.Equal("narudzbine.staff@example.com", entry.GetProperty("changedByAdminName").GetString());
        Assert.Equal("Proveren telefon", entry.GetProperty("internalNote").GetString());
        Assert.Equal("Hvala na porudžbini!", entry.GetProperty("customerMessage").GetString());

        var preparing = await _shop.ChangeStatusAsync(orderId, "Preparing");
        Assert.True(preparing.GetProperty("allowedNextStatuses")[0].GetProperty("requiresShipment").GetBoolean());

        var shippedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var shipped = await _shop.ChangeStatusAsync(orderId, "Shipped",
            shipment: new { courierCompanyId = courier.Id(), trackingNumber = "PE 123/45", shippedAt, note = "Lomljivo" });
        Assert.Equal(["Delivered", "DeliveryFailed"], Statuses(shipped));
        var shipment = Assert.Single(shipped.GetProperty("shipments").EnumerateArray());
        Assert.Equal(courier.GetProperty("name").GetString(), shipment.GetProperty("courier").GetProperty("name").GetString());
        Assert.Equal("PE 123/45", shipment.GetProperty("trackingNumber").GetString());
        Assert.Equal("https://kurir.example/pracenje?broj=PE%20123%2F45", shipment.GetProperty("trackingUrl").GetString());
        Assert.Equal(shippedAt.ToUnixTimeSeconds(), shipment.GetProperty("shippedAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal("Lomljivo", shipment.GetProperty("note").GetString());

        var delivered = await _shop.ChangeStatusAsync(orderId, "Delivered");
        Assert.Empty(Statuses(delivered));
        Assert.True(delivered.GetProperty("payment").GetProperty("canRecord").GetBoolean());
        Assert.False(delivered.GetProperty("payment").GetProperty("isCollected").GetBoolean());

        var paid = await _shop.Admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/orders/{orderId}/payment",
            new { collectedAmount = 1500m, collectedAt = (DateTimeOffset?)null, version = delivered.GetProperty("version").GetUInt32() });
        Assert.Equal(1500m, paid.GetProperty("payment").GetProperty("collectedAmount").GetDecimal());
        Assert.True(paid.GetProperty("payment").GetProperty("isCollected").GetBoolean());

        Assert.Equal(
            ["|Received", "Received|Accepted", "Accepted|Preparing", "Preparing|Shipped", "Shipped|Delivered"],
            paid.GetProperty("timeline").EnumerateArray().Select(t => $"{t.GetProperty("previousStatus").GetString()}|{t.GetProperty("newStatus").GetString()}"));
        Assert.Equal(4L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Order.StatusChanged' AND entity_id = @o", ("o", orderId.ToString())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Order.PaymentRecorded' AND entity_id = @o", ("o", orderId.ToString())));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Shipment.Created' AND entity_id = @s", ("s", shipment.Id().ToString())));

        // Delivery never returns stock: 10 - 2.
        Assert.Equal(8, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(0L, await MovementsAsync(orderId, "OrderCancelled"));
    }

    [Fact]
    public async Task Invalid_transitions_are_refused_with_the_allowed_statuses()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);

        var response = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status",
            new { newStatus = "Delivered", version = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32() });
        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "INVALID_STATUS_TRANSITION");
        Assert.Equal(["Accepted", "Rejected", "Cancelled"], problem.GetProperty("allowedNextStatuses").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("„Primljena”", problem.GetProperty("detail").GetString());

        await _shop.ChangeStatusAsync(orderId, "Rejected");
        var final = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status",
            new { newStatus = "Accepted", version = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32() });
        var finalProblem = await ResponseAssertions.AssertProblemAsync(final, HttpStatusCode.Conflict, "INVALID_STATUS_TRANSITION");
        Assert.Empty(finalProblem.GetProperty("allowedNextStatuses").EnumerateArray());

        var unknown = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{Guid.CreateVersion7()}/status", new { newStatus = "Accepted", version = 1u });
        await ResponseAssertions.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");

        var badStatus = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status", new { newStatus = "Lost", version = 1u });
        await ResponseAssertions.AssertProblemAsync(badStatus, HttpStatusCode.BadRequest, "BAD_REQUEST");

        Assert.Equal(2L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM order_status_history WHERE order_id = @o", ("o", orderId)));
    }

    [Fact]
    public async Task Shipment_and_restock_requirements_are_checked()
    {
        var courier = await _shop.CreateCourierAsync();
        var inactive = await _shop.CreateCourierAsync(isActive: false);
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);

        var shipmentTooEarly = await _shop.ChangeStatusAsync(orderId, "Accepted", shipment: new { courierCompanyId = courier.Id() }, expected: HttpStatusCode.BadRequest);
        Assert.True(shipmentTooEarly.GetProperty("errors").TryGetProperty("shipment", out _));
        var restockTooEarly = await _shop.ChangeStatusAsync(orderId, "Cancelled", restock: true, expected: HttpStatusCode.BadRequest);
        Assert.True(restockTooEarly.GetProperty("errors").TryGetProperty("restock", out _));

        await _shop.ChangeStatusAsync(orderId, "Accepted");
        await _shop.ChangeStatusAsync(orderId, "Preparing");

        var noShipment = await _shop.ChangeStatusAsync(orderId, "Shipped", expected: HttpStatusCode.BadRequest);
        Assert.Equal("Za status „Poslata” izaberite kurirsku službu.", noShipment.GetProperty("errors").GetProperty("shipment")[0].GetString());
        var inactiveCourier = await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = inactive.Id() }, expected: HttpStatusCode.BadRequest);
        Assert.True(inactiveCourier.GetProperty("errors").TryGetProperty("shipment.courierCompanyId", out _));
        var future = await _shop.ChangeStatusAsync(orderId, "Shipped",
            shipment: new { courierCompanyId = courier.Id(), shippedAt = DateTimeOffset.UtcNow.AddDays(1) }, expected: HttpStatusCode.BadRequest);
        Assert.True(future.GetProperty("errors").TryGetProperty("shipment.shippedAt", out _));
        var missingCourier = await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { trackingNumber = "X" }, expected: HttpStatusCode.BadRequest);
        Assert.True(missingCourier.GetProperty("errors").TryGetProperty("shipment.courierCompanyId", out _));

        // No tracking number yet and no date: shipped now, number added later.
        var shipped = await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courier.Id() });
        var shipment = shipped.GetProperty("shipments")[0];
        Assert.Equal(JsonValueKind.Null, shipment.GetProperty("trackingNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, shipment.GetProperty("trackingUrl").ValueKind);
        Assert.InRange(shipment.GetProperty("shippedAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));

        var failed = await _shop.ChangeStatusAsync(orderId, "DeliveryFailed", customerMessage: "Kurir vas nije zatekao.");
        Assert.Equal(["Shipped", "Cancelled"], Statuses(failed));
        var cancel = failed.GetProperty("allowedNextStatuses")[1];
        Assert.Equal("AdminChoice", cancel.GetProperty("stockReturn").GetString());
        Assert.True(cancel.GetProperty("requiresRestockChoice").GetBoolean());

        var noChoice = await _shop.ChangeStatusAsync(orderId, "Cancelled", expected: HttpStatusCode.BadRequest);
        Assert.Equal("Izaberite da li se vraćena roba vraća na stanje.", noChoice.GetProperty("errors").GetProperty("restock")[0].GetString());

        // A new attempt adds a second shipment.
        var again = await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courier.Id(), trackingNumber = "NOVI-1" });
        Assert.Equal(2, again.GetProperty("shipments").GetArrayLength());
        Assert.Equal("NOVI-1", again.GetProperty("shipments")[1].GetProperty("trackingNumber").GetString());
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task Stock_returns_automatically_before_shipping(string status)
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 6);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId, 4);
        Assert.Equal(2, await _shop.StockAsync(_postgres, variantId));

        await _shop.ChangeStatusAsync(orderId, status);

        Assert.Equal(6, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(1L, await MovementsAsync(orderId, "OrderCancelled"));
        Assert.Equal(6, await _postgres.ScalarAsync<int>(
            "SELECT quantity_after FROM inventory_movements WHERE order_id = @o AND movement_type = 'OrderCancelled'", ("o", orderId)));
        Assert.Equal("narudzbine.owner@example.com", await _postgres.ScalarAsync<string>(
            "SELECT a.full_name FROM inventory_movements m JOIN admin_users a ON a.id = m.admin_user_id WHERE m.order_id = @o AND m.movement_type = 'OrderCancelled'",
            ("o", orderId)));
    }

    [Fact]
    public async Task Stock_returns_when_cancelled_while_preparing()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 3);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId, 3);
        await _shop.ChangeStatusAsync(orderId, "Accepted");
        await _shop.ChangeStatusAsync(orderId, "Preparing");

        await _shop.ChangeStatusAsync(orderId, "Cancelled");

        Assert.Equal(3, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(1L, await MovementsAsync(orderId, "OrderCancelled"));
    }

    [Theory]
    [InlineData(true, 5, "ReturnRestock")]
    [InlineData(false, 3, null)]
    public async Task Cancelling_after_a_failed_delivery_restocks_only_on_request(bool restock, int stockAfter, string? movement)
    {
        var courier = await _shop.CreateCourierAsync();
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId, 2);
        await _shop.ChangeStatusAsync(orderId, "Accepted");
        await _shop.ChangeStatusAsync(orderId, "Preparing");
        await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courier.Id() });
        await _shop.ChangeStatusAsync(orderId, "DeliveryFailed");
        Assert.Equal(3, await _shop.StockAsync(_postgres, variantId));

        var cancelled = await _shop.ChangeStatusAsync(orderId, "Cancelled", restock: restock, internalNote: "Vraćeno od kurira");

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(stockAfter, await _shop.StockAsync(_postgres, variantId));
        Assert.Equal(0L, await MovementsAsync(orderId, "OrderCancelled"));
        Assert.Equal(movement is null ? 0L : 1L, await MovementsAsync(orderId, "ReturnRestock"));
        Assert.Contains(
            restock ? "\"restock\": true" : "\"restock\": false",
            await _postgres.ScalarAsync<string>(
                "SELECT changes::text FROM audit_logs WHERE action = 'Order.StatusChanged' AND entity_id = @o ORDER BY created_at DESC LIMIT 1",
                ("o", orderId.ToString())));
    }

    [Fact]
    public async Task Stale_or_concurrent_versions_give_concurrent_edit()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);
        var version = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32();
        await _shop.ChangeStatusAsync(orderId, "Accepted", version: version);

        var stale = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status", new { newStatus = "Preparing", version });
        await ResponseAssertions.AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENT_EDIT");

        var current = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32();
        var responses = await Task.WhenAll(
            _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status", new { newStatus = "Preparing", version = current }),
            _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status", new { newStatus = "Cancelled", version = current }));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(3L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM order_status_history WHERE order_id = @o", ("o", orderId)));
        var status = await _postgres.ScalarAsync<string>("SELECT status FROM orders WHERE id = @o", ("o", orderId));
        Assert.Equal(status == "Cancelled" ? 1L : 0L, await MovementsAsync(orderId, "OrderCancelled"));
    }

    [Fact]
    public async Task Shipments_can_be_corrected()
    {
        var courier = await _shop.CreateCourierAsync(template: null);
        var other = await _shop.CreateCourierAsync(template: "https://drugi.example/{trackingNumber}");
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);
        await _shop.ChangeStatusAsync(orderId, "Accepted");
        await _shop.ChangeStatusAsync(orderId, "Preparing");
        var shipped = await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courier.Id() });
        var shipmentId = shipped.GetProperty("shipments")[0].Id();
        var version = shipped.GetProperty("version").GetUInt32();
        var url = $"/api/v1/admin/orders/{orderId}/shipments/{shipmentId}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-2);

        var updated = await _staff.JsonAsync(HttpMethod.Put, url, new { courierCompanyId = other.Id(), trackingNumber = "AB123", shippedAt = date, note = "Ispravka" });

        var shipment = updated.GetProperty("shipments")[0];
        Assert.Equal(other.Id(), shipment.GetProperty("courier").Id());
        Assert.Equal("https://drugi.example/AB123", shipment.GetProperty("trackingUrl").GetString());
        Assert.Equal("Ispravka", shipment.GetProperty("note").GetString());
        Assert.Equal(version, updated.GetProperty("version").GetUInt32());
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Shipment.Updated' AND entity_id = @s", ("s", shipmentId.ToString())));

        // The current courier may stay after it was deactivated; switching to an inactive one is refused.
        await _shop.Admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/couriers/{other.Id()}",
            new { name = other.GetProperty("name").GetString(), trackingUrlTemplate = "https://drugi.example/{trackingNumber}", isActive = false, sortOrder = 0 });
        await _staff.JsonAsync(HttpMethod.Put, url, new { courierCompanyId = other.Id(), trackingNumber = "AB124", shippedAt = date, note = (string?)null });
        await _shop.Admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/couriers/{courier.Id()}",
            new { name = courier.GetProperty("name").GetString(), trackingUrlTemplate = (string?)null, isActive = false, sortOrder = 0 });
        var refused = await _staff.SendAsync(HttpMethod.Put, url, new { courierCompanyId = courier.Id(), trackingNumber = "AB124", shippedAt = date, note = (string?)null });
        await ResponseAssertions.AssertProblemAsync(refused, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var missing = await _staff.SendAsync(HttpMethod.Put, url, new { courierCompanyId = other.Id(), trackingNumber = "AB124", shippedAt = date });
        var missingProblem = await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(missingProblem.GetProperty("errors").TryGetProperty("note", out _));

        var wrongOrder = await _staff.SendAsync(HttpMethod.Put, $"/api/v1/admin/orders/{Guid.CreateVersion7()}/shipments/{shipmentId}",
            new { courierCompanyId = other.Id(), trackingNumber = "AB124", shippedAt = date, note = (string?)null });
        await ResponseAssertions.AssertProblemAsync(wrongOrder, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Payment_can_be_recorded_only_for_delivered_orders()
    {
        var courier = await _shop.CreateCourierAsync();
        var (_, variantId) = await _shop.ProductAsync(price: 1000m, stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);
        var url = $"/api/v1/admin/orders/{orderId}/payment";

        var early = await _shop.Admin.SendAsync(HttpMethod.Put, url,
            new { collectedAmount = 1000m, collectedAt = (DateTimeOffset?)null, version = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32() });
        await ResponseAssertions.AssertProblemAsync(early, HttpStatusCode.Conflict, "ORDER_NOT_DELIVERED");

        var delivered = await _shop.DeliverAsync(orderId, courier.Id());
        var version = delivered.GetProperty("version").GetUInt32();

        var future = await _shop.Admin.SendAsync(HttpMethod.Put, url, new { collectedAmount = 1000m, collectedAt = DateTimeOffset.UtcNow.AddDays(2), version });
        var futureProblem = await ResponseAssertions.AssertProblemAsync(future, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(futureProblem.GetProperty("errors").TryGetProperty("collectedAt", out _));
        var zero = await _shop.Admin.SendAsync(HttpMethod.Put, url, new { collectedAmount = 0m, collectedAt = (DateTimeOffset?)null, version });
        await ResponseAssertions.AssertProblemAsync(zero, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var dateWithoutAmount = await _shop.Admin.SendAsync(HttpMethod.Put, url, new { collectedAmount = (decimal?)null, collectedAt = DateTimeOffset.UtcNow, version });
        await ResponseAssertions.AssertProblemAsync(dateWithoutAmount, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var collectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var paid = await _shop.Admin.JsonAsync(HttpMethod.Put, url, new { collectedAmount = 950.5m, collectedAt, version });
        Assert.Equal(950.5m, paid.GetProperty("payment").GetProperty("collectedAmount").GetDecimal());
        Assert.Equal(collectedAt.ToUnixTimeSeconds(), paid.GetProperty("payment").GetProperty("collectedAt").GetDateTimeOffset().ToUnixTimeSeconds());

        var stale = await _shop.Admin.SendAsync(HttpMethod.Put, url, new { collectedAmount = (decimal?)null, collectedAt = (DateTimeOffset?)null, version });
        await ResponseAssertions.AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENT_EDIT");

        var cleared = await _shop.Admin.JsonAsync(HttpMethod.Put, url,
            new { collectedAmount = (decimal?)null, collectedAt = (DateTimeOffset?)null, version = paid.GetProperty("version").GetUInt32() });
        Assert.False(cleared.GetProperty("payment").GetProperty("isCollected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("payment").GetProperty("collectedAmount").ValueKind);
        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Order.PaymentCleared' AND entity_id = @o", ("o", orderId.ToString())));

        var listed = await _shop.Admin.GetAsync($"/api/v1/admin/orders?q={delivered.GetProperty("orderNumber").GetString()}");
        Assert.False(listed.Items()[0].GetProperty("isPaymentCollected").GetBoolean());
    }

    [Fact]
    public async Task Notes_are_added_without_changing_the_status()
    {
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (orderId, _) = await _shop.PlaceOrderAsync(_postgres, variantId);
        var version = (await _shop.DetailAsync(orderId)).GetProperty("version").GetUInt32();

        var detail = await _staff.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/notes", new { note = "  Kupac traži dostavu posle 17h.  " });

        var note = Assert.Single(detail.GetProperty("notes").EnumerateArray());
        Assert.Equal("Kupac traži dostavu posle 17h.", note.GetProperty("note").GetString());
        Assert.Equal("narudzbine.staff@example.com", note.GetProperty("adminName").GetString());
        Assert.Equal("Received", detail.GetProperty("status").GetString());
        Assert.Equal(version, detail.GetProperty("version").GetUInt32());
        Assert.Single(detail.GetProperty("timeline").EnumerateArray());

        var empty = await _staff.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/notes", new { note = "  " });
        await ResponseAssertions.AssertProblemAsync(empty, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var unknown = await _staff.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{Guid.CreateVersion7()}/notes", new { note = "x" });
        await ResponseAssertions.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task List_searches_filters_sorts_and_pages()
    {
        var family = "Đorđević" + ShopApi.Letters();
        var (_, cheap) = await _shop.ProductAsync(price: 100m, stock: 50);
        var (_, dear) = await _shop.ProductAsync(price: 900m, stock: 50);
        var email = $"pretraga-{Unique.Token()}@example.com";
        var a = await _shop.PlaceOrderAsync(_postgres, cheap, 1, lastName: family, email: email);
        var b = await _shop.PlaceOrderAsync(_postgres, dear, 2, lastName: family);
        var c = await _shop.PlaceOrderAsync(_postgres, cheap, 3, lastName: family);
        await _shop.ChangeStatusAsync(b.Id, "Accepted");
        await _shop.ChangeStatusAsync(c.Id, "Rejected");

        var byName = await _staff.GetAsync($"/api/v1/admin/orders?q={Uri.EscapeDataString(family.ToUpperInvariant())}&pageSize=10");
        Assert.Equal(3, byName.GetProperty("totalItems").GetInt32());
        Assert.Equal([c.Number, b.Number, a.Number], byName.Items().Select(i => i.GetProperty("orderNumber").GetString()));
        var row = byName.Items()[1];
        Assert.Equal($"Ana {family}", row.GetProperty("customerName").GetString());
        Assert.Equal("Novi Sad", row.GetProperty("city").GetString());
        Assert.Equal(1, row.GetProperty("itemCount").GetInt32());
        Assert.Equal(2, row.GetProperty("totalQuantity").GetInt32());
        Assert.Equal(1800m, row.GetProperty("total").GetDecimal());
        Assert.Equal("Accepted", row.GetProperty("status").GetString());
        Assert.False(row.GetProperty("isPaymentCollected").GetBoolean());

        var unaccented = await _staff.GetAsync($"/api/v1/admin/orders?q={Uri.EscapeDataString("ana dordevic" + family[8..])}");
        Assert.Equal(3, unaccented.GetProperty("totalItems").GetInt32());

        var byEmail = await _staff.GetAsync($"/api/v1/admin/orders?q={Uri.EscapeDataString(email.ToUpperInvariant())}");
        Assert.Equal([a.Number], byEmail.Items().Select(i => i.GetProperty("orderNumber").GetString()));
        var byNumber = await _staff.GetAsync($"/api/v1/admin/orders?q={b.Number.ToLowerInvariant()}");
        Assert.Equal([b.Number], byNumber.Items().Select(i => i.GetProperty("orderNumber").GetString()));

        var phone = "+38169" + Random.Shared.Next(1_000_000, 9_999_999);
        await _postgres.ExecuteAsync("UPDATE orders SET contact_phone = @p WHERE id = @o", ("p", phone), ("o", a.Id));
        var byPhone = await _staff.GetAsync($"/api/v1/admin/orders?q={Uri.EscapeDataString("069 " + phone[6..])}");
        Assert.Equal([a.Number], byPhone.Items().Select(i => i.GetProperty("orderNumber").GetString()));

        var statuses = await _staff.GetAsync($"/api/v1/admin/orders?q={family}&status=Received,Accepted&sort=totalDesc");
        Assert.Equal([b.Number, a.Number], statuses.Items().Select(i => i.GetProperty("orderNumber").GetString()));
        var repeated = await _staff.GetAsync($"/api/v1/admin/orders?q={family}&status=Rejected&status=Accepted&sort=totalAsc");
        Assert.Equal([c.Number, b.Number], repeated.Items().Select(i => i.GetProperty("orderNumber").GetString()));
        var oldest = await _staff.GetAsync($"/api/v1/admin/orders?q={family}&sort=oldest&pageSize=2&page=2");
        Assert.Equal([c.Number], oldest.Items().Select(i => i.GetProperty("orderNumber").GetString()));
        Assert.Equal(2, oldest.GetProperty("totalPages").GetInt32());

        var today = Domain.Common.SerbianTime.DateOf(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tomorrow = Domain.Common.SerbianTime.DateOf(DateTimeOffset.UtcNow).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(3, (await _staff.GetAsync($"/api/v1/admin/orders?q={family}&from={today}&to={today}")).GetProperty("totalItems").GetInt32());
        Assert.Equal(0, (await _staff.GetAsync($"/api/v1/admin/orders?q={family}&from={tomorrow}")).GetProperty("totalItems").GetInt32());

        // A day ends at midnight Serbian time: an order placed at 23:30 UTC on the 1st belongs to the 2nd in Belgrade.
        await _postgres.ExecuteAsync("UPDATE orders SET placed_at = '2026-01-01T23:30:00Z' WHERE id = @o", ("o", a.Id));
        Assert.Equal([a.Number], (await _staff.GetAsync($"/api/v1/admin/orders?q={family}&from=2026-01-02&to=2026-01-02")).Items().Select(i => i.GetProperty("orderNumber").GetString()));
        Assert.Equal(0, (await _staff.GetAsync($"/api/v1/admin/orders?q={family}&from=2026-01-01&to=2026-01-01")).GetProperty("totalItems").GetInt32());

        var badStatus = await _staff.SendAsync(HttpMethod.Get, "/api/v1/admin/orders?status=Lost");
        await ResponseAssertions.AssertProblemAsync(badStatus, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var badRange = await _staff.SendAsync(HttpMethod.Get, "/api/v1/admin/orders?from=2026-02-01&to=2026-01-01");
        await ResponseAssertions.AssertProblemAsync(badRange, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var badSort = await _staff.SendAsync(HttpMethod.Get, "/api/v1/admin/orders?sort=name");
        await ResponseAssertions.AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Summary_counts_orders_per_status()
    {
        async Task<Dictionary<string, int>> CountsAsync()
        {
            var summary = await _staff.GetAsync("/api/v1/admin/orders/summary");
            var counts = summary.GetProperty("counts").EnumerateArray().ToDictionary(c => c.GetProperty("status").GetString()!, c => c.GetProperty("count").GetInt32());
            Assert.Equal(counts.Values.Sum(), summary.GetProperty("total").GetInt32());
            return counts;
        }

        var before = await CountsAsync();
        Assert.Equal(["Received", "Accepted", "Preparing", "Shipped", "Delivered", "Cancelled", "Rejected", "DeliveryFailed"], before.Keys);

        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var (first, _) = await _shop.PlaceOrderAsync(_postgres, variantId);
        await _shop.PlaceOrderAsync(_postgres, variantId);
        await _shop.ChangeStatusAsync(first, "Accepted");

        var after = await CountsAsync();
        Assert.Equal(before["Received"] + 1, after["Received"]);
        Assert.Equal(before["Accepted"] + 1, after["Accepted"]);
    }

    [Fact]
    public async Task Order_endpoints_require_an_administrator()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/admin/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/admin/orders/summary")).StatusCode);
        var detail = await _shop.Admin.SendAsync(HttpMethod.Get, $"/api/v1/admin/orders/{Guid.CreateVersion7()}");
        await ResponseAssertions.AssertProblemAsync(detail, HttpStatusCode.NotFound, "NOT_FOUND");
    }
}
