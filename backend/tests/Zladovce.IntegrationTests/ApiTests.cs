using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

internal static class ResponseAssertions
{
    /// <summary>Asserts the RFC 9457 shape used by every error response and returns the body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal("https://zladovce.com/errors/" + code.ToLowerInvariant().Replace('_', '-'), body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        return body;
    }

    public static HttpRequestMessage AsDevAdmin(this HttpRequestMessage request, string email, string role)
    {
        request.Headers.Add("X-Dev-User-Email", email);
        request.Headers.Add("X-Dev-User-Role", role);
        return request;
    }
}

[Collection(PostgresCollection.Name)]
public class DevApiTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;

    public DevApiTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Task<HttpResponseMessage> GetMeAsync(string email, string role) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me").AsDevAdmin(email, role));

    [Fact]
    public void Application_uses_the_test_database_not_the_developer_database()
    {
        var dataSource = _factory.Services.GetRequiredService<NpgsqlDataSource>();
        var expected = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString);
        var actual = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal("zladovce_test", actual.Database);
    }

    [Fact]
    public async Task Health_is_alive()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ready_reports_ready_when_the_database_answers()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ready_reports_503_when_the_database_is_unreachable_while_liveness_stays_ok()
    {
        using var factory = new DevApiFactory { ConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2" };
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health");

        await ResponseAssertions.AssertProblemAsync(ready, HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Store_returns_the_seeded_settings()
    {
        var response = await _client.GetAsync("/api/v1/store");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.Equal("Zladovce", body.GetProperty("storeName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("logoKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("contact").GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("contact").GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("contact").GetProperty("address").ValueKind);
        Assert.Equal(0, body.GetProperty("social").GetProperty("otherLinks").GetArrayLength());
        Assert.True(body.GetProperty("shipping").GetProperty("enabled").GetBoolean());
        Assert.Equal(0m, body.GetProperty("shipping").GetProperty("price").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shipping").GetProperty("freeShippingThreshold").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("seo").GetProperty("defaultTitle").ValueKind);

        // Money is a JSON number with two decimals.
        Assert.Contains("\"price\":0.00", text);
    }

    [Fact]
    public async Task Admin_me_without_credentials_is_401()
    {
        var response = await _client.GetAsync("/api/v1/admin/me");

        var body = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Equal("Niste prijavljeni", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Admin_me_with_dev_headers_provisions_the_admin_and_writes_an_audit_entry()
    {
        const string email = "owner.provision@example.com";

        var response = await GetMeAsync(email, "Owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(7, id.Version);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal(email, body.GetProperty("fullName").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal(4, body.EnumerateObject().Count());

        Assert.Equal("dev:" + email, await _postgres.ScalarAsync<string>("SELECT cognito_sub FROM admin_users WHERE id = @id", ("id", id)));
        Assert.True(await _postgres.ScalarAsync<bool>("SELECT last_login_at IS NOT NULL AND is_active FROM admin_users WHERE id = @id", ("id", id)));

        var audit = await _postgres.ScalarAsync<string>(
            "SELECT action || '|' || entity_type || '|' || entity_id || '|' || (changes->>'role') FROM audit_logs WHERE admin_user_id = @id",
            ("id", id));
        Assert.Equal($"Auth.Login|AdminUser|{id}|Owner", audit);
    }

    [Fact]
    public async Task Admin_me_is_stable_across_requests_and_provisions_only_once()
    {
        const string email = "staff.repeat@example.com";

        var first = await (await GetMeAsync(email, "Staff")).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await GetMeAsync(email.ToUpperInvariant(), "Staff")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Staff", second.GetProperty("role").GetString());
        Assert.Equal(1L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM admin_users WHERE email = @e", ("e", email)));
        Assert.Equal(
            1L,
            await _postgres.ScalarAsync<long>(
                "SELECT count(*) FROM audit_logs WHERE admin_user_id = @id", ("id", first.GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task Admin_me_mirrors_a_role_change_from_the_identity_provider()
    {
        const string email = "promoted@example.com";
        await GetMeAsync(email, "Staff");

        var body = await (await GetMeAsync(email, "Owner")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal("Owner", await _postgres.ScalarAsync<string>("SELECT role FROM admin_users WHERE email = @e", ("e", email)));
    }

    [Fact]
    public async Task Admin_me_for_an_inactive_admin_is_403()
    {
        const string email = "inactive@example.com";
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(email, "Staff")).StatusCode);
        Assert.Equal(1, await _postgres.ExecuteAsync("UPDATE admin_users SET is_active = false WHERE email = @e", ("e", email)));

        var response = await GetMeAsync(email, "Staff");

        var body = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "ADMIN_INACTIVE");
        Assert.Equal("Nalog nije aktivan", body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("someone@example.com", "Admin")]
    [InlineData("someone@example.com", "owner")]
    [InlineData("someone@example.com", "")]
    [InlineData("not-an-email", "Owner")]
    public async Task Invalid_dev_headers_are_401(string email, string role)
    {
        var response = await GetMeAsync(email, role);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Unknown_route_is_a_404_problem()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist");

        var body = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal("Nije pronađeno", body.GetProperty("title").GetString());
        Assert.False(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Wrong_method_is_a_405_problem()
    {
        var response = await _client.PostAsync("/api/v1/store", null);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
    }

    [Fact]
    public async Task Invalid_request_body_is_a_400_problem_with_field_errors()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/_probe",
            new { email = "nije-email", quantity = 0, customer = new { firstName = "" } });

        var body = await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("Neispravni podaci", body.GetProperty("title").GetString());

        var errors = body.GetProperty("errors");
        Assert.Equal(["customer.firstName", "email", "quantity"], errors.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.All(errors.EnumerateObject(), field =>
        {
            Assert.Equal(JsonValueKind.Array, field.Value.ValueKind);
            Assert.All(field.Value.EnumerateArray(), message => Assert.False(string.IsNullOrWhiteSpace(message.GetString())));
        });
    }

    [Fact]
    public async Task Valid_request_body_passes_validation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/_probe", new { email = "kupac@example.com", quantity = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        var response = await _client.PostAsync(
            "/api/v1/_probe", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Unhandled_exception_is_a_500_problem_without_internal_details()
    {
        var response = await _client.GetAsync("/api/v1/_probe/boom");

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.DoesNotContain("sensitive", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correlation_id_is_echoed_or_generated()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", "test-correlation-42");

        var echoed = await _client.SendAsync(request);
        var generated = await _client.GetAsync("/api/v1/does-not-exist");

        Assert.Equal("test-correlation-42", echoed.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Matches("^[0-9a-f]{32}$", generated.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Theory]
    [InlineData("http://localhost:4200", true)]
    [InlineData("http://localhost:4201", true)]
    [InlineData("http://localhost:4300", false)]
    [InlineData("https://evil.example", false)]
    public async Task Cors_allows_only_the_configured_origins(string origin, bool allowed)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/admin/me");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,x-dev-user-email,x-dev-user-role");

        var response = await _client.SendAsync(request);

        Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Single() == origin);
    }

    [Fact]
    public async Task OpenApi_document_is_served_in_development_and_describes_the_endpoints()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.True(paths.TryGetProperty("/health/ready", out _));
        Assert.True(paths.TryGetProperty("/api/v1/store", out _));
        Assert.True(paths.GetProperty("/api/v1/admin/me").GetProperty("get").TryGetProperty("security", out _));

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/swagger/index.html")).StatusCode);
    }
}

[Collection(PostgresCollection.Name)]
public class StartupGuardTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Application_refuses_to_start_with_dev_bypass_outside_development(string environment)
    {
        using var factory = new CustomApiFactory(environment, new Dictionary<string, string?>
        {
            ["Auth:DevBypass"] = "true",
            ["Auth:Cognito:Region"] = CognitoApiFactory.Region,
            ["Auth:Cognito:UserPoolId"] = CognitoApiFactory.UserPoolId,
            ["Auth:Cognito:ClientId"] = CognitoApiFactory.ClientId,
        })
        { ConnectionString = postgres.ConnectionString };

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Auth:DevBypass", exception.Message);
    }

    [Fact]
    public void Application_refuses_to_start_without_any_authentication_configuration()
    {
        using var factory = new CustomApiFactory("Production", [])
        { ConnectionString = postgres.ConnectionString };

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Auth:Cognito", exception.Message);
    }

    [Fact]
    public void Application_refuses_a_wildcard_cors_origin()
    {
        using var factory = new CustomApiFactory("Development", new Dictionary<string, string?>
        {
            ["Auth:DevBypass"] = "true",
            ["Cors:AllowedOrigins:0"] = "*",
        })
        { ConnectionString = postgres.ConnectionString };

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
