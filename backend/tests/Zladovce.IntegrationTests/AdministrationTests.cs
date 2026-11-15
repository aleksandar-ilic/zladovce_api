using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Zladovce.Infrastructure.Site;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

/// <summary>Admin users, the audit log, the store logo and the storefront rebuild webhook.</summary>
[Collection(PostgresCollection.Name)]
public class AdministrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _owner;

    public AdministrationTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _owner = new AdminApi(_client, $"vlasnik-{Unique.Token()}@example.com");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<JsonElement> MeAsync(AdminApi api) => await api.GetAsync("/api/v1/admin/me");

    [Fact]
    public async Task Owner_invites_and_changes_administrators_with_the_safety_rules()
    {
        var me = await MeAsync(_owner);
        var email = $"urednik-{Unique.Token()}@example.com";
        var invited = await _owner.JsonAsync(HttpMethod.Post, "/api/v1/admin/users", new { email, fullName = "Ana Urednik", role = "Staff" }, HttpStatusCode.Created);
        Assert.Equal("Staff", invited.GetProperty("role").GetString());
        Assert.Equal("Urednik", invited.GetProperty("roleLabel").GetString());
        Assert.Equal(JsonValueKind.Null, invited.GetProperty("lastLoginAt").ValueKind);
        Assert.Equal($"dev:{email}", await _postgres.ScalarAsync<string>("SELECT cognito_sub FROM admin_users WHERE id = @id", ("id", invited.Id())));

        // The invited person signs in with the development headers and finds the prepared row.
        var invitee = new AdminApi(_client, email, "Staff");
        Assert.Equal(invited.Id(), (await MeAsync(invitee)).Id());
        Assert.Equal("Ana Urednik", (await MeAsync(invitee)).GetProperty("fullName").GetString());

        var taken = await _owner.SendAsync(HttpMethod.Post, "/api/v1/admin/users", new { email = email.ToUpperInvariant(), fullName = "X", role = "Staff" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("ADMIN_EMAIL_TAKEN", await taken.Content.ReadAsStringAsync());

        // Staff cannot manage administrators or read the audit log.
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.SendAsync(HttpMethod.Get, "/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{me.Id()}", new { role = "Staff", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitee.SendAsync(HttpMethod.Get, "/api/v1/admin/audit-logs")).StatusCode);

        // Self-demotion and self-deactivation are refused.
        var self = await _owner.SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{me.Id()}", new { role = "Staff", isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Contains("CANNOT_CHANGE_SELF", await self.Content.ReadAsStringAsync());
        Assert.Contains("CANNOT_CHANGE_SELF", await (await _owner.SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{me.Id()}", new { role = "Owner", isActive = false })).Content.ReadAsStringAsync());

        // Deactivation blocks the user at once; activation lets them in again.
        var deactivated = await _owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/users/{invited.Id()}", new { role = "Staff", isActive = false });
        Assert.False(deactivated.GetProperty("isActive").GetBoolean());
        var blocked = await invitee.SendAsync(HttpMethod.Get, "/api/v1/admin/me");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("ADMIN_INACTIVE", await blocked.Content.ReadAsStringAsync());
        await _owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/users/{invited.Id()}", new { role = "Owner", isActive = true });

        var audit = await _owner.GetAsync($"/api/v1/admin/audit-logs?entityType=AdminUser&entityId={invited.Id()}");
        var actions = audit.Items().Select(i => i.GetProperty("action").GetString()!).ToList();
        Assert.Contains("AdminUser.Invited", actions);
        Assert.Contains("AdminUser.Deactivated", actions);
        Assert.Contains("AdminUser.Activated", actions);
        Assert.Contains("AdminUser.RoleChanged", actions);
        Assert.Contains(audit.Items(), i => i.GetProperty("summary").GetString() == "Administrator – promena uloge");
    }

    [Fact]
    public async Task The_last_active_owner_can_be_neither_demoted_nor_deactivated()
    {
        // A database of this test's own, so that exactly one Owner exists.
        var connectionString = await _postgres.CreateEmptyDatabaseAsync($"owners_{Unique.Token()}");
        await PostgresFixture.RunMigrationsAsync(connectionString);
        await using var factory = new DevApiFactory { ConnectionString = connectionString };
        using var client = factory.CreateClient();
        var owner = new AdminApi(client, "jedini.vlasnik@example.com");
        var staff = new AdminApi(client, "urednik.vlasnik@example.com", "Staff");
        var ownerId = (await MeAsync(owner)).Id();
        var staffId = (await MeAsync(staff)).Id();

        // Over HTTP every caller allowed to change administrators is an Owner itself (the role follows the token), so the rule
        // is exercised at the service with a caller whose row is not an active Owner (e.g. a token issued before a demotion).
        async Task<Exception?> UpdateAsync(Guid target, string role, bool active)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<Zladovce.Application.Common.IRequestContext>();
            context.SetAdmin(new Zladovce.Application.Common.CurrentAdmin(staffId, "urednik.vlasnik@example.com", "Urednik", Zladovce.Domain.Identity.AdminRole.Owner));
            var service = scope.ServiceProvider.GetRequiredService<Zladovce.Application.Identity.IAdminUserService>();
            return await Record.ExceptionAsync(() => service.UpdateAsync(target, new Zladovce.Application.Identity.UpdateAdminRequest
            {
                Role = Enum.Parse<Zladovce.Domain.Identity.AdminRole>(role),
                IsActive = active,
            }, CancellationToken.None));
        }

        var demote = Assert.IsType<Zladovce.Application.Common.Errors.ConflictException>(await UpdateAsync(ownerId, "Staff", true));
        Assert.Equal("LAST_OWNER", demote.Code);
        var deactivate = Assert.IsType<Zladovce.Application.Common.Errors.ConflictException>(await UpdateAsync(ownerId, "Owner", false));
        Assert.Equal("LAST_OWNER", deactivate.Code);

        // With a second active Owner the same change is allowed.
        await owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/users/{staffId}", new { role = "Owner", isActive = true });
        Assert.Null(await UpdateAsync(ownerId, "Staff", true));
        Assert.Equal("Staff", await RoleOfAsync(connectionString, ownerId));
    }

    private static async Task<string?> RoleOfAsync(string connectionString, Guid id)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT role FROM admin_users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Audit_log_filters_and_summaries()
    {
        var me = await MeAsync(_owner);
        var category = await _owner.CreateCategoryAsync();
        await _owner.JsonAsync(HttpMethod.Put, $"/api/v1/admin/categories/{category.Id()}", AdminApi.Category("Novo ime " + Unique.Token(), isActive: false));

        var mine = await _owner.GetAsync($"/api/v1/admin/audit-logs?adminUserId={me.Id()}&entityType=Category&entityId={category.Id()}");
        var items = mine.Items();
        Assert.Equal(["Category.Updated", "Category.Created"], items.Select(i => i.GetProperty("action").GetString()!));
        Assert.All(items, i => Assert.Equal(me.Id(), i.GetProperty("adminUserId").GetGuid()));
        Assert.StartsWith("Kategorija – izmena (", items[0].GetProperty("summary").GetString());
        Assert.Contains("isActive", items[0].GetProperty("summary").GetString());
        Assert.Equal(me.GetProperty("email").GetString(), items[0].GetProperty("adminEmail").GetString());

        var byAction = await _owner.GetAsync($"/api/v1/admin/audit-logs?action=Category.Created&entityId={category.Id()}");
        Assert.Single(byAction.Items());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var future = await _owner.GetAsync($"/api/v1/admin/audit-logs?entityId={category.Id()}&from={today.AddDays(2):yyyy-MM-dd}");
        Assert.Empty(future.Items());
        var past = await _owner.GetAsync($"/api/v1/admin/audit-logs?entityId={category.Id()}&to={today.AddDays(-2):yyyy-MM-dd}");
        Assert.Empty(past.Items());

        var filters = await _owner.GetAsync("/api/v1/admin/audit-logs/filters");
        Assert.Contains(filters.GetProperty("entityTypes").EnumerateArray(), t => t.GetProperty("value").GetString() == "Category" && t.GetProperty("label").GetString() == "Kategorija");

        var invalid = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/audit-logs?from=2026-05-02&to=2026-05-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Logo_is_set_kept_replaced_and_removed_through_the_settings()
    {
        var current = await _owner.GetAsync("/api/v1/admin/settings");
        var original = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!;

        Dictionary<string, object?> Body(JsonElement settings, string? logoKey)
        {
            var body = new Dictionary<string, object?>();
            foreach (var name in new[] { "version", "storeName", "contactEmail", "phone", "businessAddress", "shippingEnabled", "shippingPrice", "freeShippingThreshold",
                "minOrderAmount", "estimatedDeliveryText", "deliveryInfo", "facebookUrl", "instagramUrl", "otherLinks", "defaultSeoTitle", "defaultSeoDescription" })
            {
                body[name] = settings.GetProperty(name);
            }

            body["logoKey"] = logoKey;
            return body;
        }

        var upload = await MediaTestSupport.UploadAsync(_owner, _client, "logo", 1200, 400);
        var withLogo = await _owner.JsonAsync(HttpMethod.Put, "/api/v1/admin/settings", Body(current, upload));
        var logo = withLogo.GetProperty("logo");
        var key = logo.GetProperty("key").GetString()!;
        Assert.StartsWith("media/logos/", key);
        Assert.Equal(key, withLogo.GetProperty("logoKey").GetString());
        Assert.EndsWith($"{key}/email.png", logo.GetProperty("emailPngUrl").GetString());
        var png = await _minio.ReadAsync($"{key}/email.png");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.True(await _minio.ExistsAsync($"{key}/400.webp"));
        Assert.Equal(key, (await (await _client.GetAsync("/api/v1/store")).Content.ReadFromJsonAsyncSafe()).GetProperty("logo").GetProperty("key").GetString());

        var kept = await _owner.JsonAsync(HttpMethod.Put, "/api/v1/admin/settings", Body(withLogo, key));
        Assert.Equal(key, kept.GetProperty("logoKey").GetString());

        var wrongPurpose = await _owner.SendAsync(HttpMethod.Put, "/api/v1/admin/settings", Body(kept, await MediaTestSupport.UploadAsync(_owner, _client, "blog")));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPurpose.StatusCode);
        Assert.Contains("\"logoKey\"", await wrongPurpose.Content.ReadAsStringAsync());

        var replaced = await _owner.JsonAsync(HttpMethod.Put, "/api/v1/admin/settings", Body(kept, await MediaTestSupport.UploadAsync(_owner, _client, "logo", 300, 100)));
        Assert.NotEqual(key, replaced.GetProperty("logoKey").GetString());
        Assert.False(await _minio.ExistsAsync($"{key}/email.png"));

        var removed = await _owner.JsonAsync(HttpMethod.Put, "/api/v1/admin/settings", Body(replaced, null));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("logo").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("logoKey").ValueKind);
        _ = original;
    }

    [Fact]
    public async Task Content_changes_trigger_one_debounced_rebuild_call()
    {
        await using var hook = new RecordingHttpServer();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new Dictionary<string, string?>(_minio.Settings())
        {
            ["Site:RebuildWebhookUrl"] = hook.Url,
            ["Site:RebuildMinIntervalSeconds"] = "300",
        };
        await using var factory = new DevApiFactory { ConnectionString = _postgres.ConnectionString, ExtraSettings = settings, TestServices = s => s.AddSingleton<TimeProvider>(clock) };
        using var client = factory.CreateClient();
        var admin = new AdminApi(client, "rebuild.owner@example.com");

        async Task<SiteRebuildResult> RunAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISiteRebuildDispatcher>().RunAsync(CancellationToken.None);
        }

        // Start from a clean state: whatever other tests requested is sent once now (or is already within the interval).
        await _postgres.ExecuteAsync("UPDATE site_rebuild_requests SET pending = false, last_triggered_at = NULL WHERE id = 1");
        Assert.Equal("Idle", (await RunAsync()).Outcome);

        // A FAQ change (prerendered) requests a rebuild; a rebuild is called once with an empty POST.
        var faq = await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/faq", new { question = $"Rebuild {Unique.Token()}?", answer = "Da.", isPublished = false }, HttpStatusCode.Created);
        Assert.True(await _postgres.ScalarAsync<bool>("SELECT pending FROM site_rebuild_requests WHERE id = 1"));
        Assert.Equal("Triggered", (await RunAsync()).Outcome);
        Assert.Single(hook.Requests);
        Assert.Equal("POST", hook.Requests[0].Method);
        Assert.Equal(0, hook.Requests[0].BodyLength);

        // More changes within five minutes are coalesced: no call until the interval is over.
        await admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/faq/{faq.Id()}", new { question = "Rebuild 2?", answer = "Da.", isPublished = true });
        await admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/faq/{faq.Id()}", null, HttpStatusCode.NoContent);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("Waiting", (await RunAsync()).Outcome);
        Assert.Single(hook.Requests);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Triggered", (await RunAsync()).Outcome);
        Assert.Equal(2, hook.Requests.Count);
        Assert.Equal("Idle", (await RunAsync()).Outcome);

        // Changes that are not prerendered (an expense) request nothing.
        await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/expenses", new { expenseDate = "2026-01-01", category = "Ostalo", description = "Test", amount = 1m }, HttpStatusCode.Created);
        Assert.False(await _postgres.ScalarAsync<bool>("SELECT pending FROM site_rebuild_requests WHERE id = 1"));

        // A failing webhook keeps the request pending.
        hook.StatusCode = 500;
        await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/testimonials", new { authorName = "Test", place = (string?)null, text = "Rebuild", isPublished = false, sortOrder = 0 }, HttpStatusCode.Created);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("Failed", (await RunAsync()).Outcome);
        Assert.True(await _postgres.ScalarAsync<bool>("SELECT pending FROM site_rebuild_requests WHERE id = 1"));
        Assert.Equal("HTTP 500", await _postgres.ScalarAsync<string>("SELECT last_result FROM site_rebuild_requests WHERE id = 1"));
        await _postgres.ExecuteAsync("UPDATE site_rebuild_requests SET pending = false WHERE id = 1");
    }

    [Fact]
    public async Task Without_a_webhook_url_nothing_is_called()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISiteRebuildDispatcher>().RunAsync(CancellationToken.None);
        Assert.Equal("NotConfigured", result.Outcome);
    }
}

/// <summary>A minimal HTTP/1.1 server on a loopback port that records requests and answers with <see cref="StatusCode"/>.</summary>
public sealed class RecordingHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RecordingHttpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public sealed record Request(string Method, string Path, int BodyLength);

    public List<Request> Requests { get; } = [];

    public int StatusCode { get; set; } = 200;

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/build-hook";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var head = received.ToString();
                var requestLine = head.Split("\r\n")[0].Split(' ');
                var length = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } header
                    ? int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                lock (Requests)
                {
                    Requests.Add(new Request(requestLine[0], requestLine[1], length));
                }

                var reason = StatusCode == 200 ? "OK" : "Error";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {StatusCode} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}

internal static class HttpContentExtensions
{
    public static async Task<JsonElement> ReadFromJsonAsyncSafe(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync()).RootElement.Clone();
}
