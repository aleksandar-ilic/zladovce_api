using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

/// <summary>
/// Dashboard and report numbers against a fixed, hand-calculated data set in a database of its own (no other test writes there).
/// The data sits on the DST change days 29 March 2026 (23-hour day) and 25 October 2026 (25-hour day) and right at the Belgrade
/// midnights around them.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AnalyticsTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static (string ConnectionString, Guid Variant1, Guid Variant2, Guid Product1)? _seeded;

    private readonly PostgresFixture _postgres;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero));
    private string _connectionString = null!;
    private DevApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AdminApi _owner = null!;

    private Guid _variant1;
    private Guid _variant2;
    private Guid _product1;

    public AnalyticsTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        await SeedGate.WaitAsync();
        try
        {
            if (_seeded is { } done)
            {
                (_connectionString, _variant1, _variant2, _product1) = done;
                CreateClient();
                return;
            }

            _connectionString = await _postgres.CreateEmptyDatabaseAsync($"analytics_{Unique.Token()}");
            await PostgresFixture.RunMigrationsAsync(_connectionString);
            CreateClient();
            await SeedAsync();
            _seeded = (_connectionString, _variant1, _variant2, _product1);
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private void CreateClient()
    {
        _factory = new DevApiFactory { ConnectionString = _connectionString, TestServices = s => s.AddSingleton<TimeProvider>(_clock) };
        _client = _factory.CreateClient();
        _owner = new AdminApi(_client, "analitika.owner@example.com");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// Period 29.03.2026 (Belgrade) = [2026-03-28T23:00Z, 2026-03-29T22:00Z).
    /// A 1000 placed 28T23:00:00Z (first second of 29.03) Delivered 29T21:59:59Z, collected 1000 on 29T20:00Z
    /// B  500 placed 28T22:59:59Z (last second of 28.03) Delivered 29T10:00Z, collected 480 on 30T08:00Z
    /// C  700 placed 29T12:00Z Cancelled 29T13:00Z;  D 300 placed 29T15:00Z Rejected 29T15:30Z
    /// E  450 placed 29T21:59:59Z Received;           F 999 placed 29T22:00:00Z (= 30.03 00:00) Received
    /// G  800 placed 20.03 Delivered 29T22:00:00Z (30.03), collected 800 on 29T05:00Z
    /// Expenses: 28.03 Ostalo 100, 29.03 Ambalaza 250.50, 30.03 Dostava 40.
    /// Items: A = 2 × variant1 (300) + 1 × variant2 (400); B = 1 × variant1 (300) + shipping 200.
    /// October: H 610 placed 2026-10-25T22:30Z (25.10 23:30 local, UTC+1) Received; I 620 placed 2026-10-24T21:59Z (24.10) Received.
    /// </summary>
    private async Task SeedAsync()
    {
        var categoryX = await _owner.CreateCategoryAsync("Džemovi X");
        var categoryY = await _owner.CreateCategoryAsync("Sokovi Y");
        var p1 = await _owner.CreateProductAsync(categoryX.Id(), name: "Džem od kupine", variants: AdminApi.Variant($"A1-{Unique.Token()}", 300m, stock: 50));
        var p2 = await _owner.CreateProductAsync(categoryY.Id(), name: "Sok od aronije", variants: AdminApi.Variant($"A2-{Unique.Token()}", 400m, stock: 50));
        _product1 = p1.Id();
        _variant1 = p1.GetProperty("variants")[0].Id();
        _variant2 = p2.GetProperty("variants")[0].Id();

        // Product 2 and then its category are archived: its sales fall back to "Bez kategorije".
        await _owner.JsonAsync(HttpMethod.Post, $"/api/v1/admin/products/{p2.Id()}/archive", null, HttpStatusCode.OK);
        await _owner.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/categories/{categoryY.Id()}", null, HttpStatusCode.NoContent);

        var a = await OrderAsync("A", "2026-03-28T23:00:00Z", 1000m, "Delivered", [("Delivered", "2026-03-29T21:59:59Z")],
            [(_variant1, "Džem od kupine", 300m, 2), (_variant2, "Sok od aronije", 400m, 1)], 0m);
        var b = await OrderAsync("B", "2026-03-28T22:59:59Z", 500m, "Delivered", [("Delivered", "2026-03-29T10:00:00Z")],
            [(_variant1, "Džem od kupine", 300m, 1)], 200m);
        await OrderAsync("C", "2026-03-29T12:00:00Z", 700m, "Cancelled", [("Cancelled", "2026-03-29T13:00:00Z")], [(_variant1, "Džem od kupine", 350m, 2)], 0m);
        await OrderAsync("D", "2026-03-29T15:00:00Z", 300m, "Rejected", [("Rejected", "2026-03-29T15:30:00Z")], [(_variant1, "Džem od kupine", 300m, 1)], 0m);
        await OrderAsync("E", "2026-03-29T21:59:59Z", 450m, "Received", [], [(_variant1, "Džem od kupine", 450m, 1)], 0m);
        await OrderAsync("F", "2026-03-29T22:00:00Z", 999m, "Received", [], [(_variant1, "Džem od kupine", 999m, 1)], 0m);
        var g = await OrderAsync("G", "2026-03-20T09:00:00Z", 800m, "Delivered", [("Delivered", "2026-03-29T22:00:00Z")], [(_variant1, "Džem od kupine", 400m, 2)], 0m);
        await OrderAsync("H", "2026-10-25T22:30:00Z", 610m, "Received", [], [(_variant1, "Džem od kupine", 610m, 1)], 0m);
        await OrderAsync("I", "2026-10-24T21:59:00Z", 620m, "Received", [], [(_variant1, "Džem od kupine", 620m, 1)], 0m);

        await ExecuteAsync("UPDATE orders SET collected_amount = 1000, collected_at = '2026-03-29T20:00:00Z' WHERE id = @id", ("id", a));
        await ExecuteAsync("UPDATE orders SET collected_amount = 480, collected_at = '2026-03-30T08:00:00Z' WHERE id = @id", ("id", b));
        await ExecuteAsync("UPDATE orders SET collected_amount = 800, collected_at = '2026-03-29T05:00:00Z' WHERE id = @id", ("id", g));

        await ExpenseAsync("2026-03-28", "Ostalo", 100m);
        await ExpenseAsync("2026-03-29", "Ambalaza", 250.50m);
        await ExpenseAsync("2026-03-30", "Dostava", 40m);
    }

    private async Task<Guid> OrderAsync(
        string tag, string placedAt, decimal total, string status, (string Status, string At)[] history, (Guid Variant, string Name, decimal Price, int Qty)[] items, decimal shipping)
    {
        var customer = Guid.CreateVersion7();
        var address = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        var placed = DateTimeOffset.Parse(placedAt, System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteAsync("INSERT INTO customers (id, email, first_name, last_name, phone, created_at, updated_at) VALUES (@c, @e, 'Test', 'Kupac', '+381641234567', now(), now())",
            ("c", customer), ("e", $"a-{tag}-{Unique.Token()}@example.com"));
        await ExecuteAsync("INSERT INTO addresses (id, customer_id, city, street, postal_code, created_at) VALUES (@a, @c, 'Beograd', 'Ulica 1', '11000', now())",
            ("a", address), ("c", customer));
        var subtotal = items.Sum(i => i.Price * i.Qty);
        Assert.Equal(total, subtotal + shipping);
        await ExecuteAsync(
            """
            INSERT INTO orders (id, order_number, idempotency_key, request_hash, customer_id, shipping_address_id, status,
                contact_first_name, contact_last_name, contact_email, contact_phone, payment_method, subtotal, shipping_price, total,
                placed_at, created_at, updated_at)
            VALUES (@o, @n, @k, @h, @c, @a, @s, 'Test', 'Kupac', 'kupac@example.com', '+381641234567', 'CashOnDelivery', @sub, @ship, @t, @p, @p, @p)
            """,
            ("o", order), ("n", $"ZLD-2000-{Random.Shared.Next(100_000_000, 999_999_999)}"), ("k", Guid.NewGuid()), ("h", RandomNumberGenerator.GetBytes(32)),
            ("c", customer), ("a", address), ("s", status), ("sub", subtotal), ("ship", shipping), ("t", total), ("p", placed));
        foreach (var item in items)
        {
            await ExecuteAsync(
                "INSERT INTO order_items (id, order_id, variant_id, product_name, variant_name, sku, unit_price, quantity, line_total) VALUES (@i, @o, @v, @n, '370 g', 'SKU', @u, @q, @l)",
                ("i", Guid.CreateVersion7()), ("o", order), ("v", item.Variant), ("n", item.Name), ("u", item.Price), ("q", item.Qty), ("l", item.Price * item.Qty));
        }

        await ExecuteAsync("INSERT INTO order_status_history (id, order_id, previous_status, new_status, created_at) VALUES (@i, @o, NULL, 'Received', @p)",
            ("i", Guid.CreateVersion7()), ("o", order), ("p", placed));
        foreach (var (next, at) in history)
        {
            await ExecuteAsync("INSERT INTO order_status_history (id, order_id, previous_status, new_status, created_at) VALUES (@i, @o, 'Shipped', @s, @at)",
                ("i", Guid.CreateVersion7()), ("o", order), ("s", next), ("at", DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
        }

        return order;
    }

    private Task<JsonElement> ExpenseAsync(string date, string category, decimal amount) =>
        _owner.JsonAsync(HttpMethod.Post, "/api/v1/admin/expenses", new { expenseDate = date, category, description = $"Trošak {category}", amount }, HttpStatusCode.Created);

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Summary_of_the_spring_DST_day_matches_the_hand_calculation()
    {
        var summary = await _owner.GetAsync("/api/v1/admin/dashboard/summary?period=custom&from=2026-03-29&to=2026-03-29");
        var current = summary.GetProperty("current");

        Assert.Equal(1450m, D(current, "orderedValue"));        // A 1000 + E 450 (C cancelled, D rejected excluded; B, F outside)
        Assert.Equal(2, I(current, "orderedCount"));
        Assert.Equal(725m, D(current, "averageOrderValue"));     // 1450 / 2
        Assert.Equal(1500m, D(current, "deliveredValue"));       // A 1000 + B 500 (delivered on the 29th); G delivered at 30.03 00:00
        Assert.Equal(2, I(current, "deliveredCount"));
        Assert.Equal(1500m, D(current, "estimatedCollected"));   // = delivered value
        Assert.Equal(1800m, D(current, "actualCollected"));      // by collection date: A 1000 + G 800; B's 480 was collected on the 30th
        Assert.Equal(2, I(current, "collectedCount"));
        Assert.Equal(250.50m, D(current, "expenses"));
        Assert.Equal(1249.50m, D(current, "estimatedProfit"));   // 1500 − 250.50
        Assert.Equal(1, I(current, "cancelledCount"));
        Assert.Equal(1, I(current, "rejectedCount"));

        // Previous comparable period: 28.03 (one day): only B was placed; nothing delivered or collected; expense 100.
        var previous = summary.GetProperty("previous");
        Assert.Equal(500m, D(previous, "orderedValue"));
        Assert.Equal(0m, D(previous, "deliveredValue"));
        Assert.Equal(0m, D(previous, "actualCollected"));
        Assert.Equal(100m, D(previous, "expenses"));
        Assert.Equal(-100m, D(previous, "estimatedProfit"));
        Assert.Equal("2026-03-28", summary.GetProperty("period").GetProperty("previous").GetProperty("from").GetString());

        // Queues are current states: E and F are Received; nothing accepted or preparing.
        var queues = summary.GetProperty("queues");
        Assert.Equal(4, I(queues, "awaitingConfirmation")); // E, F, H, I
        Assert.Equal(0, I(queues, "inPreparation"));
        Assert.Equal("RSD", summary.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Yesterday_is_the_Belgrade_day_before_today()
    {
        // The clock is 2026-03-30T10:00Z: yesterday is 29.03.
        var yesterday = await _owner.GetAsync("/api/v1/admin/dashboard/summary?period=yesterday");
        Assert.Equal(1450m, D(yesterday.GetProperty("current"), "orderedValue"));
        Assert.Equal(1500m, D(yesterday.GetProperty("current"), "deliveredValue"));

        var today = await _owner.GetAsync("/api/v1/admin/dashboard/summary?period=today");
        Assert.Equal(999m, D(today.GetProperty("current"), "orderedValue"));   // F at 30.03 00:00 local
        Assert.Equal(800m, D(today.GetProperty("current"), "deliveredValue")); // G
        Assert.Equal(40m, D(today.GetProperty("current"), "expenses"));
        Assert.Equal(1450m, D(today.GetProperty("previous"), "orderedValue"));
    }

    [Fact]
    public async Task Autumn_DST_day_has_25_hours_and_its_boundaries_are_local_midnights()
    {
        var summary = await _owner.GetAsync("/api/v1/admin/dashboard/summary?period=custom&from=2026-10-25&to=2026-10-25");
        Assert.Equal(610m, D(summary.GetProperty("current"), "orderedValue")); // H 23:30 local on the 25th; I is 23:59 on the 24th
        Assert.Equal(620m, D(summary.GetProperty("previous"), "orderedValue"));
    }

    [Fact]
    public async Task Revenue_and_order_series_are_daily_and_zero_filled()
    {
        var revenue = await _owner.GetAsync("/api/v1/admin/dashboard/revenue-series?period=custom&from=2026-03-27&to=2026-03-30");
        Assert.Equal("Day", revenue.GetProperty("period").GetProperty("granularity").GetString());
        var points = revenue.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(["2026-03-27", "2026-03-28", "2026-03-29", "2026-03-30"], points.Select(p => p.GetProperty("label").GetString()!));
        Assert.Equal([0m, 500m, 1450m, 999m], points.Select(p => D(p, "orderedValue")));
        Assert.Equal([0m, 0m, 1500m, 800m], points.Select(p => D(p, "deliveredValue")));

        var orders = await _owner.GetAsync("/api/v1/admin/dashboard/order-series?period=custom&from=2026-03-27&to=2026-03-30");
        var counts = orders.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2, 1], counts.Select(p => I(p, "orderCount")));
        Assert.Equal([0, 1, 4, 1], counts.Select(p => I(p, "placedCount")));
    }

    [Fact]
    public async Task Long_ranges_are_monthly()
    {
        var revenue = await _owner.GetAsync("/api/v1/admin/dashboard/revenue-series?period=custom&from=2026-02-15&to=2026-10-31");
        Assert.Equal("Month", revenue.GetProperty("period").GetProperty("granularity").GetString());
        var points = revenue.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(9, points.Count);
        Assert.Equal("2026-02-15", points[0].GetProperty("from").GetString());
        var march = points.Single(p => p.GetProperty("label").GetString() == "2026-03");
        Assert.Equal(1000m + 500m + 450m + 999m + 800m, D(march, "orderedValue")); // A, B, E, F, G (C cancelled, D rejected)
        Assert.Equal(1000m + 500m + 800m, D(march, "deliveredValue"));            // A, B, G (30.03 00:00 local is still March)
        Assert.Equal(610m + 620m, D(points.Single(p => p.GetProperty("label").GetString() == "2026-10"), "orderedValue"));
        Assert.Equal(0m, D(points.Single(p => p.GetProperty("label").GetString() == "2026-06"), "orderedValue"));
    }

    [Fact]
    public async Task Top_products_and_sales_by_category_use_delivered_items()
    {
        var top = await _owner.GetAsync("/api/v1/admin/dashboard/top-products?period=custom&from=2026-03-29&to=2026-03-29&limit=5");
        var byQuantity = top.GetProperty("byQuantity").EnumerateArray().ToList();
        Assert.Equal(2, byQuantity.Count);
        Assert.Equal(_product1, byQuantity[0].GetProperty("productId").GetGuid());
        Assert.Equal(3, I(byQuantity[0], "quantity"));          // A 2 + B 1
        Assert.Equal(900m, D(byQuantity[0], "revenue"));
        Assert.Equal(400m, D(byQuantity[1], "revenue"));

        var categories = await _owner.GetAsync("/api/v1/admin/dashboard/sales-by-category?period=custom&from=2026-03-29&to=2026-03-29");
        var items = categories.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Džemovi X", items[0].GetProperty("categoryName").GetString());
        Assert.Equal(900m, D(items[0], "revenue"));
        Assert.Equal("Bez kategorije", items[1].GetProperty("categoryName").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("categoryId").ValueKind);
        Assert.Equal(1300m, D(categories, "totalRevenue"));     // line totals only: B's shipping 200 is not product revenue
    }

    [Fact]
    public async Task Status_distribution_counts_current_status_of_orders_placed_in_the_period()
    {
        var distribution = await _owner.GetAsync("/api/v1/admin/dashboard/status-distribution?period=custom&from=2026-03-29&to=2026-03-29");
        var items = distribution.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("status").GetString()!, i => I(i, "count"));
        Assert.Equal(8, items.Count);
        Assert.Equal(1, items["Delivered"]);
        Assert.Equal(1, items["Cancelled"]);
        Assert.Equal(1, items["Rejected"]);
        Assert.Equal(1, items["Received"]);
        Assert.Equal(0, items["Shipped"]);
        Assert.Equal(4, I(distribution, "total"));
        Assert.Equal("Isporučena", distribution.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("status").GetString() == "Delivered").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Period_validation_and_staff_access()
    {
        var missing = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/dashboard/summary?period=custom&from=2026-03-29");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("\"to\"", await missing.Content.ReadAsStringAsync());

        var unknown = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/dashboard/summary?period=week");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var reversed = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/dashboard/summary?period=custom&from=2026-03-29&to=2026-03-01");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);

        // Staff sees the dashboard and reports, but not expenses.
        var staff = new AdminApi(_client, "analitika.staff@example.com", "Staff");
        await staff.GetAsync("/api/v1/admin/dashboard/summary?period=last7");
        await staff.GetAsync("/api/v1/admin/reports/inventory");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.SendAsync(HttpMethod.Get, "/api/v1/admin/expenses")).StatusCode);
    }

    [Fact]
    public async Task Expenses_list_with_period_and_category_totals()
    {
        var list = await _owner.GetAsync("/api/v1/admin/expenses?period=custom&from=2026-03-28&to=2026-03-29");
        Assert.Equal(350.50m, D(list, "total"));
        var totals = list.GetProperty("totals").EnumerateArray().ToDictionary(t => t.GetProperty("category").GetString()!, t => D(t, "amount"));
        Assert.Equal(5, totals.Count);
        Assert.Equal(250.50m, totals["Ambalaza"]);
        Assert.Equal(100m, totals["Ostalo"]);
        Assert.Equal(0m, totals["Dostava"]);
        Assert.Equal("Ambalaža", list.GetProperty("totals").EnumerateArray().First(t => t.GetProperty("category").GetString() == "Ambalaza").GetProperty("categoryLabel").GetString());
        Assert.Equal(2, list.GetProperty("expenses").GetProperty("totalItems").GetInt32());

        var categories = await _owner.GetAsync("/api/v1/admin/expenses/categories");
        Assert.Equal(["Ambalaza", "Sirovine", "Dostava", "Marketing", "Ostalo"], categories.EnumerateArray().Select(c => c.GetProperty("value").GetString()!));

        var invalid = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/expenses", new { expenseDate = "2026-03-29", category = "Hrana", description = "x", amount = 0 });
        var text = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("\"category\"", text);
        Assert.Contains("\"amount\"", text);

        // Update and delete, both audited.
        var created = await ExpenseAsync("2026-01-10", "Marketing", 10m);
        var updated = await _owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/expenses/{created.Id()}",
            new { expenseDate = "2026-01-11", category = "Marketing", description = "Letci", amount = 12.30m });
        Assert.Equal(12.30m, D(updated, "amount"));
        await _owner.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/expenses/{created.Id()}", null, HttpStatusCode.NoContent);
        var audit = await _owner.GetAsync($"/api/v1/admin/audit-logs?entityType=Expense&entityId={created.Id()}");
        // The test clock stands still, so the three entries share one timestamp: compare as a set.
        Assert.Equal(["Expense.Created", "Expense.Deleted", "Expense.Updated"], audit.Items().Select(i => i.GetProperty("action").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Sales_report_json_and_csv()
    {
        var report = await _owner.GetAsync("/api/v1/admin/reports/sales?period=custom&from=2026-03-29&to=2026-03-29");
        Assert.Equal(1300m, D(report, "totalRevenue"));
        Assert.Equal(4, I(report, "totalQuantity"));
        Assert.Equal(2, I(report, "deliveredOrders"));
        Assert.Equal(200m, D(report, "shippingRevenue"));

        var response = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/reports/sales?period=custom&from=2026-03-29&to=2026-03-29&format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("prodaja-po-proizvodima-2026-03-29-2026-03-29.csv", response.Content.Headers.ContentDisposition!.ToString());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Proizvod;Varijanta;Šifra (SKU);Kategorija;Količina;Prihod (RSD)", lines[0]);
        Assert.Contains("Džem od kupine;370 g;SKU;Džemovi X;3;900,00", lines);
        Assert.Equal("Ukupno;;;;4;1300,00", lines[^1]);

        var byCategory = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/reports/sales?period=custom&from=2026-03-29&to=2026-03-29&format=csv&groupBy=category");
        Assert.Contains("Bez kategorije;1;400,00", Encoding.UTF8.GetString(await byCategory.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Inventory_report_values_stock_at_the_regular_price()
    {
        var report = await _owner.GetAsync("/api/v1/admin/reports/inventory");
        var row = report.Items().Single(i => i.GetProperty("variantId").GetGuid() == _variant1);
        Assert.Equal(50, I(row, "quantityOnHand"));
        Assert.Equal(15000m, D(row, "stockValue"));
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal("Na stanju", row.GetProperty("statusLabel").GetString());
        Assert.DoesNotContain(report.Items(), i => i.GetProperty("variantId").GetGuid() == _variant2); // archived product

        var csv = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/reports/inventory?format=csv");
        var text = Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync())[3..]);
        Assert.Contains(";50;2;Na stanju;300,00;15000,00;Da", text);
    }
}
