using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Zladovce.Application.Notifications;

namespace Zladovce.Infrastructure.Notifications;

public static class NotificationServices
{
    /// <summary>
    /// Tracking-code protection, the email sender of <c>Email:Provider</c> and the outbox dispatcher. Needs the persistence
    /// services and an <see cref="Application.Common.IRateLimiter"/>.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<StorefrontOptions>(configuration.GetSection(StorefrontOptions.SectionName));
        services.Configure<TrackingOptions>(configuration.GetSection(TrackingOptions.SectionName));

        services.TryAddSingleton<ITrackingKeyProvider>(sp =>
        {
            var tracking = sp.GetRequiredService<IOptions<TrackingOptions>>();
            return string.IsNullOrWhiteSpace(tracking.Value.TokenEncryptionKeySecretArn)
                ? new ConfiguredTrackingKeyProvider(tracking)
                : new SecretsManagerTrackingKeyProvider(tracking);
        });
        services.TryAddSingleton<ITrackingCodeProtector, AesGcmTrackingCodeProtector>();

        services.TryAddSingleton<IEmailSender>(sp =>
            sp.GetRequiredService<IOptions<EmailOptions>>().Value.Provider == EmailProvider.Smtp
                ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp)
                : ActivatorUtilities.CreateInstance<SesEmailSender>(sp));

        services.AddScoped<IEmailDispatcher, EmailDispatcher>();
        return services;
    }

    /// <summary>
    /// Checks the email and tracking configuration of a deployed environment (anything but Development), so a broken
    /// deployment fails at start instead of losing confirmation emails:
    /// <list type="bullet">
    /// <item>always (every order writes an encrypted code and links): <c>Storefront:BaseUrl</c> (absolute https URL) and a key
    /// source (<c>Tracking:TokenEncryptionKeySecretArn</c>, or a valid <c>Tracking:TokenEncryptionKey</c> that is not the public
    /// development key);</item>
    /// <item>when <paramref name="sendsEmail"/> (this host sends) and <c>Email:Enabled</c>: <c>Email:FromAddress</c>.</item>
    /// </list>
    /// <c>Email:Enabled=false</c> is allowed: rows are written and stay Pending.
    /// </summary>
    public static void EnsureConfiguration(IConfiguration configuration, bool isDevelopment, bool sendsEmail)
    {
        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
        var storefront = configuration.GetSection(StorefrontOptions.SectionName).Get<StorefrontOptions>() ?? new StorefrontOptions();
        var tracking = configuration.GetSection(TrackingOptions.SectionName).Get<TrackingOptions>() ?? new TrackingOptions();
        var development = isDevelopment;
        var problems = new List<string>();

        if (!development)
        {
            if (!storefront.IsConfigured || !storefront.BaseUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Storefront:BaseUrl must be the absolute https URL of the shop (links in emails).");
            }

            if (!tracking.HasKeySource)
            {
                problems.Add("Set Tracking:TokenEncryptionKeySecretArn (or Tracking:TokenEncryptionKey) for the tracking-code encryption key.");
            }
            else if (string.IsNullOrWhiteSpace(tracking.TokenEncryptionKeySecretArn))
            {
                if (TrackingOptions.DecodeKey(tracking.TokenEncryptionKey) is null)
                {
                    problems.Add("Tracking:TokenEncryptionKey must be the base64 encoding of exactly 32 bytes.");
                }
                else if (tracking.TokenEncryptionKey!.Trim() == TrackingOptions.DevelopmentKey)
                {
                    problems.Add("Tracking:TokenEncryptionKey is the public development key; it must not be used outside Development.");
                }
            }

            if (email.Dispatcher.RunInProcess && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")))
            {
                problems.Add("Email:Dispatcher:RunInProcess must be false inside AWS Lambda (the outbox worker Lambda sends emails).");
            }
        }

        if (sendsEmail && email.Enabled && string.IsNullOrWhiteSpace(email.FromAddress))
        {
            problems.Add("Email:FromAddress (the verified sender address) is required while Email:Enabled is true.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Email configuration is incomplete: " + string.Join(" ", problems));
        }
    }
}
