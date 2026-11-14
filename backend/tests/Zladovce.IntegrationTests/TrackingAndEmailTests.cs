using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Zladovce.Application.Inventory;
using Zladovce.Domain.Tracking;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TrackingAndEmailTests : IAsyncLifetime
{
    private const string LookupUrl = "/api/v1/tracking/lookup";

    private readonly PostgresFixture _postgres;
    private readonly MailpitFixture _mailpit;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ShopApi _shop;

    public TrackingAndEmailTests(PostgresFixture postgres, MinioFixture minio, MailpitFixture mailpit)
    {
        _postgres = postgres;
        _mailpit = mailpit;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _shop = new ShopApi(_client, new AdminApi(_client));
    }

    public Task InitializeAsync() =>
        _postgres.ExecuteAsync(
            "UPDATE store_settings SET shipping_enabled = true, shipping_price = 0, free_shipping_threshold = NULL, min_order_amount = NULL WHERE id = 1");

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string Email() => $"pracenje-{Unique.Token()}@example.com";

    private async Task<(Guid Id, string Number, string Code, string Email)> PlaceAsync(int quantity = 1, int stock = 10, string? email = null)
    {
        var (_, variantId) = await _shop.ProductAsync(stock: stock, name: "Džem od kupina");
        email ??= Email();
        var placed = await _shop.PlaceAsync(ShopApi.Body([ShopApi.Item(variantId, quantity)], firstName: "Milica", lastName: "Petrović", email: email,
            phone: "065 765 4321", city: "Kraljevo", street: "Omladinska 7", postalCode: "36000"));
        var number = placed.GetProperty("orderNumber").GetString()!;
        return (await _shop.OrderIdAsync(_postgres, number), number, placed.GetProperty("trackingCode").GetString()!, email);
    }

    private async Task<HttpResponseMessage> LookupRawAsync(object body, string? forwardedFor = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LookupUrl)
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await (client ?? _client).SendAsync(request);
    }

    private async Task<JsonElement> LookupAsync(string code)
    {
        var response = await LookupRawAsync(new { code });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"lookup: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<long> CountAsync(string sql, params (string, object)[] parameters) => _postgres.ScalarAsync<long>(sql, parameters);

    private async Task<Guid> CourierAsync(string? template = "https://kurir.example/pracenje?broj={trackingNumber}") =>
        (await _shop.CreateCourierAsync(name: $"Brza pošta {Unique.Token()}", template: template)).Id();

    [Fact]
    public async Task Checkout_returns_a_working_code_and_queues_the_confirmation_with_the_order()
    {
        var (orderId, number, code, email) = await PlaceAsync();

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}-[0-9A-HJKMNP-TV-Z]{6}$", code);
        var canonical = TrackingCode.Normalize(code)!;

        // Only the hash is stored.
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM order_tracking_tokens WHERE order_id = @o AND token_hash = @h",
            ("o", orderId), ("h", SHA256.HashData(Encoding.ASCII.GetBytes(canonical)))));

        // The confirmation row: same order, encrypted code, payload without the code.
        Assert.Equal("OrderReceived", await _postgres.ScalarAsync<string>(
            "SELECT template FROM email_outbox WHERE dedup_key = @k", ("k", $"order:{orderId:D}:received")));
        Assert.Equal("Pending", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", orderId)));
        Assert.Equal(email, await _postgres.ScalarAsync<string>("SELECT recipient::text FROM email_outbox WHERE order_id = @o", ("o", orderId)));
        var encrypted = await _postgres.ScalarAsync<byte[]>("SELECT encrypted_token FROM email_outbox WHERE order_id = @o", ("o", orderId));
        Assert.NotNull(encrypted);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.ASCII.GetBytes(canonical)), Convert.ToHexString(encrypted!));
        var payload = await _postgres.ScalarAsync<string>("SELECT payload::text FROM email_outbox WHERE order_id = @o", ("o", orderId));
        Assert.DoesNotContain(canonical, payload);
        Assert.DoesNotContain(code, payload);
        Assert.Contains(number, payload);

        // Every accepted form of the code finds the order.
        foreach (var form in new[] { code, canonical, canonical.ToLowerInvariant(), code.Replace('-', ' '), $"  {code.ToLowerInvariant()}  " })
        {
            var tracking = await LookupAsync(form);
            Assert.Equal(number, tracking.GetProperty("orderNumber").GetString());
            Assert.Equal("Received", tracking.GetProperty("status").GetString());
            Assert.Equal("Primljena", tracking.GetProperty("statusLabel").GetString());
            var entry = Assert.Single(tracking.GetProperty("timeline").EnumerateArray());
            Assert.Equal("Porudžbina primljena", entry.GetProperty("label").GetString());
            Assert.Equal(JsonValueKind.Null, tracking.GetProperty("shipment").ValueKind);
        }

        Assert.NotNull(await _postgres.ScalarAsync<DateTime?>("SELECT last_used_at FROM order_tracking_tokens WHERE order_id = @o", ("o", orderId)));
    }

    [Fact]
    public async Task Lookup_shows_the_real_timeline_and_courier_and_no_personal_data()
    {
        var (orderId, number, code, email) = await PlaceAsync();
        var courierId = await CourierAsync();

        await _shop.ChangeStatusAsync(orderId, "Accepted", customerMessage: "Hvala, šaljemo uskoro.", internalNote: "Tajna interna beleška");
        await _shop.ChangeStatusAsync(orderId, "Preparing");
        await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courierId, trackingNumber = "PK 778/9", note = "Interna napomena kurira" });

        var response = await LookupRawAsync(new { code });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tracking = JsonDocument.Parse(text).RootElement;

        Assert.Equal(
            ["orderNumber", "placedAt", "shipment", "status", "statusLabel", "timeline"],
            tracking.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(number, tracking.GetProperty("orderNumber").GetString());
        Assert.Equal("Shipped", tracking.GetProperty("status").GetString());
        Assert.Equal("Poslata", tracking.GetProperty("statusLabel").GetString());

        var timeline = tracking.GetProperty("timeline").EnumerateArray().ToArray();
        Assert.Equal(["Received", "Accepted", "Preparing", "Shipped"], timeline.Select(t => t.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(
            ["Porudžbina primljena", "Porudžbina prihvaćena", "Proizvodi se pripremaju", "Pošiljka predata kurirskoj službi"],
            timeline.Select(t => t.GetProperty("label").GetString()!).ToArray());
        Assert.Equal("Hvala, šaljemo uskoro.", timeline[1].GetProperty("customerMessage").GetString());
        Assert.Equal(JsonValueKind.Null, timeline[2].GetProperty("customerMessage").ValueKind);
        Assert.All(timeline, t => Assert.Equal(["at", "customerMessage", "label", "status"], t.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));

        var shipment = tracking.GetProperty("shipment");
        Assert.StartsWith("Brza pošta", shipment.GetProperty("courierName").GetString());
        Assert.Equal("PK 778/9", shipment.GetProperty("trackingNumber").GetString());
        Assert.Equal("https://kurir.example/pracenje?broj=PK%20778%2F9", shipment.GetProperty("trackingUrl").GetString());

        foreach (var secret in new[] { "Milica", "Petrović", email, "657654321", "765 4321", "Kraljevo", "Omladinska", "36000", "Tajna interna", "Interna napomena", "Džem", "katalog.owner" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unknown_revoked_and_malformed_codes_get_the_same_404()
    {
        var (orderId, _, code, _) = await PlaceAsync();
        var revokedResult = await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/tracking/revoke");
        Assert.Equal(1, revokedResult.GetProperty("revokedCount").GetInt32());

        var bodies = new List<string>();
        foreach (var input in new object?[]
                 {
                     TrackingCode.Format(TrackingCode.Generate()), code, "", "nope", "ABCDE-FGHJK", new string('Z', 5000),
                     "' OR 1=1 --", "UUUUU-UUUUU-UUUUU-UUUUU-UUUUUU", null,
                 })
        {
            var response = await LookupRawAsync(new { code = input });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("TRACKING_NOT_FOUND", (string?)problem["code"]);
            problem.Remove("traceId");
            bodies.Add(problem.ToJsonString());
        }

        Assert.Single(bodies.Distinct());

        // A missing member is a schema error, not a lookup.
        var missing = await LookupRawAsync("{}");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Lookup_is_limited_to_ten_per_ten_minutes_per_address()
    {
        await using var limited = new DevApiFactory
        {
            ConnectionString = _postgres.ConnectionString,
            ExtraSettings = new() { ["RateLimiting:Enabled"] = "true" },
        };
        using var client = limited.CreateClient();
        var address = $"10.66.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 255)}";

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await LookupRawAsync(new { code = "x" }, address, client)).StatusCode);
        }

        var blocked = await LookupRawAsync(new { code = "x" }, address, client);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(int.Parse(blocked.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Contains("RATE_LIMITED", await blocked.Content.ReadAsStringAsync());

        // Another address is not affected.
        Assert.Equal(HttpStatusCode.NotFound, (await LookupRawAsync(new { code = "x" }, "10.67.0.1", client)).StatusCode);
    }

    [Fact]
    public async Task A_failed_checkout_leaves_no_token_and_no_outbox_row()
    {
        await using var failing = new DevApiFactory
        {
            ConnectionString = _postgres.ConnectionString,
            TestServices = services =>
            {
                services.RemoveAll<IInventoryService>();
                services.AddScoped<IInventoryService>(sp => new FailingDeductionInventory(ActivatorUtilities.CreateInstance<InventoryService>(sp)));
            },
        };
        using var client = failing.CreateClient();
        var (_, variantId) = await _shop.ProductAsync(stock: 5);
        var email = Email();

        var response = await new ShopApi(client, _shop.Admin).PlaceRawAsync(ShopApi.Body([ShopApi.Item(variantId, 1)], email: email), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM orders WHERE contact_email = @e", ("e", email)));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM email_outbox WHERE recipient = @e", ("e", email)));
        Assert.Equal(0L, await CountAsync(
            "SELECT count(*) FROM order_tracking_tokens t WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.id = t.order_id)"));
    }

    [Fact]
    public async Task Dispatcher_sends_the_confirmation_and_erases_the_encrypted_code()
    {
        await _postgres.ExecuteAsync("UPDATE store_settings SET contact_email = 'kontakt@zladovce.test', phone = '+381 31 000 000' WHERE id = 1");
        try
        {
            var (orderId, number, code, email) = await PlaceAsync(quantity: 2);

            await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

            Assert.Equal("Sent", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", orderId)));
            Assert.True(await _postgres.ScalarAsync<bool>("SELECT encrypted_token IS NULL AND sent_at IS NOT NULL AND locked_until IS NULL FROM email_outbox WHERE order_id = @o", ("o", orderId)));
            Assert.Equal(1L, await CountAsync(
                "SELECT count(*) FROM email_logs l JOIN email_outbox m ON m.id = l.outbox_id WHERE m.order_id = @o AND l.succeeded AND l.ses_message_id IS NOT NULL AND l.attempt_number = 1",
                ("o", orderId)));

            var message = Assert.Single(await _mailpit.MessagesForAsync(email));
            Assert.Equal($"Porudžbina {number} je primljena", message.Subject);
            Assert.Equal("prodavnica@zladovce.test", message.From);
            Assert.Equal(["kontakt@zladovce.test"], message.ReplyTo);
            Assert.Equal(code, MailpitFixture.CodeFrom(message));
            Assert.Contains($"http://localhost:4200/track-order#kod={code}", message.Html);
            foreach (var part in new[] { number, "Zdravo, Milica!", "Džem od kupina", "Plaćanje pouzećem", code, "Omladinska 7", "kontakt@zladovce.test" })
            {
                Assert.Contains(part, message.Html);
                Assert.Contains(part, message.Text);
            }

            // The code in the email is the one that works.
            Assert.Equal(number, (await LookupAsync(MailpitFixture.CodeFrom(message))).GetProperty("orderNumber").GetString());

            // A second run sends nothing again.
            await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
            Assert.Single(await _mailpit.IdsForAsync(email));
        }
        finally
        {
            await _postgres.ExecuteAsync("UPDATE store_settings SET contact_email = NULL, phone = NULL WHERE id = 1");
        }
    }

    [Fact]
    public async Task Status_changes_queue_their_emails_with_fresh_codes_and_delivery_failed_sends_none()
    {
        var (orderId, number, firstCode, email) = await PlaceAsync();
        var courierId = await CourierAsync();

        await _shop.ChangeStatusAsync(orderId, "Accepted", customerMessage: "Pripremamo sve sa pažnjom.");
        await _shop.ChangeStatusAsync(orderId, "Preparing");
        await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courierId, trackingNumber = "RS123" });
        await _shop.ChangeStatusAsync(orderId, "DeliveryFailed", customerMessage: "Kurir vas nije zatekao.");
        await _shop.ChangeStatusAsync(orderId, "Shipped", shipment: new { courierCompanyId = courierId, trackingNumber = "RS124" });
        await _shop.ChangeStatusAsync(orderId, "Delivered");

        var templates = await _postgres.ScalarAsync<string[]>(
            "SELECT array_agg(template ORDER BY created_at, id) FROM email_outbox WHERE order_id = @o", ("o", orderId));
        Assert.Equal(["OrderReceived", "OrderAccepted", "OrderPreparing", "OrderShipped", "OrderShipped", "OrderDelivered"], templates!);

        // One row per history entry, keyed by it; DeliveryFailed's entry has none.
        Assert.Equal(5L, await CountAsync(
            "SELECT count(*) FROM email_outbox m JOIN order_status_history h ON m.dedup_key = 'order:' || h.order_id || ':history:' || h.id WHERE h.order_id = @o",
            ("o", orderId)));
        Assert.Equal(0L, await CountAsync(
            "SELECT count(*) FROM email_outbox m JOIN order_status_history h ON m.dedup_key LIKE '%' || h.id WHERE h.order_id = @o AND h.new_status = 'DeliveryFailed'",
            ("o", orderId)));
        Assert.Equal(6L, await CountAsync("SELECT count(*) FROM order_tracking_tokens WHERE order_id = @o", ("o", orderId)));

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

        var messages = await _mailpit.MessagesForAsync(email);
        Assert.Equal(6, messages.Count);
        var accepted = Assert.Single(messages, m => m.Subject == $"Porudžbina {number} je prihvaćena");
        Assert.Contains("Pripremamo sve sa pažnjom.", accepted.Html);
        var shipped = messages.Where(m => m.Subject == $"Porudžbina {number} je poslata").ToList();
        Assert.Equal(2, shipped.Count);
        Assert.Contains(shipped, m => m.Html.Contains("RS123") && m.Html.Contains("https://kurir.example/pracenje?broj=RS123"));
        Assert.Contains(shipped, m => m.Html.Contains("RS124"));
        Assert.DoesNotContain(messages, m => m.Html.Contains("Kurir vas nije zatekao."));
        Assert.Single(messages, m => m.Subject == $"Porudžbina {number} je isporučena");

        // Every email carries its own working code.
        var codes = messages.Select(MailpitFixture.CodeFrom).ToList();
        Assert.Contains(firstCode, codes);
        Assert.Equal(6, codes.Distinct().Count());
        foreach (var code in codes)
        {
            Assert.Equal("Delivered", (await LookupAsync(code)).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Rejected_sends_the_cancellation_email_with_the_message()
    {
        var (orderId, number, _, email) = await PlaceAsync();

        await _shop.ChangeStatusAsync(orderId, "Rejected", customerMessage: "Nažalost, ne dostavljamo na ostrva <b>Jadrana</b>.");

        Assert.Equal("OrderCancelled", await _postgres.ScalarAsync<string>(
            "SELECT template FROM email_outbox WHERE order_id = @o AND dedup_key LIKE '%:history:%'", ("o", orderId)));
        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

        var message = Assert.Single(await _mailpit.MessagesForAsync(email), m => m.Subject.StartsWith($"Porudžbina {number} nije", StringComparison.Ordinal));
        Assert.Equal($"Porudžbina {number} nije prihvaćena", message.Subject);
        Assert.Contains("Nažalost, ne dostavljamo na ostrva &lt;b&gt;Jadrana&lt;/b&gt;.", message.Html);
        Assert.Contains("Nažalost, ne dostavljamo na ostrva <b>Jadrana</b>.", message.Text);
    }

    [Fact]
    public async Task Failures_retry_with_back_off_end_dead_and_an_administrator_can_send_again()
    {
        var (orderId, _, code, email) = await PlaceAsync();
        var outboxId = await _postgres.ScalarAsync<Guid>("SELECT id FROM email_outbox WHERE order_id = @o", ("o", orderId));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));

        await using (var down = new RejectingSmtpServer())
        {
            var worker = new OutboxWorker(_postgres.ConnectionString, "127.0.0.1", down.Port, clock);
            TimeSpan[] delays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

            for (var attempt = 1; attempt <= 6; attempt++)
            {
                await worker.DrainAsync();

                Assert.Equal(attempt, await _postgres.ScalarAsync<int>("SELECT attempt_count FROM email_outbox WHERE id = @i", ("i", outboxId)));
                var status = await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE id = @i", ("i", outboxId));
                if (attempt < 6)
                {
                    Assert.Equal("Pending", status);
                    var next = await _postgres.ScalarAsync<DateTime>("SELECT next_attempt_at FROM email_outbox WHERE id = @i", ("i", outboxId));
                    Assert.Equal(clock.GetUtcNow().UtcDateTime + delays[attempt - 1], next, TimeSpan.FromMilliseconds(5));

                    // Not due before its time.
                    clock.Advance(delays[attempt - 1] - TimeSpan.FromSeconds(1));
                    await worker.DrainAsync();
                    Assert.Equal(attempt, await _postgres.ScalarAsync<int>("SELECT attempt_count FROM email_outbox WHERE id = @i", ("i", outboxId)));
                    clock.Advance(TimeSpan.FromSeconds(1));
                }
                else
                {
                    Assert.Equal("Dead", status);
                }
            }

            Assert.True(down.Sessions >= 6);
        }

        Assert.Equal(6L, await CountAsync("SELECT count(*) FROM email_logs WHERE outbox_id = @i AND NOT succeeded AND error LIKE '%554%'", ("i", outboxId)));
        var lastError = await _postgres.ScalarAsync<string>("SELECT last_error FROM email_outbox WHERE id = @i", ("i", outboxId));
        Assert.Contains("554", lastError);
        Assert.DoesNotContain(email, lastError);
        Assert.NotNull(await _postgres.ScalarAsync<byte[]>("SELECT encrypted_token FROM email_outbox WHERE id = @i", ("i", outboxId)));

        // The order was never at risk: it is there and its code works.
        Assert.Equal("Received", (await LookupAsync(code)).GetProperty("status").GetString());

        // Admin list shows it as Dead with the error; retrying anything else is refused.
        var dead = await _shop.Admin.GetAsync($"/api/v1/admin/emails?status=Dead&orderId={orderId}");
        var item = Assert.Single(dead.Items());
        Assert.Equal(outboxId, item.Id());
        Assert.Equal(6, item.GetProperty("attempts").GetInt32());
        Assert.Contains("554", item.GetProperty("lastError").GetString());

        var retried = await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/emails/{outboxId}/retry");
        Assert.Equal("Pending", retried.GetProperty("status").GetString());
        Assert.Equal(0, retried.GetProperty("attempts").GetInt32());
        var again = await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/emails/{outboxId}/retry");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("EMAIL_NOT_RETRYABLE", await again.Content.ReadAsStringAsync());

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

        Assert.Equal("Sent", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE id = @i", ("i", outboxId)));
        Assert.Equal(7, await _postgres.ScalarAsync<int>("SELECT max(attempt_number) FROM email_logs WHERE outbox_id = @i AND succeeded", ("i", outboxId)));
        var message = Assert.Single(await _mailpit.MessagesForAsync(email));
        Assert.Equal(code, MailpitFixture.CodeFrom(message));

        var detail = await _shop.Admin.GetAsync($"/api/v1/admin/emails/{outboxId}");
        Assert.Equal(7, detail.GetProperty("attemptLog").GetArrayLength());
        Assert.True(detail.GetProperty("attemptLog")[6].GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Retrying_a_dead_email_whose_code_is_gone_issues_a_new_working_code()
    {
        var (orderId, number, _, email) = await PlaceAsync();
        var outboxId = await _postgres.ScalarAsync<Guid>("SELECT id FROM email_outbox WHERE order_id = @o", ("o", orderId));
        await _postgres.ExecuteAsync(
            "UPDATE email_outbox SET status = 'Dead', attempt_count = 6, encrypted_token = NULL, last_error = 'test' WHERE id = @i", ("i", outboxId));

        await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/emails/{outboxId}/retry");

        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM order_tracking_tokens WHERE order_id = @o", ("o", orderId)));
        Assert.NotNull(await _postgres.ScalarAsync<byte[]>("SELECT encrypted_token FROM email_outbox WHERE id = @i", ("i", outboxId)));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM audit_logs WHERE action = 'Email.Retried' AND entity_id = @i", ("i", outboxId.ToString())));

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        var message = Assert.Single(await _mailpit.MessagesForAsync(email));
        Assert.Equal(number, (await LookupAsync(MailpitFixture.CodeFrom(message))).GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task An_order_is_never_lost_while_the_mail_server_is_down()
    {
        await using var down = new RejectingSmtpServer();
        var (orderId, number, code, _) = await PlaceAsync();

        await new OutboxWorker(_postgres.ConnectionString, "127.0.0.1", down.Port).DrainAsync();

        Assert.Equal(number, await _postgres.ScalarAsync<string>("SELECT order_number FROM orders WHERE id = @o", ("o", orderId)));
        Assert.Equal("Pending", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", orderId)));
        Assert.Equal(1, await _postgres.ScalarAsync<int>("SELECT attempt_count FROM email_outbox WHERE order_id = @o", ("o", orderId)));
        Assert.Equal(number, (await LookupAsync(code)).GetProperty("orderNumber").GetString());
        var detail = await _shop.DetailAsync(orderId);
        Assert.Equal("Pending", Assert.Single(detail.GetProperty("emails").EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Two_concurrent_dispatchers_never_send_an_email_twice()
    {
        var emails = new List<string>();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var placed = await PlaceAsync();
            emails.Add(placed.Email);
            ids.Add(placed.Id);
        }

        var first = OutboxWorker.For(_postgres, _mailpit);
        var second = OutboxWorker.For(_postgres, _mailpit);
        await Task.WhenAll(Task.Run(first.DrainAsync), Task.Run(second.DrainAsync), Task.Run(first.DrainAsync));

        foreach (var email in emails)
        {
            Assert.Single(await _mailpit.IdsForAsync(email));
        }

        foreach (var id in ids)
        {
            Assert.Equal(1L, await CountAsync(
                "SELECT count(*) FROM email_logs l JOIN email_outbox m ON m.id = l.outbox_id WHERE m.order_id = @o", ("o", id)));
            Assert.Equal("Sent", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", id)));
        }
    }

    [Fact]
    public async Task Resend_confirmation_issues_a_new_code_and_the_old_one_keeps_working()
    {
        var (orderId, number, oldCode, email) = await PlaceAsync();

        var first = await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/emails/resend-confirmation");
        var second = await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/emails/resend-confirmation");

        Assert.Equal("OrderReceived", first.GetProperty("template").GetString());
        Assert.True(first.GetProperty("isResent").GetBoolean());
        Assert.Equal(number, first.GetProperty("orderNumber").GetString());
        Assert.Equal($"order:{orderId:D}:resend:1", await _postgres.ScalarAsync<string>("SELECT dedup_key FROM email_outbox WHERE id = @i", ("i", first.Id())));
        Assert.Equal($"order:{orderId:D}:resend:2", await _postgres.ScalarAsync<string>("SELECT dedup_key FROM email_outbox WHERE id = @i", ("i", second.Id())));
        Assert.Equal(3L, await CountAsync("SELECT count(*) FROM order_tracking_tokens WHERE order_id = @o", ("o", orderId)));
        Assert.Equal(HttpStatusCode.NotFound, (await _shop.Admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/orders/{Guid.CreateVersion7()}/emails/resend-confirmation")).StatusCode);

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

        var messages = await _mailpit.MessagesForAsync(email);
        Assert.Equal(3, messages.Count);
        var resent = messages.Where(m => m.Subject == $"Potvrda porudžbine {number}").ToList();
        Assert.Equal(2, resent.Count);
        var newCodes = resent.Select(MailpitFixture.CodeFrom).ToList();
        Assert.DoesNotContain(oldCode, newCodes);
        foreach (var code in newCodes.Append(oldCode))
        {
            Assert.Equal(number, (await LookupAsync(code)).GetProperty("orderNumber").GetString());
        }

        // Revoking makes every code of the order invalid; the next issued one works again.
        await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/tracking/revoke");
        foreach (var code in newCodes.Append(oldCode))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await LookupRawAsync(new { code })).StatusCode);
        }

        await _shop.Admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/orders/{orderId}/emails/resend-confirmation");
        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        var newest = (await _mailpit.MessagesForAsync(email)).Select(MailpitFixture.CodeFrom).Except(newCodes.Append(oldCode)).Single();
        Assert.Equal(number, (await LookupAsync(newest)).GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task Admin_email_list_filters_and_never_contains_bodies()
    {
        var (orderId, number, _, email) = await PlaceAsync();
        await _shop.ChangeStatusAsync(orderId, "Accepted");

        var list = await _shop.Admin.GetAsync($"/api/v1/admin/emails?orderId={orderId}");
        Assert.Equal(2, list.GetProperty("totalItems").GetInt32());
        var items = list.Items();
        Assert.Equal(["OrderAccepted", "OrderReceived"], items.Select(i => i.GetProperty("template").GetString()!).ToArray());
        Assert.All(items, i =>
        {
            Assert.Equal(number, i.GetProperty("orderNumber").GetString());
            Assert.Equal(email, i.GetProperty("recipient").GetString());
            Assert.Equal("Pending", i.GetProperty("status").GetString());
            Assert.Equal(
                ["attempts", "createdAt", "id", "isResent", "lastError", "nextAttemptAt", "orderId", "orderNumber", "recipient", "sentAt", "status", "template"],
                i.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        });

        Assert.Empty((await _shop.Admin.GetAsync($"/api/v1/admin/emails?orderId={orderId}&status=Sent,Dead")).Items());
        Assert.Equal(2, (await _shop.Admin.GetAsync($"/api/v1/admin/emails?orderId={orderId}&status=Pending&status=Sent")).Items().Length);
        Assert.True((await _shop.Admin.GetAsync("/api/v1/admin/emails?pageSize=1")).GetProperty("totalItems").GetInt32() >= 2);
        Assert.Equal(HttpStatusCode.BadRequest, (await _shop.Admin.SendAsync(HttpMethod.Get, "/api/v1/admin/emails?status=Lost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _shop.Admin.SendAsync(HttpMethod.Get, $"/api/v1/admin/emails/{Guid.CreateVersion7()}")).StatusCode);

        var detail = await _shop.DetailAsync(orderId);
        var emails = detail.GetProperty("emails").EnumerateArray().ToArray();
        Assert.Equal(["OrderReceived", "OrderAccepted"], emails.Select(e => e.GetProperty("template").GetString()!).ToArray());

        var raw = await (await _shop.Admin.SendAsync(HttpMethod.Get, $"/api/v1/admin/emails?orderId={orderId}")).Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "payload", "html", "encrypted", "Milica", "Omladinska", "Džem" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        // Public callers cannot see it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/admin/emails")).StatusCode);
    }

    [Fact]
    public async Task Worker_purges_expired_rate_limit_windows_and_sends_nothing_while_email_is_disabled()
    {
        var oldKey = $"test:{Unique.Token()}";
        var freshKey = $"test:{Unique.Token()}";
        await _postgres.ExecuteAsync("INSERT INTO rate_limit_buckets (bucket_key, window_start, count) VALUES (@k, now() - interval '2 days', 3)", ("k", oldKey));
        await _postgres.ExecuteAsync("INSERT INTO rate_limit_buckets (bucket_key, window_start, count) VALUES (@k, now() - interval '5 minutes', 3)", ("k", freshKey));
        var (orderId, _, _, _) = await PlaceAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["Database:ConnectionString"] = _postgres.ConnectionString,
                ["Email:Enabled"] = "false",
                ["Storefront:BaseUrl"] = "http://localhost:4200",
                ["Tracking:TokenEncryptionKey"] = Zladovce.Application.Notifications.TrackingOptions.DevelopmentKey,
            })
            .Build();
        var result = await new Zladovce.Lambda.OutboxWorkerFunction(configuration).HandleAsync(null, new FakeLambdaContext());

        Assert.Equal(0, result.Claimed);
        Assert.True(result.PurgedRateLimitBuckets >= 1);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM rate_limit_buckets WHERE bucket_key = @k", ("k", oldKey)));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM rate_limit_buckets WHERE bucket_key = @k", ("k", freshKey)));
        Assert.Equal("Pending", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", orderId)));
        Assert.Equal(0, await _postgres.ScalarAsync<int>("SELECT attempt_count FROM email_outbox WHERE order_id = @o", ("o", orderId)));
    }

    [Fact]
    public async Task An_abandoned_lease_is_taken_over_and_one_that_was_the_last_attempt_ends_dead()
    {
        var (resumedOrder, _, _, resumedEmail) = await PlaceAsync();
        var (lastOrder, _, _, lastEmail) = await PlaceAsync();
        var (heldOrder, _, _, heldEmail) = await PlaceAsync();

        // A worker died while sending: attempt 2 of the first, attempt 6 (the last) of the second. The third is still leased.
        await _postgres.ExecuteAsync(
            "UPDATE email_outbox SET status = 'Processing', attempt_count = 2, locked_until = now() - interval '1 second' WHERE order_id = @o", ("o", resumedOrder));
        await _postgres.ExecuteAsync(
            "UPDATE email_outbox SET status = 'Processing', attempt_count = 6, locked_until = now() - interval '1 second' WHERE order_id = @o", ("o", lastOrder));
        await _postgres.ExecuteAsync(
            "UPDATE email_outbox SET status = 'Processing', attempt_count = 1, locked_until = now() + interval '1 hour' WHERE order_id = @o", ("o", heldOrder));

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();

        Assert.Equal("Sent", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", resumedOrder)));
        Assert.Equal(3, await _postgres.ScalarAsync<int>("SELECT attempt_count FROM email_outbox WHERE order_id = @o", ("o", resumedOrder)));
        Assert.Single(await _mailpit.IdsForAsync(resumedEmail));

        Assert.Equal("Dead", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", lastOrder)));
        Assert.Contains("prekinuto", await _postgres.ScalarAsync<string>("SELECT last_error FROM email_outbox WHERE order_id = @o", ("o", lastOrder)));
        Assert.Empty(await _mailpit.IdsForAsync(lastEmail));

        Assert.Equal("Processing", await _postgres.ScalarAsync<string>("SELECT status FROM email_outbox WHERE order_id = @o", ("o", heldOrder)));
        Assert.Empty(await _mailpit.IdsForAsync(heldEmail));
    }
}
