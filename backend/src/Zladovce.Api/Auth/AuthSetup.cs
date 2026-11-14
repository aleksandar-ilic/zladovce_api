using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Zladovce.Application.Identity;
using Zladovce.Infrastructure.Identity;

namespace Zladovce.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddZladovceAuth(
        this IServiceCollection services,
        IHostEnvironment environment,
        AuthOptions auth,
        CognitoOptions cognito)
    {
        // The only place where the development scheme can be switched on.
        var devBypass = environment.IsDevelopment() && auth.DevBypass;

        var defaultScheme = (cognito.IsConfigured, devBypass) switch
        {
            (true, true) => AuthSchemes.Selector,
            (false, true) => AuthSchemes.Dev,
            _ => AuthSchemes.Cognito,
        };

        var authentication = services.AddAuthentication(defaultScheme);

        if (cognito.IsConfigured)
        {
            authentication.AddJwtBearer(AuthSchemes.Cognito, options => ConfigureCognito(options, cognito));
        }

        if (devBypass)
        {
            authentication.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(AuthSchemes.Dev, null);
        }

        if (defaultScheme == AuthSchemes.Selector)
        {
            authentication.AddPolicyScheme(AuthSchemes.Selector, null, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(DevAuthenticationHandler.EmailHeader)
                        ? AuthSchemes.Dev
                        : AuthSchemes.Cognito);
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.AdminAny, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ZladovceClaims.Role, AdminRoleMapper.OwnerGroup, AdminRoleMapper.StaffGroup))
            .AddPolicy(AuthPolicies.OwnerOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ZladovceClaims.Role, AdminRoleMapper.OwnerGroup));

        return services;
    }

    private static void ConfigureCognito(JwtBearerOptions options, CognitoOptions cognito)
    {
        // Signing keys are downloaded from the user pool's JWKS endpoint and cached by the handler.
        options.Authority = cognito.Issuer;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognito.Issuer,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(60),
            // Cognito access tokens carry no "aud"; the app client is checked through "client_id" below.
            ValidateAudience = false,
            NameClaimType = ZladovceClaims.Subject,
            RoleClaimType = ZladovceClaims.Role,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var principal = context.Principal;
                if (principal?.Identity is not ClaimsIdentity identity)
                {
                    context.Fail("The token has no identity.");
                    return Task.CompletedTask;
                }

                var violation = CognitoTokenRules.FindViolation(principal, cognito.ClientId!);
                if (violation is not null)
                {
                    context.Fail(violation);
                    return Task.CompletedTask;
                }

                CognitoTokenRules.ApplyRole(identity);
                return Task.CompletedTask;
            },
        };
    }
}
