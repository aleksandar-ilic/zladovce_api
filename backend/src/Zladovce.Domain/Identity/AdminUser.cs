using Zladovce.Domain.Common;

namespace Zladovce.Domain.Identity;

/// <summary>Local mirror of a Cognito administrator (table <c>admin_users</c>).</summary>
public sealed class AdminUser
{
    /// <summary>A login is recorded again only after this much time, so normal requests do not write to the database.</summary>
    public static readonly TimeSpan LoginRefreshInterval = TimeSpan.FromHours(1);

    private AdminUser()
    {
    }

    public Guid Id { get; private set; }
    public string CognitoSub { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public AdminRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AdminUser Create(string cognitoSub, string email, string? fullName, AdminRole role, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cognitoSub))
        {
            throw new DomainException("Admin user requires an identity subject.");
        }

        var normalizedEmail = email?.Trim();
        if (string.IsNullOrEmpty(normalizedEmail) || !LooksLikeEmail(normalizedEmail))
        {
            throw new DomainException("Admin user requires a valid email address.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new DomainException("Unknown admin role.");
        }

        return new AdminUser
        {
            Id = Guid.CreateVersion7(now),
            CognitoSub = cognitoSub.Trim(),
            Email = normalizedEmail,
            FullName = string.IsNullOrWhiteSpace(fullName) ? normalizedEmail : fullName.Trim(),
            Role = role,
            IsActive = true,
            LastLoginAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// An administrator invited by the Owner: the identity already exists in the identity provider (<paramref name="cognitoSub"/>),
    /// the person has not signed in yet (<see cref="LastLoginAt"/> null).
    /// </summary>
    public static AdminUser Invite(string cognitoSub, string email, string fullName, AdminRole role, DateTimeOffset now)
    {
        var admin = Create(cognitoSub, email, fullName, role, now);
        admin.LastLoginAt = null;
        return admin;
    }

    /// <summary>The Owner changes the role. Returns true when something changed.</summary>
    public bool ChangeRole(AdminRole role, DateTimeOffset now) => SyncRole(role, now);

    /// <summary>Aligns the stored role with the identity provider. Returns true when something changed.</summary>
    public bool SyncRole(AdminRole role, DateTimeOffset now)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainException("Unknown admin role.");
        }

        if (Role == role)
        {
            return false;
        }

        Role = role;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Records a login when the previous one is older than <see cref="LoginRefreshInterval"/>. Returns true when something changed.</summary>
    public bool TouchLogin(DateTimeOffset now)
    {
        if (LastLoginAt is { } last && now - last < LoginRefreshInterval)
        {
            return false;
        }

        LastLoginAt = now;
        UpdatedAt = now;
        return true;
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        UpdatedAt = now;
    }

    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }
}
