using System.Security.Claims;
using Zladovce.Application.Identity;

namespace Zladovce.Api.Auth;

/// <summary>
/// Checks that go beyond signature, issuer and lifetime for Amazon Cognito tokens.
/// Cognito access tokens have no <c>aud</c> claim; the app client is in <c>client_id</c>.
/// </summary>
public static class CognitoTokenRules
{
    /// <summary>Returns null when the token is an access token of the expected app client; otherwise the reason.</summary>
    public static string? FindViolation(ClaimsPrincipal principal, string expectedClientId)
    {
        if (principal.FindFirst(ZladovceClaims.CognitoTokenUse)?.Value != "access")
        {
            return "The token is not a Cognito access token.";
        }

        if (!string.Equals(principal.FindFirst(ZladovceClaims.CognitoClientId)?.Value, expectedClientId, StringComparison.Ordinal))
        {
            return "The token was issued to a different app client.";
        }

        if (string.IsNullOrEmpty(principal.FindFirst(ZladovceClaims.Subject)?.Value))
        {
            return "The token has no subject.";
        }

        return null;
    }

    /// <summary>Adds the effective role claim derived from <c>cognito:groups</c>. Without an administrator group no role is added.</summary>
    public static void ApplyRole(ClaimsIdentity identity)
    {
        var role = AdminRoleMapper.FromGroups(identity.FindAll(ZladovceClaims.CognitoGroups).Select(c => c.Value));
        if (role is { } value)
        {
            identity.AddClaim(new Claim(ZladovceClaims.Role, AdminRoleMapper.ToName(value)));
        }
    }
}
