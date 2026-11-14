using Microsoft.AspNetCore.Http.HttpResults;
using Zladovce.Api.Auth;
using Zladovce.Api.Errors;
using Zladovce.Api.Validation;
using Zladovce.Application.Common;
using Zladovce.Application.Identity;
using Zladovce.Application.StoreSettings;
using Zladovce.Infrastructure.Persistence;

namespace Zladovce.Api.Endpoints;

/// <summary>Response of the health endpoints.</summary>
public sealed record HealthResponse(string Status);

public static class EndpointMapping
{
    public const string ApiBasePath = "/api/v1";

    public static IEndpointRouteBuilder MapZladovceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();

        var api = app.MapGroup(ApiBasePath).WithRequestValidation();
        api.MapStoreEndpoints();
        api.MapPublicCatalogEndpoints();
        api.MapStorefrontEndpoints();
        api.MapCheckoutEndpoints();

        var admin = api.MapGroup("/admin")
            .RequireAuthorization(AuthPolicies.AdminAny)
            .AddEndpointFilter<CurrentAdminFilter>()
            .WithMetadata(
                new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(ApiProblem), [ApiProblem.ContentType]),
                new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(ApiProblem), [ApiProblem.ContentType]));
        admin.MapAdminSessionEndpoints();
        admin.MapAdminCatalogEndpoints();
        admin.MapAdminInventoryEndpoints();
        admin.MapAdminOrderEndpoints();
        admin.MapAdminShippingEndpoints();

        return app;
    }

    private static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var health = app.MapGroup("/health").WithTags("Health").AllowAnonymous();

        health.MapGet("", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("GetHealth")
            .WithSummary("Liveness: the process is running. Does not touch the database.");

        health.MapGet("/ready", async Task<Ok<HealthResponse>> (
                ZladovceDbContext db, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            {
                try
                {
                    if (await db.Database.CanConnectAsync(cancellationToken))
                    {
                        return TypedResults.Ok(new HealthResponse("ready"));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger("Zladovce.Api.Health").LogWarning(
                        "Readiness check failed: {ExceptionType}", exception.GetType().Name);
                }

                throw new ServiceNotReadyException();
            })
            .WithName("GetReadiness")
            .WithSummary("Readiness: the database is reachable.")
            .Produces<ApiProblem>(StatusCodes.Status503ServiceUnavailable, ApiProblem.ContentType);
    }

    private static void MapStoreEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/store", async (IStoreSettingsService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetPublicAsync(cancellationToken)))
            .WithName("GetStore")
            .WithTags("Store")
            .WithSummary("Public store settings: name, logo, contact, social links, delivery and shipping, SEO defaults.")
            .AllowAnonymous();
    }

    private static void MapAdminSessionEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/me", (IRequestContext requestContext) =>
                TypedResults.Ok(AdminMeResponse.From(requestContext.Admin!)))
            .WithName("GetAdminMe")
            .WithTags("Admin session")
            .WithSummary("The signed-in administrator and role.");
    }
}
