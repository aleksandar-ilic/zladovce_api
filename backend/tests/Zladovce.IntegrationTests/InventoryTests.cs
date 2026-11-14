using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Inventory;
using Zladovce.Domain.Inventory;
using Zladovce.Infrastructure.Persistence;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class InventoryTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _api;

    public InventoryTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _api = new AdminApi(_client, "skladiste@example.com", "Staff");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(JsonElement Product, Guid VariantId, string Sku)> CreateStockedAsync(int stock, int threshold = 2)
    {
        var category = await _api.CreateCategoryAsync();
        var sku = $"INV-{Unique.Token()}";
        var product = await _api.CreateProductAsync(category.Id(), variants: [AdminApi.Variant(sku, stock: stock, threshold: threshold)]);
        return (product, product.GetProperty("variants")[0].Id(), sku);
    }

    [Fact]
    public async Task Adjustment_changes_stock_writes_a_movement_with_the_admin_and_an_audit_row()
    {
        var (_, variantId, _) = await CreateStockedAsync(10);

        var item = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = -4, reason = "Oštećene tegle" });
        Assert.Equal(6, item.GetProperty("quantityOnHand").GetInt32());
        Assert.Equal("ok", item.GetProperty("status").GetString());

        item = await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = 5, reason = "Nova tura" });
        Assert.Equal(11, item.GetProperty("quantityOnHand").GetInt32());

        var movements = await _api.GetAsync($"/api/v1/admin/inventory/{variantId}/movements");
        var rows = movements.Items();
        Assert.Equal(3, movements.GetProperty("totalItems").GetInt32());
        Assert.Equal(["ManualAdjustment", "ManualAdjustment", "InitialStock"], rows.Select(r => r.GetProperty("movementType").GetString()));
        Assert.Equal([5, -4, 10], rows.Select(r => r.GetProperty("quantityDelta").GetInt32()));
        Assert.Equal([11, 6, 10], rows.Select(r => r.GetProperty("quantityAfter").GetInt32()));
        Assert.Equal("Nova tura", rows[0].GetProperty("reason").GetString());
        Assert.Equal("skladiste@example.com", rows[0].GetProperty("adminName").GetString());

        Assert.Equal(2L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Inventory.Adjusted' AND entity_id = @id AND admin_user_id IS NOT NULL",
            ("id", variantId.ToString())));
        var changes = await _postgres.ScalarAsync<string>(
            "SELECT changes::text FROM audit_logs WHERE action = 'Inventory.Adjusted' AND entity_id = @id ORDER BY created_at LIMIT 1",
            ("id", variantId.ToString()));
        var json = JsonDocument.Parse(changes!).RootElement;
        Assert.Equal(10, json.GetProperty("quantityOnHand").GetProperty("old").GetInt32());
        Assert.Equal(6, json.GetProperty("quantityOnHand").GetProperty("new").GetInt32());

        var paged = await _api.GetAsync($"/api/v1/admin/inventory/{variantId}/movements?page=2&pageSize=2");
        Assert.Equal([10], paged.Items().Select(r => r.GetProperty("quantityDelta").GetInt32()));
    }

    [Fact]
    public async Task Adjustment_below_zero_is_refused_and_changes_nothing()
    {
        var (_, variantId, _) = await CreateStockedAsync(3);

        var response = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = -4, reason = "Previše" });
        var problem = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.Contains("3", problem.GetProperty("detail").GetString());

        Assert.Equal(3, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", variantId)));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM inventory_movements WHERE variant_id = @v", ("v", variantId)));
        Assert.Equal(0L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Inventory.Adjusted' AND entity_id = @id", ("id", variantId.ToString())));
    }

    [Fact]
    public async Task Adjustment_validation_and_not_found()
    {
        var (_, variantId, _) = await CreateStockedAsync(3);

        var noReason = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = 2, reason = " " });
        var problem = await ResponseAssertions.AssertProblemAsync(noReason, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));

        var zero = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = 0, reason = "x" });
        await ResponseAssertions.AssertProblemAsync(zero, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var missing = await _api.SendAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{Guid.CreateVersion7()}/adjustments", new { quantityDelta = 1, reason = "x" });
        await ResponseAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");

        var missingMovements = await _api.SendAsync(HttpMethod.Get, $"/api/v1/admin/inventory/{Guid.CreateVersion7()}/movements");
        await ResponseAssertions.AssertProblemAsync(missingMovements, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Threshold_changes_status_low_stock_and_list_filters()
    {
        var (_, variantId, sku) = await CreateStockedAsync(5, threshold: 2);

        var list = await _api.GetAsync($"/api/v1/admin/inventory?q={sku}&status=ok");
        Assert.Equal([variantId], list.Items().Select(i => i.GetProperty("variantId").GetGuid()));

        var item = await _api.JsonAsync(HttpMethod.Put, $"/api/v1/admin/inventory/{variantId}/threshold", new { lowStockThreshold = 5 });
        Assert.Equal("low", item.GetProperty("status").GetString());
        Assert.Equal(5, item.GetProperty("lowStockThreshold").GetInt32());

        Assert.Empty((await _api.GetAsync($"/api/v1/admin/inventory?q={sku}&status=ok")).Items());
        Assert.Single((await _api.GetAsync($"/api/v1/admin/inventory?q={sku.ToLowerInvariant()}&status=low")).Items());

        var lowStock = await _api.GetAsync("/api/v1/admin/dashboard/low-stock?limit=100");
        Assert.Contains(lowStock.Items(), i => i.GetProperty("variantId").GetGuid() == variantId);
        Assert.True(lowStock.GetProperty("totalItems").GetInt32() >= 1);

        await _api.JsonAsync(HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments", new { quantityDelta = -5, reason = "Prodato van sistema" });
        Assert.Single((await _api.GetAsync($"/api/v1/admin/inventory?q={sku}&status=out")).Items());

        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM audit_logs WHERE action = 'Inventory.Updated' AND entity_id = @id", ("id", variantId.ToString())));

        var negative = await _api.SendAsync(HttpMethod.Put, $"/api/v1/admin/inventory/{variantId}/threshold", new { lowStockThreshold = -1 });
        await ResponseAssertions.AssertProblemAsync(negative, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var badStatus = await _api.SendAsync(HttpMethod.Get, "/api/v1/admin/inventory?status=empty");
        await ResponseAssertions.AssertProblemAsync(badStatus, HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var badLimit = await _api.SendAsync(HttpMethod.Get, "/api/v1/admin/dashboard/low-stock?limit=0");
        await ResponseAssertions.AssertProblemAsync(badLimit, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Parallel_deductions_never_oversell()
    {
        const int stock = 7;
        const int attempts = 25;
        var (_, variantId, _) = await CreateStockedAsync(stock);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            start.Wait();
            try
            {
                await inventory.DeductAsync([new StockLine(variantId, 1)], Guid.CreateVersion7(), CancellationToken.None);
                return true;
            }
            catch (InsufficientStockException)
            {
                return false;
            }
        })).ToList();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(stock, results.Count(r => r));
        Assert.Equal(0, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", variantId)));
        Assert.Equal((long)stock, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM inventory_movements WHERE variant_id = @v AND movement_type = 'OrderPlaced'", ("v", variantId)));

        // Every movement records the stock after it: 6, 5, ... 0 exactly once each.
        var after = await _postgres.ScalarAsync<int[]>(
            "SELECT array_agg(quantity_after ORDER BY quantity_after) FROM inventory_movements WHERE variant_id = @v AND movement_type = 'OrderPlaced'",
            ("v", variantId));
        Assert.Equal(Enumerable.Range(0, stock), after!);
    }

    [Fact]
    public async Task Deduction_of_several_variants_is_all_or_nothing_and_return_restores_stock()
    {
        var (first, firstVariant, _) = await CreateStockedAsync(5);
        var (_, secondVariant, _) = await CreateStockedAsync(1);
        var orderId = Guid.CreateVersion7();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var exception = await Assert.ThrowsAsync<InsufficientStockException>(() =>
                inventory.DeductAsync([new StockLine(firstVariant, 2), new StockLine(secondVariant, 3)], orderId, CancellationToken.None));
            var shortage = Assert.Single(exception.Shortages);
            Assert.Equal(secondVariant, shortage.VariantId);
            Assert.Equal(1, shortage.Available);
        }

        Assert.Equal(5, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", firstVariant)));

        // Inside a caller's transaction (as checkout will do): the caller decides about commit.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZladovceDbContext>();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await inventory.DeductAsync([new StockLine(firstVariant, 2), new StockLine(secondVariant, 1)], orderId, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Equal(5, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", firstVariant)));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            await inventory.DeductAsync([new StockLine(firstVariant, 2), new StockLine(secondVariant, 1)], orderId, CancellationToken.None);
            await inventory.ReturnAsync([new StockLine(firstVariant, 2)], InventoryMovementType.OrderCancelled, orderId, CancellationToken.None);
        }

        Assert.Equal(5, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", firstVariant)));
        Assert.Equal(0, await _postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", secondVariant)));
        Assert.Equal(2L, await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM inventory_movements WHERE order_id = @o AND variant_id = @v", ("o", orderId), ("v", firstVariant)));
        Assert.Null(await _postgres.ScalarAsync<Guid?>(
            "SELECT admin_user_id FROM inventory_movements WHERE order_id = @o AND movement_type = 'OrderPlaced' LIMIT 1", ("o", orderId)));
        Assert.True(first.GetProperty("variants").GetArrayLength() == 1);
    }

    [Fact]
    public async Task Database_refuses_negative_stock_and_reasonless_manual_adjustments()
    {
        var (_, variantId, _) = await CreateStockedAsync(1);

        var negative = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            _postgres.ExecuteAsync("UPDATE inventory SET quantity_on_hand = -1 WHERE variant_id = @v", ("v", variantId)));
        Assert.Equal("ck_inventory_quantity_on_hand", negative.ConstraintName);

        var noReason = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => _postgres.ExecuteAsync(
            "INSERT INTO inventory_movements (id, variant_id, movement_type, quantity_delta, quantity_after, created_at) VALUES (@id, @v, 'ManualAdjustment', 1, 2, now())",
            ("id", Guid.CreateVersion7()), ("v", variantId)));
        Assert.Equal("ck_inventory_movements_reason", noReason.ConstraintName);

        var zero = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => _postgres.ExecuteAsync(
            "INSERT INTO inventory_movements (id, variant_id, movement_type, quantity_delta, quantity_after, created_at) VALUES (@id, @v, 'InitialStock', 0, 2, now())",
            ("id", Guid.CreateVersion7()), ("v", variantId)));
        Assert.Equal("ck_inventory_movements_quantity_delta", zero.ConstraintName);
    }
}
