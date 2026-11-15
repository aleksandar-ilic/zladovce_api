using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

/// <summary>Newsletter double opt-in, unsubscribe, honeypots, rate limits and the contact form, with real emails in Mailpit.</summary>
[Collection(PostgresCollection.Name)]
public class MarketingTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly MailpitFixture _mailpit;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _owner;
    private string? _originalContactEmail;

    public MarketingTests(PostgresFixture postgres, MailpitFixture mailpit)
    {
        _postgres = postgres;
        _mailpit = mailpit;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString };
        _client = _factory.CreateClient();
        _owner = new AdminApi(_client, "marketing.owner@example.com");
    }

    public async Task InitializeAsync() =>
        _originalContactEmail = await _postgres.ScalarAsync<string>("SELECT contact_email::text FROM store_settings WHERE id = 1");

    public async Task DisposeAsync()
    {
        await SetContactEmailAsync(_originalContactEmail);
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task SetContactEmailAsync(string? email) =>
        email is null
            ? _postgres.ExecuteAsync("UPDATE store_settings SET contact_email = NULL WHERE id = 1")
            : _postgres.ExecuteAsync("UPDATE store_settings SET contact_email = @e WHERE id = 1", ("e", email));

    private async Task<HttpResponseMessage> PostAsync(string url, object body, string? forwardedFor = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await (client ?? _client).SendAsync(request);
    }

    private static string Token(MailpitFixture.Message message, string page) =>
        Regex.Match(message.Html, $"/newsletter/{page}#token=([A-Za-z0-9_\\-]+\\.[A-Za-z0-9_\\-]+)\"").Groups[1].Value is { Length: > 0 } token
            ? token
            : throw new InvalidOperationException($"No {page} link in the email.");

    private async Task<string?> StatusAsync(string email) =>
        await _postgres.ScalarAsync<string>(
            "SELECT CASE WHEN unsubscribed_at IS NOT NULL THEN 'Unsubscribed' WHEN confirmed_at IS NOT NULL THEN 'Confirmed' ELSE 'Pending' END FROM newsletter_subscribers WHERE email = @e",
            ("e", email));

    [Fact]
    public async Task Double_opt_in_confirm_and_one_click_unsubscribe()
    {
        var email = $"novosti-{Unique.Token()}@example.com";
        var response = await PostAsync("/api/v1/newsletter/subscriptions", new { email, consent = true });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadAsStringAsync();
        Assert.Equal("Pending", await StatusAsync(email));

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        var message = Assert.Single(await _mailpit.MessagesForAsync(email));
        Assert.Contains("Potvrdite prijavu", message.Subject);
        var confirm = Token(message, "potvrda");
        var unsubscribe = Token(message, "odjava");
        Assert.Contains("http://localhost:4200/newsletter/potvrda#token=", message.Html);

        // A repeated sign-up shortly after: same answer, no second email.
        var again = await PostAsync("/api/v1/newsletter/subscriptions", new { email = email.ToUpperInvariant(), consent = true });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(accepted, await again.Content.ReadAsStringAsync());
        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        Assert.Single(await _mailpit.MessagesForAsync(email));

        var confirmed = await PostAsync("/api/v1/newsletter/confirm", new { token = confirm });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("Confirmed", await StatusAsync(email));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/v1/newsletter/confirm", new { token = confirm })).StatusCode); // idempotent

        // Signing up a confirmed address: same 202, nothing sent.
        var known = await PostAsync("/api/v1/newsletter/subscriptions", new { email, consent = true });
        Assert.Equal(accepted, await known.Content.ReadAsStringAsync());

        // Admin export lists confirmed addresses only.
        var export = await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/newsletter/subscribers/export");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains(email, Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync()));
        var list = await _owner.GetAsync($"/api/v1/admin/newsletter/subscribers?status=Confirmed&q={Uri.EscapeDataString(email)}");
        Assert.Equal("Confirmed", list.GetProperty("subscribers").Items().Single().GetProperty("status").GetString());

        var unsubscribed = await PostAsync("/api/v1/newsletter/unsubscribe", new { token = unsubscribe });
        Assert.Equal(HttpStatusCode.OK, unsubscribed.StatusCode);
        Assert.Equal("Unsubscribed", await StatusAsync(email));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/v1/newsletter/unsubscribe", new { token = unsubscribe })).StatusCode);
        Assert.DoesNotContain(email, Encoding.UTF8.GetString(await (await _owner.SendAsync(HttpMethod.Get, "/api/v1/admin/newsletter/subscribers/export")).Content.ReadAsByteArrayAsync()));

        // The old confirmation link cannot re-subscribe an unsubscribed address.
        var stale = await PostAsync("/api/v1/newsletter/confirm", new { token = confirm });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("NEWSLETTER_TOKEN_INVALID", await stale.Content.ReadAsStringAsync());

        // Signing up again starts a new double opt-in with a new link.
        await PostAsync("/api/v1/newsletter/subscriptions", new { email, consent = true });
        Assert.Equal("Pending", await StatusAsync(email));
        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        Assert.Equal(2, (await _mailpit.MessagesForAsync(email)).Count);
    }

    [Fact]
    public async Task Tokens_are_checked_and_purposes_are_not_interchangeable()
    {
        var email = $"tokeni-{Unique.Token()}@example.com";
        await PostAsync("/api/v1/newsletter/subscriptions", new { email, consent = true });
        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        var message = Assert.Single(await _mailpit.MessagesForAsync(email));
        var confirm = Token(message, "potvrda");
        var unsubscribe = Token(message, "odjava");

        foreach (var (url, token) in new[]
        {
            ("/api/v1/newsletter/confirm", unsubscribe),
            ("/api/v1/newsletter/unsubscribe", confirm),
            ("/api/v1/newsletter/confirm", confirm[..^2] + (confirm[^2] == 'A' ? "BA" : "AA")),
            ("/api/v1/newsletter/confirm", "nije.token"),
        })
        {
            var response = await PostAsync(url, new { token });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("NEWSLETTER_TOKEN_INVALID", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("Pending", await StatusAsync(email));
    }

    [Fact]
    public async Task Sign_up_is_validated_and_the_honeypot_is_silent()
    {
        var invalid = await PostAsync("/api/v1/newsletter/subscriptions", new { email = "nije-email", consent = false });
        var text = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("\"email\"", text);
        Assert.Contains("\"consent\"", text);

        var missing = await PostAsync("/api/v1/newsletter/subscriptions", new { email = "a@example.com" });
        Assert.Contains("\"consent\"", await missing.Content.ReadAsStringAsync());

        var bot = $"bot-{Unique.Token()}@example.com";
        var trapped = await PostAsync("/api/v1/newsletter/subscriptions", new { email = bot, consent = true, website = "http://spam.example" });
        Assert.Equal(HttpStatusCode.Accepted, trapped.StatusCode);
        Assert.Null(await StatusAsync(bot));
    }

    [Fact]
    public async Task Contact_form_is_refused_without_a_store_email_and_otherwise_emails_the_store()
    {
        await SetContactEmailAsync(null);
        var refused = await PostAsync("/api/v1/contact", new { name = "Jovana", email = "jovana@example.com", message = "Pitanje o dostavi" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("CONTACT_NOT_CONFIGURED", problem.GetProperty("code").GetString());
        Assert.Contains("telefonom", problem.GetProperty("detail").GetString());

        var store = $"prodavnica-{Unique.Token()}@example.com";
        await SetContactEmailAsync(store);
        var sender = $"posetilac-{Unique.Token()}@example.com";
        var accepted = await PostAsync("/api/v1/contact", new { name = "Jovana <b>Ilić</b>", email = sender, message = "Da li šaljete\nu Niš?" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await OutboxWorker.For(_postgres, _mailpit).DrainAsync();
        var message = Assert.Single(await _mailpit.MessagesForAsync(store));
        Assert.Equal([sender], message.ReplyTo);
        Assert.Contains("Jovana &lt;b&gt;Ilić&lt;/b&gt;", message.Html);
        Assert.Contains("Da li šaljete", message.Text);

        var messages = await _owner.GetAsync("/api/v1/admin/contact-messages?unreadOnly=true&pageSize=100");
        var stored = messages.GetProperty("messages").Items().Single(m => m.GetProperty("email").GetString() == sender);
        var read = await _owner.JsonAsync(HttpMethod.Post, $"/api/v1/admin/contact-messages/{stored.Id()}/read");
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("readAt").ValueKind);
        Assert.Equal("marketing.owner@example.com", read.GetProperty("readByAdminName").GetString());

        // Honeypot: same 202, nothing stored or sent.
        var bot = $"bot-{Unique.Token()}@example.com";
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/contact", new { name = "Bot", email = bot, message = "spam", website = "x" })).StatusCode);
        Assert.Equal(0L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM contact_messages WHERE email = @e", ("e", bot)));
    }

    [Fact]
    public async Task Contact_and_newsletter_are_rate_limited_per_ip()
    {
        await using var limited = new DevApiFactory
        {
            ConnectionString = _postgres.ConnectionString,
            ExtraSettings = new() { ["RateLimiting:Enabled"] = "true" },
        };
        using var client = limited.CreateClient();
        var ip = $"198.51.100.{Random.Shared.Next(1, 250)}";
        await SetContactEmailAsync($"limit-{Unique.Token()}@example.com");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/newsletter/subscriptions", new { email = $"l{i}-{Unique.Token()}@example.com", consent = true }, ip, client)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/contact", new { name = "A", email = "a@example.com", message = "m" }, ip, client)).StatusCode);
        }

        var newsletter = await PostAsync("/api/v1/newsletter/subscriptions", new { email = "x@example.com", consent = true }, ip, client);
        Assert.Equal(HttpStatusCode.TooManyRequests, newsletter.StatusCode);
        Assert.True(newsletter.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync("/api/v1/contact", new { name = "A", email = "a@example.com", message = "m" }, ip, client)).StatusCode);

        // Another address is not affected.
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/api/v1/contact", new { name = "A", email = "a@example.com", message = "m" }, "203.0.113.77", client)).StatusCode);
    }
}
