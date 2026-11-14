namespace Zladovce.Infrastructure.Identity;

/// <summary>Configuration section <c>Auth:Cognito</c>. All values are public identifiers, not secrets.</summary>
public sealed class CognitoOptions
{
    public const string SectionName = "Auth:Cognito";

    public string? Region { get; set; }
    public string? UserPoolId { get; set; }

    /// <summary>App client id of the admin SPA; access tokens issued to any other client are rejected.</summary>
    public string? ClientId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(UserPoolId) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Token issuer and OpenID Connect authority of the user pool.</summary>
    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";
}
