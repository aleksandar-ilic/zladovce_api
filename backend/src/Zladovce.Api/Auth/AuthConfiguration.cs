using Zladovce.Infrastructure.Identity;

namespace Zladovce.Api.Auth;

/// <summary>Configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Enables the development header authentication scheme. Honoured only in the Development
    /// environment; anywhere else the application refuses to start.
    /// </summary>
    public bool DevBypass { get; set; }
}

public static class AuthSchemes
{
    public const string Cognito = "Bearer";
    public const string Dev = "Dev";

    /// <summary>Chooses between the two schemes per request when both are registered.</summary>
    public const string Selector = "CognitoOrDev";
}

public static class AuthPolicies
{
    /// <summary>Owner or Staff.</summary>
    public const string AdminAny = "AdminAny";

    /// <summary>Owner only.</summary>
    public const string OwnerOnly = "OwnerOnly";
}

public static class ZladovceClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";

    /// <summary>The single effective administrator role (<c>Owner</c> or <c>Staff</c>), added after token validation.</summary>
    public const string Role = "zladovce:role";

    public const string CognitoGroups = "cognito:groups";
    public const string CognitoTokenUse = "token_use";
    public const string CognitoClientId = "client_id";
}

public static class StartupGuards
{
    /// <summary>
    /// Refuses to start with an unsafe or incomplete authentication setup:
    /// the development bypass outside Development (or inside AWS Lambda), or no Cognito settings
    /// when the bypass is off.
    /// </summary>
    public static void EnsureAuthConfiguration(
        IHostEnvironment environment,
        AuthOptions auth,
        CognitoOptions cognito,
        bool runningInLambda)
    {
        if (auth.DevBypass && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Auth:DevBypass is enabled in the '{environment.EnvironmentName}' environment. " +
                "The development authentication bypass is allowed only in Development. Remove the setting.");
        }

        if (auth.DevBypass && runningInLambda)
        {
            throw new InvalidOperationException(
                "Auth:DevBypass is enabled inside AWS Lambda. The development authentication bypass must never be deployed.");
        }

        if (!auth.DevBypass && !cognito.IsConfigured)
        {
            throw new InvalidOperationException(
                "Authentication is not configured: set Auth:Cognito:Region, Auth:Cognito:UserPoolId and Auth:Cognito:ClientId.");
        }
    }

    /// <summary>CORS origins must be exact origins: scheme and host, optional port, no path and no wildcard.</summary>
    public static void EnsureCorsOrigins(IReadOnlyCollection<string> origins)
    {
        foreach (var origin in origins)
        {
            var valid = !origin.Contains('*')
                && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && uri.GetLeftPart(UriPartial.Authority).Equals(origin, StringComparison.OrdinalIgnoreCase);

            if (!valid)
            {
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins contains '{origin}', which is not an exact origin such as https://example.com.");
            }
        }
    }
}
