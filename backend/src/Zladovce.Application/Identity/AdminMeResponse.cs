using Zladovce.Application.Common;
using Zladovce.Domain.Identity;

namespace Zladovce.Application.Identity;

/// <summary>Response of <c>GET /api/v1/admin/me</c>.</summary>
public sealed record AdminMeResponse(Guid Id, string Email, string FullName, AdminRole Role)
{
    public static AdminMeResponse From(CurrentAdmin admin) => new(admin.Id, admin.Email, admin.FullName, admin.Role);
}
