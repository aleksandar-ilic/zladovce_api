using System.Security.Cryptography;
using Amazon.Lambda.Core;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Zladovce.Api.Auth;
using Zladovce.Api.Validation;
using Zladovce.Application.Identity;
using Zladovce.Lambda;

namespace Zladovce.IntegrationTests.Infrastructure;

/// <summary>One PostgreSQL 17 container for the whole test run, migrated once by the real migration Lambda handler.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("zladovce_test")
        .WithUsername("zladovce")
        .WithPassword("zladovce_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public MigrationResponse InitialMigration { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        InitialMigration = await RunMigrationsAsync(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database in the container and returns its connection string.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public static Task<MigrationResponse> RunMigrationsAsync(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = connectionString })
            .Build();

        return new MigrationRunner(configuration).HandleAsync(new MigrationRequest(), new FakeLambdaContext());
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    public const string Name = "postgres";
}

internal sealed class FakeLambdaContext : ILambdaContext
{
    public string AwsRequestId => "test-request";
    public IClientContext ClientContext => null!;
    public string FunctionName => "zladovce-migration-runner";
    public string FunctionVersion => "$LATEST";
    public ICognitoIdentity Identity => null!;
    public string InvokedFunctionArn => "arn:aws:lambda:eu-central-1:000000000000:function:zladovce-migration-runner";
    public ILambdaLogger Logger => null!;
    public string LogGroupName => "test";
    public string LogStreamName => "test";
    public int MemoryLimitInMB => 512;
    public TimeSpan RemainingTime => TimeSpan.FromMinutes(5);
}

/// <summary>Request body used to exercise automatic validation; registered only in the test host.</summary>
public sealed record ProbeRequest(string? Email, ProbeCustomer? Customer, int Quantity);

public sealed record ProbeCustomer(string? FirstName);

public sealed class ProbeRequestValidator : AbstractValidator<ProbeRequest>
{
    public ProbeRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress();
        RuleFor(r => r.Quantity).GreaterThan(0);
        RuleFor(r => r.Customer!.FirstName).NotEmpty().When(r => r.Customer is not null);
    }
}

/// <summary>
/// Adds <c>POST /api/v1/_probe</c> to the real application so tests can exercise request validation
/// and error handling through the real pipeline, although Phase 2 has no endpoint with a request body yet.
/// </summary>
internal sealed class ProbeEndpointStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseEndpoints(endpoints =>
        {
            var group = endpoints.MapGroup("/api/v1/_probe").WithRequestValidation();
            group.MapPost("", (ProbeRequest request) => Results.Ok(new { accepted = request.Email }));
            group.MapGet("/boom", string () => throw new InvalidOperationException("sensitive internal detail"));
        });
    };
}

/// <summary>The API hosted in memory against the test database. Subclasses choose environment and authentication.</summary>
public abstract class ApiFactory(string environment) : WebApplicationFactory<Program>
{
    public string? ConnectionString { get; set; }

    /// <summary>Additional settings, applied after those of the subclass (for example media storage).</summary>
    public Dictionary<string, string?> ExtraSettings { get; init; } = [];

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Database:ConnectionString", ConnectionString ?? throw new InvalidOperationException("ConnectionString not set."));
        builder.UseSetting("Database:MigrateOnStartup", "false");
        foreach (var (key, value) in Settings.Concat(ExtraSettings))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddScoped<IValidator<ProbeRequest>, ProbeRequestValidator>();
            services.AddTransient<IStartupFilter, ProbeEndpointStartupFilter>();
            ConfigureTestServices(services);
        });
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }
}

/// <summary>Development environment with the header-based development authentication, as on a developer machine.</summary>
public sealed class DevApiFactory() : ApiFactory("Development")
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Auth:DevBypass"] = "true",
        ["Cors:AllowedOrigins:0"] = "http://localhost:4200",
        ["Cors:AllowedOrigins:1"] = "http://localhost:4201",
    };
}

/// <summary>Any environment with any settings; used to prove the start-up guards.</summary>
public sealed class CustomApiFactory(string environment, Dictionary<string, string?> settings) : ApiFactory(environment)
{
    protected override IReadOnlyDictionary<string, string?> Settings => settings;
}

/// <summary>
/// Production environment with Cognito token validation. The user pool's signing keys are replaced by a
/// local RSA key so that real, signed tokens can be issued without AWS; every other validation rule is the real one.
/// </summary>
public sealed class CognitoApiFactory : ApiFactory
{
    public const string Region = "eu-central-1";
    public const string UserPoolId = "eu-central-1_TESTPOOL";
    public const string ClientId = "test-admin-client";
    public const string Issuer = $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly RsaSecurityKey _foreignKey = new(RSA.Create(2048)) { KeyId = "foreign-key" };

    public CognitoApiFactory()
        : base("Production")
    {
    }

    public StubProfileLookup ProfileLookup { get; } = new();

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Auth:Cognito:Region"] = Region,
        ["Auth:Cognito:UserPoolId"] = UserPoolId,
        ["Auth:Cognito:ClientId"] = ClientId,
        ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
    };

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(AuthSchemes.Cognito, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(_signingKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });

        services.RemoveAll<IAdminProfileLookup>();
        services.AddSingleton<IAdminProfileLookup>(ProfileLookup);
    }

    public string CreateToken(
        string subject,
        string[]? groups = null,
        string tokenUse = "access",
        string clientId = ClientId,
        string issuer = Issuer,
        string? email = null,
        DateTime? expires = null,
        bool signWithForeignKey = false)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["token_use"] = tokenUse,
            ["client_id"] = clientId,
            ["username"] = subject,
            ["scope"] = "aws.cognito.signin.user.admin",
        };

        if (groups is not null)
        {
            claims["cognito:groups"] = groups;
        }

        if (email is not null)
        {
            claims["email"] = email;
        }

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            NotBefore = expiry.AddHours(-1),
            IssuedAt = expiry.AddHours(-1),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(signWithForeignKey ? _foreignKey : _signingKey, SecurityAlgorithms.RsaSha256),
        });
    }
}

public sealed class StubProfileLookup : IAdminProfileLookup
{
    private readonly Dictionary<string, AdminProfile> _profiles = [];

    public List<string> RequestedTokens { get; } = [];

    public void Register(string accessToken, AdminProfile profile) => _profiles[accessToken] = profile;

    public Task<AdminProfile?> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        RequestedTokens.Add(accessToken);
        return Task.FromResult(_profiles.GetValueOrDefault(accessToken));
    }
}
