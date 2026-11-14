using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Zladovce.Api.OpenApi;

public static class OpenApiSetup
{
    public const string DocumentName = "v1";
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddZladovceOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Zladovce API",
                    Version = "v1",
                    Description =
                        "API of the Zladovce shop. Errors are RFC 9457 Problem Details with a stable `code` and Serbian text. " +
                        "Endpoints under /api/v1/admin require an Amazon Cognito access token.",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Amazon Cognito access token of an administrator (group Owner or Staff).",
                };

                // The document is the same in every environment; hosts are not part of the contract.
                document.Servers = [];
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
                if (requiresAuth)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });
        });
}
