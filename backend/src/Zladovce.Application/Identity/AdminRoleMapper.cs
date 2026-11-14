using Zladovce.Domain.Identity;

namespace Zladovce.Application.Identity;

/// <summary>Maps identity-provider groups (Cognito <c>cognito:groups</c>) to an administrator role.</summary>
public static class AdminRoleMapper
{
    public const string OwnerGroup = "Owner";
    public const string StaffGroup = "Staff";

    /// <summary>
    /// Returns the highest role granted by the groups, or null when none of them is an administrator group.
    /// Group names are matched exactly (case-sensitive), as Cognito group names are.
    /// </summary>
    public static AdminRole? FromGroups(IEnumerable<string>? groups)
    {
        if (groups is null)
        {
            return null;
        }

        AdminRole? role = null;
        foreach (var group in groups)
        {
            switch (group)
            {
                case OwnerGroup:
                    return AdminRole.Owner;
                case StaffGroup:
                    role = AdminRole.Staff;
                    break;
            }
        }

        return role;
    }

    /// <summary>Parses a single role name (used by the development authentication scheme).</summary>
    public static AdminRole? FromName(string? name) => name switch
    {
        OwnerGroup => AdminRole.Owner,
        StaffGroup => AdminRole.Staff,
        _ => null,
    };

    public static string ToName(AdminRole role) => role switch
    {
        AdminRole.Owner => OwnerGroup,
        AdminRole.Staff => StaffGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
