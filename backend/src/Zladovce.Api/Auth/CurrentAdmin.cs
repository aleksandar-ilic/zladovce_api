using System.Net;
using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Identity;

namespace Zladovce.Api.Auth;

/// <summary>Per-request state of the HTTP host: the resolved administrator and the client address.</summary>
public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    public CurrentAdmin? Admin { get; private set; }

    public IPAddress? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;

    public void SetAdmin(CurrentAdmin admin) => Admin = admin;
}

public static class AdminIdentityFactory
{
    /// <summary>Reads the validated principal. Returns null when it lacks a subject or an administrator role.</summary>
    public static AdminIdentity? FromPrincipal(ClaimsPrincipal principal, string? accessToken)
    {
        var subject = principal.FindFirst(ZladovceClaims.Subject)?.Value;
        var role = AdminRoleMapper.FromName(principal.FindFirst(ZladovceClaims.Role)?.Value);
        if (string.IsNullOrEmpty(subject) || role is null)
        {
            return null;
        }

        return new AdminIdentity(
            subject,
            role.Value,
            NullIfEmpty(principal.FindFirst(ZladovceClaims.Email)?.Value),
            NullIfEmpty(principal.FindFirst(ZladovceClaims.Name)?.Value),
            accessToken);
    }

    public static string? ReadBearerToken(HttpRequest request)
    {
        const string prefix = "Bearer ";
        var header = request.Headers[HeaderNames.Authorization].ToString();
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Runs on every <c>/api/v1/admin</c> endpoint after authorization: maps the token to an active
/// <c>admin_users</c> row (403 otherwise) and publishes it through <see cref="IRequestContext"/>.
/// </summary>
public sealed class CurrentAdminFilter(ICurrentAdminResolver resolver, IRequestContext requestContext) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var identity = AdminIdentityFactory.FromPrincipal(http.User, AdminIdentityFactory.ReadBearerToken(http.Request))
            ?? throw new ForbiddenException();

        var admin = await resolver.ResolveAsync(identity, http.RequestAborted);
        requestContext.SetAdmin(admin);

        return await next(context);
    }
}
