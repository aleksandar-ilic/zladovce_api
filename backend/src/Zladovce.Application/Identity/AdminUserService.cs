using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Orders;
using Zladovce.Domain.Identity;

namespace Zladovce.Application.Identity;

/// <summary>Thrown by <see cref="IIdentityProvisioning"/> when the identity provider already has a user with this email.</summary>
public sealed class IdentityUserExistsException() : Exception("The identity provider already has a user with this email.");

/// <summary>Thrown by <see cref="IIdentityProvisioning"/> when the identity provider refused or failed. The message is safe to log.</summary>
public sealed class IdentityProvisioningException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The administrators' accounts in the identity provider. Cognito implementation: <c>AdminCreateUser</c> (Cognito emails a
/// temporary password), group membership (<c>Owner</c>/<c>Staff</c>), <c>AdminDisableUser</c>/<c>AdminEnableUser</c>. Development
/// implementation: no external account; the subject is <c>dev:{email}</c> (the development sign-in).
/// </summary>
public interface IIdentityProvisioning
{
    /// <summary>Creates the user, puts it in the group of <paramref name="role"/>, and returns its subject (<c>sub</c>).</summary>
    /// <exception cref="IdentityUserExistsException">The email is already a user there.</exception>
    /// <exception cref="IdentityProvisioningException">Any other failure.</exception>
    Task<string> CreateUserAsync(string email, string fullName, AdminRole role, CancellationToken cancellationToken);

    /// <summary>Moves the user from the group of <paramref name="from"/> to the group of <paramref name="to"/>.</summary>
    Task ChangeRoleAsync(string email, AdminRole from, AdminRole to, CancellationToken cancellationToken);

    Task SetEnabledAsync(string email, bool enabled, CancellationToken cancellationToken);
}

/// <summary>Transaction-scoped lock for changes of administrator roles and activity (PostgreSQL advisory lock).</summary>
public interface IAdminUserLocks
{
    Task LockAdministratorsAsync(CancellationToken cancellationToken);
}

/// <summary>Body of <c>POST /api/v1/admin/users</c> (invite).</summary>
public sealed record InviteAdminRequest
{
    /// <summary>The person's email; they receive the temporary password there (sent by Cognito).</summary>
    public required string? Email { get; init; }

    /// <summary>1–100 characters.</summary>
    public required string? FullName { get; init; }

    /// <summary><c>Owner</c> (Vlasnik) or <c>Staff</c> (Urednik).</summary>
    public required AdminRole? Role { get; init; }
}

public sealed class InviteAdminRequestValidator : AbstractValidator<InviteAdminRequest>
{
    public InviteAdminRequestValidator()
    {
        RuleFor(r => r.Email)
            .Must(e => !string.IsNullOrWhiteSpace(e) && e.Trim().Length <= CheckoutRules.MaxEmailLength && CheckoutRules.EmailPattern().IsMatch(e.Trim()))
            .WithMessage("Unesite ispravnu adresu e-pošte.");
        RuleFor(r => r.FullName)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Ime i prezime su obavezni.")
            .Must(v => v!.Trim().Length <= 100).WithMessage("Ime i prezime mogu imati najviše 100 znakova.");
        RuleFor(r => r.Role).NotNull().IsInEnum().WithMessage("Uloga mora biti Owner ili Staff.");
    }
}

/// <summary>Body of <c>PUT /api/v1/admin/users/{id}</c>.</summary>
public sealed record UpdateAdminRequest
{
    public required AdminRole? Role { get; init; }

    /// <summary>False blocks the user in the API at once and disables the identity-provider account.</summary>
    public required bool? IsActive { get; init; }
}

public sealed class UpdateAdminRequestValidator : AbstractValidator<UpdateAdminRequest>
{
    public UpdateAdminRequestValidator()
    {
        RuleFor(r => r.Role).NotNull().IsInEnum().WithMessage("Uloga mora biti Owner ili Staff.");
        RuleFor(r => r.IsActive).NotNull();
    }
}

/// <param name="RoleLabel">Vlasnik or Urednik.</param>
/// <param name="LastLoginAt">Null until the first sign-in (invited).</param>
/// <param name="IsCurrentUser">The signed-in Owner: role and active cannot be changed on this row.</param>
public sealed record AdminUserResponse(
    Guid Id, string Email, string FullName, AdminRole Role, string RoleLabel, bool IsActive, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool IsCurrentUser);

public sealed record AdminUserListResponse(IReadOnlyList<AdminUserResponse> Items);

