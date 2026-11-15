using Microsoft.AspNetCore.Http.HttpResults;
using Zladovce.Api.Auth;
using Zladovce.Application.Audit;
using Zladovce.Application.Identity;

namespace Zladovce.Api.Endpoints;

/// <summary>Administrator accounts and the audit log (Owner only).</summary>
public static class AdministrationEndpoints
{
    public static void MapAdministrationEndpoints(this IEndpointRouteBuilder admin)
    {
        var users = admin.MapGroup("/users").WithTags("Admin – users").RequireAuthorization(AuthPolicies.OwnerOnly);

        users.MapGet("", async (IAdminUserService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListAdminUsers")
            .WithSummary("Administrators (active first). `isCurrentUser` marks the signed-in Owner (Owner).")
            .WithDescription("Errors: 403 FORBIDDEN (Staff).");

        users.MapPost("", async Task<Created<AdminUserResponse>> (InviteAdminRequest request, IAdminUserService service, CancellationToken ct) =>
            {
                var created = await service.InviteAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/users/{created.Id}", created);
            })
            .WithName("InviteAdminUser")
            .WithSummary("Invite an administrator: creates the Cognito user (Cognito emails a temporary password) in the role's group.")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED (`email`, `fullName`, `role`); 403 FORBIDDEN; 409 ADMIN_EMAIL_TAKEN (`errors.email`); " +
                "503 IDENTITY_PROVIDER_ERROR (Cognito refused; nothing stored).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status503ServiceUnavailable);

        users.MapPut("/{id:guid}", async (Guid id, UpdateAdminRequest request, IAdminUserService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateAdminUser")
            .WithSummary("Change role and active state (Owner). Cognito groups and enabled state follow.")
            .WithDescription(
                "A role change takes effect in the API with the user's next token (after sign-in or token refresh, at most the access " +
                "token lifetime); deactivation blocks the user in the API at once. Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; " +
                "409 CANNOT_CHANGE_SELF (own role or own deactivation), LAST_OWNER (the last active Owner cannot be demoted or " +
                "deactivated); 503 IDENTITY_PROVIDER_ERROR.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status503ServiceUnavailable);

        var audit = admin.MapGroup("/audit-logs").WithTags("Admin – audit log").RequireAuthorization(AuthPolicies.OwnerOnly);

        audit.MapGet("", async ([AsParameters] AuditLogListQuery query, IAuditLogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(), ct)))
            .WithName("ListAuditLogs")
            .WithSummary("Audit entries, newest first, with a Serbian `summary` (Owner).")
            .WithDescription("Filters `adminUserId`, `entityType`, `entityId`, `action`, `from`/`to` (Serbian dates, inclusive), paging. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        audit.MapGet("/filters", async (IAuditLogService service, CancellationToken ct) => TypedResults.Ok(await service.FiltersAsync(ct)))
            .WithName("GetAuditLogFilters")
            .WithSummary("Entity types (with Serbian labels) and actions that occur in the log, for the filter drop-downs (Owner).");
    }
}
