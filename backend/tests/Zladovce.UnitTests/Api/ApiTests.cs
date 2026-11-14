using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Zladovce.Api;
using Zladovce.Api.Auth;
using Zladovce.Api.Errors;
using Zladovce.Api.Logging;
using Zladovce.Api.Validation;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Common;
using Zladovce.Domain.Identity;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Identity;

namespace Zladovce.UnitTests.Api;

internal sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Zladovce.Api";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public class ProblemMapperTests
{
    [Fact]
    public void Validation_exception_maps_to_400_with_the_errors_map()
    {
        var errors = new Dictionary<string, string[]> { ["email"] = ["Neispravna adresa."] };

        var problem = ProblemMapper.FromException(new RequestValidationException(errors));

        Assert.Equal(400, problem.Status);
        Assert.Equal("VALIDATION_FAILED", problem.Code);
        Assert.Equal("https://zladovce.com/errors/validation-failed", problem.Type);
        Assert.Equal("Neispravni podaci", problem.Title);
        Assert.Same(errors, problem.Errors);
    }

    [Fact]
    public void Not_found_maps_to_404_and_keeps_a_custom_code()
    {
        var generic = ProblemMapper.FromException(new NotFoundException());
        var custom = ProblemMapper.FromException(new NotFoundException("Kod nije ispravan.", "TRACKING_NOT_FOUND"));

        Assert.Equal((404, "NOT_FOUND"), (generic.Status, generic.Code));
        Assert.Null(generic.Errors);
        Assert.Equal((404, "TRACKING_NOT_FOUND"), (custom.Status, custom.Code));
        Assert.Equal("https://zladovce.com/errors/tracking-not-found", custom.Type);
        Assert.Equal("Kod nije ispravan.", custom.Detail);
    }

    [Fact]
    public void Conflict_maps_to_409()
    {
        var problem = ProblemMapper.FromException(new ConflictException("Nekih proizvoda više nema.", "INSUFFICIENT_STOCK", "Nedovoljno na stanju"));

        Assert.Equal(409, problem.Status);
        Assert.Equal("INSUFFICIENT_STOCK", problem.Code);
        Assert.Equal("Nedovoljno na stanju", problem.Title);
    }

    [Fact]
    public void Forbidden_maps_to_403()
    {
        var problem = ProblemMapper.FromException(new ForbiddenException());

        Assert.Equal((403, "FORBIDDEN"), (problem.Status, problem.Code));
    }

    [Fact]
    public void Concurrency_conflict_maps_to_409_concurrent_edit()
    {
        var problem = ProblemMapper.FromException(new DbUpdateConcurrencyException("x"));

        Assert.Equal((409, "CONCURRENT_EDIT"), (problem.Status, problem.Code));
    }

    [Fact]
    public void Domain_exception_maps_to_400_without_leaking_the_developer_message()
    {
        var problem = ProblemMapper.FromException(new DomainException("Shipping price must be zero or greater."));

        Assert.Equal((400, "VALIDATION_FAILED"), (problem.Status, problem.Code));
        Assert.DoesNotContain("Shipping", problem.Detail);
    }

    [Theory]
    [InlineData(400, "BAD_REQUEST")]
    [InlineData(413, "PAYLOAD_TOO_LARGE")]
    [InlineData(415, "UNSUPPORTED_MEDIA_TYPE")]
    public void Bad_http_request_keeps_its_client_error_status(int status, string code)
    {
        var problem = ProblemMapper.FromException(new BadHttpRequestException("Failed to read parameter", status));

        Assert.Equal((status, code), (problem.Status, problem.Code));
        Assert.DoesNotContain("parameter", problem.Detail);
    }

    [Fact]
    public void Unexpected_exception_maps_to_500_without_leaking_its_message()
    {
        var exception = new InvalidOperationException("connection string Password=secret");

        var problem = ProblemMapper.FromException(exception);

        Assert.Equal((500, "INTERNAL_ERROR"), (problem.Status, problem.Code));
        Assert.DoesNotContain("secret", problem.Detail + problem.Title);
        Assert.False(ProblemMapper.IsExpected(exception));
    }

    [Theory]
    [InlineData(401, "UNAUTHORIZED")]
    [InlineData(403, "FORBIDDEN")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(405, "METHOD_NOT_ALLOWED")]
    [InlineData(429, "RATE_LIMITED")]
    [InlineData(503, "SERVICE_UNAVAILABLE")]
    [InlineData(418, "BAD_REQUEST")]
    [InlineData(502, "INTERNAL_ERROR")]
    public void Bare_status_codes_get_a_code_and_serbian_text(int status, string code)
    {
        var problem = ProblemMapper.FromStatusCode(status);

        Assert.Equal((status, code), (problem.Status, problem.Code));
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.StartsWith("https://zladovce.com/errors/", problem.Type);
    }
}

