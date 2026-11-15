using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zladovce.Application.Identity;
using Zladovce.Domain.Identity;

namespace Zladovce.Infrastructure.Identity;

/// <summary>
/// Administrator accounts in the Cognito user pool, with the Lambda execution role. Needs (scoped to the pool):
/// <c>cognito-idp:AdminCreateUser</c>, <c>AdminAddUserToGroup</c>, <c>AdminRemoveUserFromGroup</c>, <c>AdminDisableUser</c>,
/// <c>AdminEnableUser</c>, <c>AdminGetUser</c>. The pool must accept the email as user name (username attribute or alias)
/// and have the groups <c>Owner</c> and <c>Staff</c>. Cognito sends the invitation with the temporary password itself.
/// </summary>
public sealed class CognitoIdentityProvisioning : IIdentityProvisioning, IDisposable
{
    private readonly CognitoOptions _options;
    private readonly ILogger<CognitoIdentityProvisioning> _logger;
    private readonly Lazy<IAmazonCognitoIdentityProvider> _client;

    public CognitoIdentityProvisioning(IOptions<CognitoOptions> options, ILogger<CognitoIdentityProvisioning> logger)
        : this(options, logger, () => new AmazonCognitoIdentityProviderClient(RegionEndpoint.GetBySystemName(options.Value.Region)))
    {
    }

    /// <summary>Tests: a fake client.</summary>
    public CognitoIdentityProvisioning(IOptions<CognitoOptions> options, ILogger<CognitoIdentityProvisioning> logger, Func<IAmazonCognitoIdentityProvider> clientFactory)
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<IAmazonCognitoIdentityProvider>(clientFactory);
    }

    public async Task<string> CreateUserAsync(string email, string fullName, AdminRole role, CancellationToken cancellationToken)
    {
        AdminCreateUserResponse created;
        try
        {
            created = await _client.Value.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = email,
                DesiredDeliveryMediums = ["EMAIL"],
                UserAttributes =
                [
                    new AttributeType { Name = "email", Value = email },
                    new AttributeType { Name = "email_verified", Value = "true" },
                    new AttributeType { Name = "name", Value = fullName },
                ],
            }, cancellationToken);
        }
        catch (UsernameExistsException)
        {
            throw new IdentityUserExistsException();
        }
        catch (AmazonCognitoIdentityProviderException exception)
        {
            throw Failed("AdminCreateUser", exception);
        }

        var subject = created.User?.Attributes?.FirstOrDefault(a => a.Name == "sub")?.Value;
        try
        {
            if (string.IsNullOrEmpty(subject))
            {
                var user = await _client.Value.AdminGetUserAsync(new AdminGetUserRequest { UserPoolId = _options.UserPoolId, Username = email }, cancellationToken);
                subject = user.UserAttributes?.FirstOrDefault(a => a.Name == "sub")?.Value;
            }

            await _client.Value.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = email,
                GroupName = AdminRoleMapper.ToName(role),
            }, cancellationToken);
        }
        catch (AmazonCognitoIdentityProviderException exception)
        {
            throw Failed("AdminAddUserToGroup", exception);
        }

        return string.IsNullOrEmpty(subject) ? throw new IdentityProvisioningException("Cognito returned no subject for the new user.") : subject;
    }

    public async Task ChangeRoleAsync(string email, AdminRole from, AdminRole to, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Value.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = email,
                GroupName = AdminRoleMapper.ToName(to),
            }, cancellationToken);
            await _client.Value.AdminRemoveUserFromGroupAsync(new AdminRemoveUserFromGroupRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = email,
                GroupName = AdminRoleMapper.ToName(from),
            }, cancellationToken);
        }
        catch (AmazonCognitoIdentityProviderException exception)
        {
            throw Failed("group change", exception);
        }
    }

    public async Task SetEnabledAsync(string email, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
            {
                await _client.Value.AdminEnableUserAsync(new AdminEnableUserRequest { UserPoolId = _options.UserPoolId, Username = email }, cancellationToken);
            }
            else
            {
                await _client.Value.AdminDisableUserAsync(new AdminDisableUserRequest { UserPoolId = _options.UserPoolId, Username = email }, cancellationToken);
            }
        }
        catch (AmazonCognitoIdentityProviderException exception)
        {
            throw Failed(enabled ? "AdminEnableUser" : "AdminDisableUser", exception);
        }
    }

    private IdentityProvisioningException Failed(string operation, AmazonCognitoIdentityProviderException exception)
    {
        // Error code and status only: no email, no attributes.
        _logger.LogWarning("Cognito {Operation} failed with {ErrorCode} (HTTP {StatusCode}).", operation, exception.ErrorCode, (int)exception.StatusCode);
        return new IdentityProvisioningException($"Cognito {operation} failed ({exception.ErrorCode}).", exception);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
