using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentValidation;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Zladovce.Api.Auth;
using Zladovce.Api.Endpoints;
using Zladovce.Api.Errors;
using Zladovce.Api.Logging;
using Zladovce.Api.OpenApi;
using Zladovce.Api.RateLimiting;
using Zladovce.Api.Validation;
using Zladovce.Application;
using Zladovce.Application.Common;
using Zladovce.Infrastructure;
using Zladovce.Infrastructure.Database;
using Zladovce.Infrastructure.Identity;
using Zladovce.Infrastructure.Persistence;

namespace Zladovce.Api;

/// <summary>Composition of the HTTP host: services, middleware pipeline and endpoints.</summary>
public static class ApiHost
{
    public const string CorsPolicy = "ZladovceFrontends";

    public static WebApplicationBuilder AddZladovceApi(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var environment = builder.Environment;

        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var cognito = configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>() ?? new CognitoOptions();
        var corsOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var runningInLambda = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));

        // Fail fast, before anything listens.
        StartupGuards.EnsureAuthConfiguration(environment, auth, cognito, runningInLambda);
        StartupGuards.EnsureCorsOrigins(corsOrigins);

        builder.Host.UseSerilog((context, _, logger) => logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Npgsql", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "zladovce-api")
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        // Kestrel locally; API Gateway HTTP API events when the process runs inside AWS Lambda.
        builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(configuration);

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
        builder.Services.AddScoped<CurrentAdminFilter>();

        // Query-string models of the endpoints (request bodies are validated by the validators of the Application project).
        builder.Services.AddValidatorsFromAssembly(typeof(ApiHost).Assembly, includeInternalTypes: false);

        builder.Services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        builder.Services.Configure<ClientIpOptions>(configuration.GetSection(ClientIpOptions.SectionName));
        builder.Services.AddZladovceAuth(environment, auth, cognito);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            // Numbers are JSON numbers in both directions (money is never a string).
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            // Serbian letters are written as-is; responses are JSON, never HTML.
            options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            // Members declared `required` must be sent; a missing one is a 400 VALIDATION_FAILED with its field path.
            options.SerializerOptions.TypeInfoResolver = (options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                .WithAddedModifier(RequiredMembers.Modifier);
        });

        // Binding failures (malformed JSON and the like) must reach the exception middleware in every environment.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        // Serbian (Latin) default validation messages.
        ValidatorOptions.Global.LanguageManager.Culture = CultureInfo.GetCultureInfo("sr-Latn");

        builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            var headers = new List<string> { "Authorization", "Content-Type", "Accept", CorrelationIdMiddleware.HeaderName, "Idempotency-Key" };
            if (environment.IsDevelopment() && auth.DevBypass)
            {
                headers.Add(DevAuthenticationHandler.EmailHeader);
                headers.Add(DevAuthenticationHandler.RoleHeader);
            }

            policy.WithOrigins(corsOrigins)
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .WithHeaders([.. headers])
                .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }));

        builder.Services.AddZladovceOpenApi();

        return builder;
    }

    public static WebApplication UseZladovceApi(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging(options =>
        {
            // Path only: query strings can carry search terms or codes and are never logged.
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error
                : context.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
                : LogEventLevel.Information;
        });
        app.UseZladovceErrorHandling();

        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<PostgresRateLimitingMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
            app.UseSwaggerUI(options =>
            {
                options.RoutePrefix = "swagger";
                options.SwaggerEndpoint($"/openapi/{OpenApiSetup.DocumentName}.json", "Zladovce API v1");
            });
        }

        app.MapZladovceEndpoints();
        return app;
    }

    /// <summary>
    /// Development convenience: applies migrations at start-up when <c>Database:MigrateOnStartup</c> is true.
    /// In every other environment migrations are applied only by the migration Lambda of the deployment pipeline.
    /// </summary>
    public static async Task MigrateDatabaseIfRequestedAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!ShouldMigrateOnStartup(app.Environment, options))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
    }

    public static bool ShouldMigrateOnStartup(IHostEnvironment environment, DatabaseOptions options) =>
        environment.IsDevelopment() && options.MigrateOnStartup;
}