public class StartupGuardsTests
{
    private static readonly CognitoOptions Cognito = new() { Region = "eu-central-1", UserPoolId = "eu-central-1_abc", ClientId = "client" };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("development-like")]
    public void Dev_bypass_outside_development_stops_the_application(string environment)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupGuards.EnsureAuthConfiguration(new FakeEnvironment(environment), new AuthOptions { DevBypass = true }, Cognito, runningInLambda: false));

        Assert.Contains("Auth:DevBypass", exception.Message);
    }

    [Fact]
    public void Dev_bypass_inside_lambda_stops_the_application_even_in_development()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupGuards.EnsureAuthConfiguration(new FakeEnvironment("Development"), new AuthOptions { DevBypass = true }, Cognito, runningInLambda: true));
    }

    [Fact]
    public void Dev_bypass_is_accepted_in_development_without_cognito()
    {
        StartupGuards.EnsureAuthConfiguration(new FakeEnvironment("Development"), new AuthOptions { DevBypass = true }, new CognitoOptions(), runningInLambda: false);
    }

    [Fact]
    public void Without_dev_bypass_cognito_must_be_configured()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupGuards.EnsureAuthConfiguration(new FakeEnvironment("Production"), new AuthOptions(), new CognitoOptions { Region = "eu-central-1" }, runningInLambda: true));

        StartupGuards.EnsureAuthConfiguration(new FakeEnvironment("Production"), new AuthOptions(), Cognito, runningInLambda: true);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.zladovce.com")]
    [InlineData("https://zladovce.com/")]
    [InlineData("https://zladovce.com/shop")]
    [InlineData("zladovce.com")]
    [InlineData("ftp://zladovce.com")]
    public void Cors_origins_must_be_exact(string origin) =>
        Assert.Throws<InvalidOperationException>(() => StartupGuards.EnsureCorsOrigins([origin]));

    [Fact]
    public void Exact_cors_origins_are_accepted() =>
        StartupGuards.EnsureCorsOrigins(["http://localhost:4200", "https://zladovce.com", "https://admin.zladovce.com"]);

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    public void Migrations_run_at_startup_only_in_development_and_only_when_requested(string environment, bool flag, bool expected) =>
        Assert.Equal(expected, ApiHost.ShouldMigrateOnStartup(new FakeEnvironment(environment), new DatabaseOptions { MigrateOnStartup = flag }));
}

public class CognitoTokenRulesTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer", "sub", ZladovceClaims.Role));

    [Fact]
    public void Access_token_of_the_expected_client_is_accepted() =>
        Assert.Null(CognitoTokenRules.FindViolation(
            Principal(("sub", "abc"), ("token_use", "access"), ("client_id", "client-1")), "client-1"));

    [Fact]
    public void Id_tokens_are_rejected() =>
        Assert.NotNull(CognitoTokenRules.FindViolation(
            Principal(("sub", "abc"), ("token_use", "id"), ("client_id", "client-1")), "client-1"));

    [Fact]
    public void Tokens_of_another_client_are_rejected() =>
        Assert.NotNull(CognitoTokenRules.FindViolation(
            Principal(("sub", "abc"), ("token_use", "access"), ("client_id", "client-2")), "client-1"));

    [Fact]
    public void Tokens_without_token_use_client_or_subject_are_rejected()
    {
        Assert.NotNull(CognitoTokenRules.FindViolation(Principal(("sub", "abc"), ("client_id", "client-1")), "client-1"));
        Assert.NotNull(CognitoTokenRules.FindViolation(Principal(("sub", "abc"), ("token_use", "access")), "client-1"));
        Assert.NotNull(CognitoTokenRules.FindViolation(Principal(("token_use", "access"), ("client_id", "client-1")), "client-1"));
    }

    [Fact]
    public void Role_claim_is_derived_from_cognito_groups()
    {
        var principal = Principal(("sub", "abc"), ("cognito:groups", "Staff"), ("cognito:groups", "Owner"));

        CognitoTokenRules.ApplyRole((ClaimsIdentity)principal.Identity!);

        Assert.Equal("Owner", principal.FindFirst(ZladovceClaims.Role)?.Value);
        var identity = AdminIdentityFactory.FromPrincipal(principal, "token");
        Assert.NotNull(identity);
        Assert.Equal(AdminRole.Owner, identity.Role);
        Assert.Equal("abc", identity.Subject);
        Assert.Null(identity.Email);
        Assert.Equal("token", identity.AccessToken);
    }

    [Fact]
    public void Without_an_administrator_group_there_is_no_role_and_no_admin_identity()
    {
        var principal = Principal(("sub", "abc"), ("cognito:groups", "Customers"));

        CognitoTokenRules.ApplyRole((ClaimsIdentity)principal.Identity!);

        Assert.Null(principal.FindFirst(ZladovceClaims.Role));
        Assert.Null(AdminIdentityFactory.FromPrincipal(principal, null));
    }
}

public class RequestPipelineHelpersTests
{
    [Theory]
    [InlineData("Email", "email")]
    [InlineData("Customer.FirstName", "customer.firstName")]
    [InlineData("Items[0].Quantity", "items[0].quantity")]
    [InlineData("alreadyCamel", "alreadyCamel")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Validation_keys_are_camel_case_paths(string? path, string expected) =>
        Assert.Equal(expected, RequestValidation.ToCamelCasePath(path));

    [Theory]
    [InlineData("abc-123")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("req_1.2")]
    public void Well_formed_correlation_ids_are_accepted(string value) =>
        Assert.Equal(value, CorrelationIdMiddleware.Accept(value));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Malformed_correlation_ids_are_replaced(string? value) =>
        Assert.Null(CorrelationIdMiddleware.Accept(value));
}
