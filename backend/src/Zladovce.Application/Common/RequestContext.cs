using System.Net;
using Zladovce.Domain.Identity;

namespace Zladovce.Application.Common;

/// <summary>The administrator performing the current request, after the token was matched to an active <c>admin_users</c> row.</summary>
public sealed record CurrentAdmin(Guid Id, string Email, string FullName, AdminRole Role);

/// <summary>Per-request information supplied by the host (HTTP API). Empty for background work.</summary>
public interface IRequestContext
{
    /// <summary>Set once the current administrator has been resolved; null for public requests.</summary>
    CurrentAdmin? Admin { get; }

    /// <summary>Client address, stored in audit entries only. Never written to application logs.</summary>
    IPAddress? IpAddress { get; }

    void SetAdmin(CurrentAdmin admin);
}
