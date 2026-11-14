using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Zladovce.Application.Identity;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

/// <summary>
/// The production authentication path: signed JWTs shaped like Amazon Cognito access tokens,
/// validated by the real JWT bearer configuration (only the signing key source is local).
/// </summary>
[Collection(PostgresCollection.Name)]
public class CognitoApiTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly CognitoApiFactory _factory;
    private readonly HttpClient _client;

    public CognitoApiTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new CognitoApiFactory { ConnectionString = postgres.ConnectionString };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Task<HttpResponseMessage> GetMeAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Valid_access_token_provisions_the_admin_from_the_cognito_profile()
    {
        var subject = Guid.NewGuid().ToString();
        var token = _factory.CreateToken(subject, groups: ["Owner"]);
        _factory.ProfileLookup.Register(token, new AdminProfile("vlasnik@example.com", "Vlasnik Prodavnice"));

        var response = await GetMeAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vlasnik@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Vlasnik Prodavnice", body.GetProperty("fullName").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal(subject, await _postgres.ScalarAsync<string>(
            "SELECT cognito_sub FROM admin_users WHERE id = @id", ("id", body.GetProperty("id").GetGuid())));

        // The profile is read once, at provisioning; later requests do not call Cognito again.
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(token)).StatusCode);
        Assert.Single(_factory.ProfileLookup.RequestedTokens, token);
    }

    [Fact]
    public async Task Email_claim_in_the_token_is_used_without_a_profile_lookup()
    {
        var token = _factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Staff"], email: "urednik@example.com");

        var body = await (await GetMeAsync(token)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("urednik@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Staff", body.GetProperty("role").GetString());
        Assert.DoesNotContain(token, _factory.ProfileLookup.RequestedTokens);
    }

    [Fact]
    public async Task Admin_without_a_resolvable_email_is_403_and_not_stored()
    {
        var subject = Guid.NewGuid().ToString();
        var token = _factory.CreateToken(subject, groups: ["Staff"]);

        var response = await GetMeAsync(token);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "ADMIN_NOT_PROVISIONED");
        Assert.Equal(0L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM admin_users WHERE cognito_sub = @s", ("s", subject)));
    }

    [Fact]
    public async Task A_second_identity_cannot_take_over_an_existing_email()
    {
        var first = _factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Owner"], email: "jedinstven@example.com");
        var second = _factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Owner"], email: "JEDINSTVEN@example.com");
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(first)).StatusCode);

        var response = await GetMeAsync(second);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "ADMIN_NOT_PROVISIONED");
    }

    [Fact]
    public async Task Missing_token_is_401()
    {
        var response = await GetMeAsync(null);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Id_token_is_401()
    {
        var token = _factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Owner"], tokenUse: "id", email: "id-token@example.com");

        await ResponseAssertions.AssertProblemAsync(await GetMeAsync(token), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Token_of_another_app_client_is_401()
    {
        var token = _factory.CreateToken(Guid.NewGuid().ToString(), groups: ["Owner"], clientId: "other-client", email: "other-client@example.com");

        await ResponseAssertions.AssertProblemAsync(await GetMeAsync(token), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Token_of_another_issuer_is_401()
    {
        var token = _factory.CreateToken(
            Guid.NewGuid().ToString(),
            groups: ["Owner"],
            issuer: "https://cognito-idp.eu-central-1.amazonaws.com/eu-central-1_OTHERPOOL",
            email: "other-pool@example.com");

        await ResponseAssertions.AssertProblemAsync(await GetMeAsync(token), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = _factory.CreateToken(
            Guid.NewGuid().ToString(), groups: ["Owner"], email: "expired@example.com", expires: DateTime.UtcNow.AddMinutes(-10));

        await ResponseAssertions.AssertProblemAsync(await GetMeAsync(token), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Token_signed_with_an_unknown_key_is_401()
    {
        var token = _factory.CreateToken(
            Guid.NewGuid().ToString(), groups: ["Owner"], email: "forged@example.com", signWithForeignKey: true);

        await ResponseAssertions.AssertProblemAsync(await GetMeAsync(token), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Customers")]
    public async Task Valid_token_without_an_administrator_group_is_403(string? group)
    {
        var subject = Guid.NewGuid().ToString();
        var token = _factory.CreateToken(subject, groups: group is null ? null : [group], email: $"{subject}@example.com");

        var response = await GetMeAsync(token);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM admin_users WHERE cognito_sub = @s", ("s", subject)));
    }

    [Fact]
    public async Task Development_headers_are_ignored_outside_development()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/me").AsDevAdmin("attacker@example.com", "Owner");

        var response = await _client.SendAsync(request);

        await ResponseAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Equal(0L, await _postgres.ScalarAsync<long>("SELECT count(*) FROM admin_users WHERE email = 'attacker@example.com'"));
    }

    [Fact]
    public async Task OpenApi_and_swagger_are_not_exposed_outside_development()
    {
        await ResponseAssertions.AssertProblemAsync(await _client.GetAsync("/openapi/v1.json"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ResponseAssertions.AssertProblemAsync(await _client.GetAsync("/swagger/index.html"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Public_endpoints_need_no_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/store")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
    }
}