public interface IAdminUserService
{
    Task<AdminUserListResponse> ListAsync(CancellationToken cancellationToken);
    Task<AdminUserResponse> InviteAsync(InviteAdminRequest request, CancellationToken cancellationToken);
    Task<AdminUserResponse> UpdateAsync(Guid id, UpdateAdminRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Administrator accounts (Owner only). Rules: nobody changes their own role or deactivates themselves; the last active Owner can
/// be neither demoted nor deactivated. The identity provider is changed first, then the row; every change is audited
/// (<c>AdminUser.Invited</c>, <c>AdminUser.RoleChanged</c>, <c>AdminUser.Deactivated</c>, <c>AdminUser.Activated</c>).
/// </summary>
public sealed class AdminUserService(
    IZladovceDbContext db,
    IIdentityProvisioning identity,
    IAdminUserLocks locks,
    IAuditLogger audit,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IAdminUserService
{
    public const string InvitedAction = "AdminUser.Invited";
    public const string RoleChangedAction = "AdminUser.RoleChanged";
    public const string DeactivatedAction = "AdminUser.Deactivated";
    public const string ActivatedAction = "AdminUser.Activated";

    public static string RoleLabel(AdminRole role) => role == AdminRole.Owner ? "Vlasnik" : "Urednik";

    public async Task<AdminUserListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var me = requestContext.Admin?.Id;
        var users = await db.AdminUsers.AsNoTracking().OrderByDescending(a => a.IsActive).ThenBy(a => a.FullName).ThenBy(a => a.Email).ToListAsync(cancellationToken);
        return new AdminUserListResponse([.. users.Select(u => ToResponse(u, me))]);
    }

    public async Task<AdminUserResponse> InviteAsync(InviteAdminRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        var email = request.Email!.Trim();
        var fullName = request.FullName!.Trim();
        var role = request.Role!.Value;
        if (await db.AdminUsers.AnyAsync(a => a.Email == email, cancellationToken))
        {
            throw EmailTaken();
        }

        string subject;
        try
        {
            subject = await identity.CreateUserAsync(email, fullName, role, cancellationToken);
        }
        catch (IdentityUserExistsException)
        {
            throw EmailTaken();
        }
        catch (IdentityProvisioningException)
        {
            throw ProviderFailed();
        }

        var user = AdminUser.Invite(subject, email, fullName, role, timeProvider.GetUtcNow());
        db.AdminUsers.Add(user);
        audit.Record(InvitedAction, nameof(AdminUser), user.Id.ToString(), new { email, fullName, role = AdminRoleMapper.ToName(role) });
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(user, admin.Id);
    }

    public async Task<AdminUserResponse> UpdateAsync(Guid id, UpdateAdminRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialises concurrent changes of administrators, so two Owners cannot demote each other at the same moment.
        await locks.LockAdministratorsAsync(cancellationToken);

        var user = await db.AdminUsers.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException();
        var role = request.Role!.Value;
        var active = request.IsActive!.Value;
        var roleChanges = user.Role != role;
        var activeChanges = user.IsActive != active;

        if (user.Id == admin.Id && (roleChanges || activeChanges))
        {
            throw new ConflictException(ErrorTexts.CannotChangeSelfDetail, ErrorCodes.CannotChangeSelf, ErrorTexts.CannotChangeSelfTitle);
        }

        var losesOwner = user.Role == AdminRole.Owner && user.IsActive && (role != AdminRole.Owner || !active);
        if (losesOwner && await db.AdminUsers.CountAsync(a => a.Role == AdminRole.Owner && a.IsActive && a.Id != user.Id, cancellationToken) == 0)
        {
            throw new ConflictException(ErrorTexts.LastOwnerDetail, ErrorCodes.LastOwner, ErrorTexts.LastOwnerTitle);
        }

        var now = timeProvider.GetUtcNow();
        try
        {
            if (roleChanges)
            {
                await identity.ChangeRoleAsync(user.Email, user.Role, role, cancellationToken);
            }

            if (activeChanges)
            {
                await identity.SetEnabledAsync(user.Email, active, cancellationToken);
            }
        }
        catch (IdentityProvisioningException)
        {
            throw ProviderFailed();
        }

        if (roleChanges)
        {
            audit.Record(RoleChangedAction, nameof(AdminUser), user.Id.ToString(), new
            {
                role = new { old = AdminRoleMapper.ToName(user.Role), @new = AdminRoleMapper.ToName(role) },
            });
            user.ChangeRole(role, now);
        }

        if (activeChanges)
        {
            audit.Record(active ? ActivatedAction : DeactivatedAction, nameof(AdminUser), user.Id.ToString(), new { isActive = new { old = !active, @new = active } });
            if (active)
            {
                user.Activate(now);
            }
            else
            {
                user.Deactivate(now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(user, admin.Id);
    }

    private static AdminUserResponse ToResponse(AdminUser u, Guid? me) =>
        new(u.Id, u.Email, u.FullName, u.Role, RoleLabel(u.Role), u.IsActive, u.LastLoginAt, u.CreatedAt, u.UpdatedAt, u.Id == me);

    private static ConflictException EmailTaken() => new(
        ErrorTexts.AdminEmailTakenDetail, ErrorCodes.AdminEmailTaken, ErrorTexts.AdminEmailTakenTitle,
        new Dictionary<string, string[]> { ["email"] = [ErrorTexts.AdminEmailTakenDetail] });

    private static DependencyFailedException ProviderFailed() =>
        new(ErrorCodes.IdentityProviderError, ErrorTexts.IdentityProviderErrorTitle, ErrorTexts.IdentityProviderErrorDetail);
}

/// <summary>Development: no identity provider; the row's subject is the development sign-in subject <c>dev:{email}</c>.</summary>
public sealed class DevelopmentIdentityProvisioning : IIdentityProvisioning
{
    public const string SubjectPrefix = "dev:";

    public Task<string> CreateUserAsync(string email, string fullName, AdminRole role, CancellationToken cancellationToken) =>
        Task.FromResult(SubjectPrefix + email.Trim().ToLowerInvariant());

    public Task ChangeRoleAsync(string email, AdminRole from, AdminRole to, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetEnabledAsync(string email, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
}
