using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Identity;

namespace Zladovce.Application.Identity;

/// <summary>What the API knows about the caller after the token (or development headers) was validated.</summary>
/// <param name="Subject">Stable identity-provider subject (Cognito <c>sub</c>).</param>
/// <param name="Role">Role derived from the token groups.</param>
/// <param name="Email">Email when the token carries it; Cognito access tokens normally do not.</param>
/// <param name="FullName">Display name when the token carries it.</param>
/// <param name="AccessToken">The raw access token, used only to read the user's own profile on first sign-in.</param>
public sealed record AdminIdentity(string Subject, AdminRole Role, string? Email, string? FullName, string? AccessToken);

/// <summary>Email and name of the signed-in user as stored by the identity provider.</summary>
public sealed record AdminProfile(string Email, string? FullName);

/// <summary>Reads the signed-in user's own profile from the identity provider (Cognito <c>GetUser</c>).</summary>
public interface IAdminProfileLookup
{
    Task<AdminProfile?> GetProfileAsync(string accessToken, CancellationToken cancellationToken);
}

public interface ICurrentAdminResolver
{
    /// <summary>
    /// Maps a validated identity to its <c>admin_users</c> row, creating the row on the first valid sign-in.
    /// Throws <see cref="ForbiddenException"/> when the administrator is inactive or cannot be provisioned.
    /// </summary>
    Task<CurrentAdmin> ResolveAsync(AdminIdentity identity, CancellationToken cancellationToken);
}

public sealed class CurrentAdminResolver(
    IZladovceDbContext db,
    IAdminProfileLookup profileLookup,
    IAuditLogger audit,
    TimeProvider timeProvider,
    ILogger<CurrentAdminResolver> logger) : ICurrentAdminResolver
{
    public async Task<CurrentAdmin> ResolveAsync(AdminIdentity identity, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var admin = await FindBySubjectAsync(identity.Subject, cancellationToken)
            ?? await ProvisionAsync(identity, now, cancellationToken);

        if (!admin.IsActive)
        {
            throw new ForbiddenException(ErrorTexts.AdminInactiveDetail, ErrorCodes.AdminInactive, ErrorTexts.AdminInactiveTitle);
        }

        // The identity provider is the source of truth for the role; the row mirrors it.
        var roleChanged = admin.SyncRole(identity.Role, now);
        var loginRecorded = admin.TouchLogin(now);
        if (roleChanged || loginRecorded)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new CurrentAdmin(admin.Id, admin.Email, admin.FullName, admin.Role);
    }

    private Task<AdminUser?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        db.AdminUsers.SingleOrDefaultAsync(a => a.CognitoSub == subject, cancellationToken);

    private async Task<AdminUser> ProvisionAsync(AdminIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var email = identity.Email;
        var fullName = identity.FullName;

        if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(identity.AccessToken))
        {
            var profile = await profileLookup.GetProfileAsync(identity.AccessToken, cancellationToken);
            email = profile?.Email;
            fullName ??= profile?.FullName;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Administrator could not be provisioned: the identity provider returned no email address.");
            throw NotProvisioned();
        }

        if (await db.AdminUsers.AnyAsync(a => a.Email == email, cancellationToken))
        {
            // Another identity already owns this email. Never re-link silently.
            logger.LogWarning("Administrator could not be provisioned: the email address belongs to a different identity.");
            throw NotProvisioned();
        }

        var admin = AdminUser.Create(identity.Subject, email, fullName, identity.Role, now);
        db.AdminUsers.Add(admin);
        audit.Record(
            AuditActions.AuthLogin,
            entityType: nameof(AdminUser),
            entityId: admin.Id.ToString(),
            changes: new { provisioned = true, role = AdminRoleMapper.ToName(admin.Role) },
            adminUserId: admin.Id);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Administrator {AdminUserId} provisioned with role {Role}.", admin.Id, admin.Role);
            return admin;
        }
        catch (DbUpdateException)
        {
            // Two first requests of the same administrator can race; the unique index lets one win.
            db.ChangeTracker.Clear();
            var existing = await FindBySubjectAsync(identity.Subject, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    private static ForbiddenException NotProvisioned() =>
        new(ErrorTexts.AdminNotProvisionedDetail, ErrorCodes.AdminNotProvisioned, ErrorTexts.AdminNotProvisionedTitle);
}
