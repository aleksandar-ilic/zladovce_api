using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zladovce.Application.Identity;

namespace Zladovce.Infrastructure.Identity;

/// <summary>
/// Reads the signed-in administrator's email and name with the Cognito <c>GetUser</c> operation.
/// <c>GetUser</c> is authorised by the user's own access token (scope <c>aws.cognito.signin.user.admin</c>),
/// so no AWS credentials or IAM permissions are involved. It is called only when an administrator
/// is provisioned for the first time, because Cognito access tokens do not contain the email address.
/// </summary>
public sealed class CognitoAdminProfileLookup : IAdminProfileLookup, IDisposable
{
    private readonly ILogger<CognitoAdminProfileLookup> _logger;
    private readonly Lazy<IAmazonCognitoIdentityProvider>? _client;

    public CognitoAdminProfileLookup(IOptions<CognitoOptions> options, ILogger<CognitoAdminProfileLookup> logger)
    {
        _logger = logger;
        var region = options.Value.Region;
        if (!string.IsNullOrWhiteSpace(region))
        {
            _client = new Lazy<IAmazonCognitoIdentityProvider>(() =>
                new AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(), RegionEndpoint.GetBySystemName(region)));
        }
    }

    public async Task<AdminProfile?> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return null;
        }

        try
        {
            var response = await _client.Value.GetUserAsync(new GetUserRequest { AccessToken = accessToken }, cancellationToken);
            return ToProfile(response.UserAttributes);
        }
        catch (AmazonCognitoIdentityProviderException ex)
        {
            // The error code is safe to log; the token and user attributes are not.
            _logger.LogWarning("Cognito GetUser failed with {ErrorCode} (HTTP {StatusCode}).", ex.ErrorCode, (int)ex.StatusCode);
            return null;
        }
    }

    internal static AdminProfile? ToProfile(IEnumerable<AttributeType>? attributes)
    {
        string? email = null;
        string? name = null;
        string? givenName = null;
        string? familyName = null;

        foreach (var attribute in attributes ?? [])
        {
            switch (attribute.Name)
            {
                case "email": email = attribute.Value; break;
                case "name": name = attribute.Value; break;
                case "given_name": givenName = attribute.Value; break;
                case "family_name": familyName = attribute.Value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.Join(' ', new[] { givenName, familyName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        return new AdminProfile(email.Trim(), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    public void Dispose()
    {
        if (_client is { IsValueCreated: true })
        {
            _client.Value.Dispose();
        }
    }
}
