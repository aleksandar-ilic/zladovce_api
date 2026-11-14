namespace Zladovce.Domain.Identity;

/// <summary>Administrator roles. Names match the Cognito groups and the values stored in <c>admin_users.role</c>.</summary>
public enum AdminRole
{
    /// <summary>Urednik: day-to-day administration.</summary>
    Staff = 1,

    /// <summary>Vlasnik: everything, including settings, expenses, admin users and the audit log.</summary>
    Owner = 2,
}
