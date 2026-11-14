using System.Net.Mail;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zladovce.Application.Identity;

namespace Zladovce.Api.Auth;

/// <summary>
/// DEVELOPMENT ONLY. Authenticates a request from the headers <c>X-Dev-User-Email</c> and
/// <c>X-Dev-User-Role</c> so the admin application can be developed without a Cognito user pool.
/// The scheme is registered only when the environment is Development and <c>Auth:DevBypass</c> is true;
/// <see cref="StartupGuards"/> stops the application otherwise.
/// </summary>
public sealed class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string EmailHeader = "X-Dev-User-Email";
    public const string RoleHeader = "X-Dev-User-Role";
    public const string SubjectPrefix = "dev:";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var email = Request.Headers[EmailHeader].ToString().Trim();
        var roleName = Request.Headers[RoleHeader].ToString().Trim();

        if (email.Length == 0 && roleName.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            return Task.FromResult(AuthenticateResult.Fail($"{EmailHeader} must be an email address."));
        }

        if (AdminRoleMapper.FromName(roleName) is not { } role)
        {
            return Task.FromResult(AuthenticateResult.Fail($"{RoleHeader} must be 'Owner' or 'Staff'."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ZladovceClaims.Subject, SubjectPrefix + email.ToLowerInvariant()),
                new Claim(ZladovceClaims.Email, email),
                new Claim(ZladovceClaims.Role, AdminRoleMapper.ToName(role)),
            ],
            authenticationType: Scheme.Name,
            nameType: ZladovceClaims.Email,
            roleType: ZladovceClaims.Role);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
