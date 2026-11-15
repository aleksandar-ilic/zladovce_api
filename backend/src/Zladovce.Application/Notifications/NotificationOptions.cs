namespace Zladovce.Application.Notifications;

/// <summary>Configuration section <c>Storefront</c>.</summary>
public sealed class StorefrontOptions
{
    public const string SectionName = "Storefront";

    /// <summary>Public base URL of the shop without a trailing slash, e.g. <c>https://zladovce.com</c>. Used for links in emails.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The tracking page (<c>{BaseUrl}/track-order</c>).</summary>
    public string TrackingPageUrl => $"{(BaseUrl ?? string.Empty).TrimEnd('/')}/track-order";

    /// <summary>Link with the code in the URL fragment, so it never reaches server or CDN logs.</summary>
    public static string TrackingLink(string trackingPageUrl, string groupedCode) => $"{trackingPageUrl}#kod={groupedCode}";

    /// <summary>Newsletter confirmation page: <c>{BaseUrl}/newsletter/potvrda#token={token}</c> (token in the fragment, never logged).</summary>
    public string NewsletterConfirmLink(string token) => $"{(BaseUrl ?? string.Empty).TrimEnd('/')}/newsletter/potvrda#token={token}";

    /// <summary>One-click unsubscribe page: <c>{BaseUrl}/newsletter/odjava#token={token}</c>.</summary>
    public string NewsletterUnsubscribeLink(string token) => $"{(BaseUrl ?? string.Empty).TrimEnd('/')}/newsletter/odjava#token={token}";

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>Configuration section <c>Tracking</c>: where the AES-GCM key for tracking codes in outbox rows comes from.</summary>
public sealed class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>
    /// The fixed key of <c>appsettings.Development.json</c>. It is public (in the repository) and refused outside Development.
    /// </summary>
    public const string DevelopmentKey = "ZGV2LW9ubHktdHJhY2tpbmcta2V5LTMyLWJ5dGVzISE=";

    /// <summary>Base64 of 32 random bytes. Local development and tests; on AWS use <see cref="TokenEncryptionKeySecretArn"/>.</summary>
    public string? TokenEncryptionKey { get; set; }

    /// <summary>
    /// Secrets Manager secret holding the key: the secret string is the base64 key itself or JSON <c>{ "key": "&lt;base64&gt;" }</c>.
    /// Takes precedence over <see cref="TokenEncryptionKey"/>.
    /// </summary>
    public string? TokenEncryptionKeySecretArn { get; set; }

    public bool HasKeySource => !string.IsNullOrWhiteSpace(TokenEncryptionKeySecretArn) || !string.IsNullOrWhiteSpace(TokenEncryptionKey);

    /// <summary>Decodes a base64 key; null unless it is exactly 32 bytes.</summary>
    public static byte[]? DecodeKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(base64.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public enum EmailProvider
{
    /// <summary>Amazon SES v2 (deployed environments).</summary>
    Ses,

    /// <summary>Any SMTP server (local development: Mailpit).</summary>
    Smtp,
}

/// <summary>Configuration section <c>Email</c>.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>False: nothing is sent, outbox rows stay Pending (they are still written).</summary>
    public bool Enabled { get; set; } = true;

    public EmailProvider Provider { get; set; } = EmailProvider.Ses;

    /// <summary>Sender address (a verified SES identity on AWS).</summary>
    public string? FromAddress { get; set; }

    /// <summary>Sender name; default the store name from the store settings.</summary>
    public string? FromName { get; set; }

    /// <summary>Reply-To; default the contact email of the store settings (none when that is not set either).</summary>
    public string? ReplyToAddress { get; set; }

    /// <summary>Optional absolute https URL of the store logo shown in the email header. No other external images are used.</summary>
    public string? LogoUrl { get; set; }

    public SesEmailOptions Ses { get; set; } = new();

    public SmtpEmailOptions Smtp { get; set; } = new();

    public EmailDispatcherOptions Dispatcher { get; set; } = new();
}

public sealed class SesEmailOptions
{
    /// <summary>SES configuration set (event publishing, suppression); optional.</summary>
    public string? ConfigurationSetName { get; set; }

    /// <summary>Region of the SES identity; default the Lambda's region.</summary>
    public string? Region { get; set; }
}

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;

    /// <summary><c>None</c> (Mailpit), <c>StartTls</c> or <c>SslOnConnect</c>.</summary>
    public string Security { get; set; } = "None";
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

/// <summary>Configuration section <c>Email:Dispatcher</c>.</summary>
public sealed class EmailDispatcherOptions
{
    /// <summary>
    /// Local development: a background service of the API sends due emails every <see cref="IntervalSeconds"/>, so no Lambda
    /// is needed. Must be false on AWS (the outbox worker Lambda does it).
    /// </summary>
    public bool RunInProcess { get; set; }

    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Most emails one run sends.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claimed row belongs to the worker. Must exceed <see cref="SendTimeoutSeconds"/>.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Timeout of one send call.</summary>
    public int SendTimeoutSeconds { get; set; } = 30;
}
