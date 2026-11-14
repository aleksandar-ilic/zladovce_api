using System.Net;
using System.Text;
using System.Text.Json;

namespace Zladovce.IntegrationTests.Infrastructure;

/// <summary>The public checkout and the admin order API, for the order tests.</summary>
public sealed class ShopApi(HttpClient client, AdminApi admin)
{
    public const string OrdersUrl = "/api/v1/orders";

    public HttpClient Client => client;

    public AdminApi Admin => admin;

    /// <summary>Random lower-case letters: names in checkout may contain letters only.</summary>
    public static string Letters(int length = 8) =>
        new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    public static object Item(Guid variantId, int quantity, decimal? knownUnitPrice = null) =>
        knownUnitPrice is null ? new { variantId, quantity } : new { variantId, quantity, knownUnitPrice };

    /// <summary>A complete, valid checkout body.</summary>
    public static Dictionary<string, object?> Body(
        IEnumerable<object> items,
        string firstName = "Ana",
        string lastName = "Jović",
        string? email = null,
        string phone = "064 123 4567",
        string city = "Novi Sad",
        string street = "Bulevar oslobođenja 12",
        string postalCode = "21000",
        string? deliveryNote = null,
        bool acceptedTerms = true,
        decimal? expectedTotal = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["items"] = items.ToArray(),
            ["customer"] = new { firstName, lastName, email = email ?? $"kupac-{Unique.Token()}@example.com", phone },
            ["address"] = new { city, street, postalCode },
            ["deliveryNote"] = deliveryNote,
            ["acceptedTerms"] = acceptedTerms,
        };
        if (expectedTotal is not null)
        {
            body["expectedTotal"] = expectedTotal;
        }

        return body;
    }

    public async Task<HttpResponseMessage> PlaceRawAsync(object body, Guid? key, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OrdersUrl)
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key.Value.ToString());
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request);
    }

    public async Task<JsonElement> PlaceAsync(object body, Guid? key = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await PlaceRawAsync(body, key ?? Guid.CreateVersion7());
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST /orders: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A product in a new category; returns the product and the id of its first variant.</summary>
    public async Task<(JsonElement Product, Guid VariantId)> ProductAsync(decimal price = 500m, int stock = 10, decimal? discount = null, string? name = null)
    {
        var category = await admin.CreateCategoryAsync();
        var product = await admin.CreateProductAsync(category.Id(), name: name, variants: AdminApi.Variant($"V-{Unique.Token()}", price, discount, stock));
        return (product, product.GetProperty("variants")[0].Id());
    }

    public Task<int> StockAsync(PostgresFixture postgres, Guid variantId) =>
        postgres.ScalarAsync<int>("SELECT quantity_on_hand FROM inventory WHERE variant_id = @v", ("v", variantId));

    public async Task<Guid> OrderIdAsync(PostgresFixture postgres, string orderNumber) =>
        await postgres.ScalarAsync<Guid>("SELECT id FROM orders WHERE order_number = @n", ("n", orderNumber));

    public Task<JsonElement> DetailAsync(Guid orderId) => admin.GetAsync($"/api/v1/admin/orders/{orderId}");

    public async Task<JsonElement> ChangeStatusAsync(
        Guid orderId,
        string newStatus,
        object? shipment = null,
        bool? restock = null,
        uint? version = null,
        HttpStatusCode expected = HttpStatusCode.OK,
        string? internalNote = null,
        string? customerMessage = null,
        AdminApi? asAdmin = null)
    {
        var currentVersion = version ?? (await DetailAsync(orderId)).GetProperty("version").GetUInt32();
        return await (asAdmin ?? admin).JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/status", new
        {
            newStatus,
            version = currentVersion,
            internalNote,
            customerMessage,
            shipment,
            restock,
        }, expected);
    }

    public async Task<JsonElement> CreateCourierAsync(string? name = null, string? template = "https://kurir.example/pracenje?broj={trackingNumber}", bool isActive = true) =>
        await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/couriers",
            new { name = name ?? $"Kurir {Unique.Token()}", trackingUrlTemplate = template, isActive, sortOrder = 0 }, HttpStatusCode.Created);

    /// <summary>Places an order for one variant and returns its id and number.</summary>
    public async Task<(Guid Id, string Number)> PlaceOrderAsync(PostgresFixture postgres, Guid variantId, int quantity = 1, string lastName = "Jović", string? email = null)
    {
        var placed = await PlaceAsync(Body([Item(variantId, quantity)], lastName: lastName, email: email));
        var number = placed.GetProperty("orderNumber").GetString()!;
        return (await OrderIdAsync(postgres, number), number);
    }

    /// <summary>Moves an order Received → Accepted → Preparing → Shipped (with the given courier) → Delivered.</summary>
    public async Task<JsonElement> DeliverAsync(Guid orderId, Guid courierId)
    {
        await ChangeStatusAsync(orderId, "Accepted");
        await ChangeStatusAsync(orderId, "Preparing");
        await ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courierId });
        return await ChangeStatusAsync(orderId, "Delivered");
    }
}
