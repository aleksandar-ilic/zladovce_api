using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Zladovce.Application.Notifications;

namespace Zladovce.Infrastructure.Notifications;

/// <summary>
/// Amazon SES v2 <c>SendEmail</c> (simple content: HTML and text, UTF-8). Uses the Lambda execution role; needs
/// <c>ses:SendEmail</c> on the sender identity and the configuration set. The client is created on first use.
/// </summary>
public sealed class SesEmailSender(IOptions<EmailOptions> options) : IEmailSender, IDisposable
{
    private readonly Lazy<AmazonSimpleEmailServiceV2Client> _client = new(() =>
    {
        var region = options.Value.Ses.Region;
        return string.IsNullOrWhiteSpace(region)
            ? new AmazonSimpleEmailServiceV2Client()
            : new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(region));
    });

    public async Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var request = new SendEmailRequest
        {
            // SES expects non-ASCII display names as MIME encoded words ("Zladovce" is ASCII, "Žaklina" is not).
            FromEmailAddress = new MailboxAddress(message.FromName, message.FromAddress).ToString(FormatOptions.Default, encode: true),
            Destination = new Destination { ToAddresses = [message.To] },
            ReplyToAddresses = message.ReplyTo is null ? null : [message.ReplyTo],
            ConfigurationSetName = string.IsNullOrWhiteSpace(options.Value.Ses.ConfigurationSetName) ? null : options.Value.Ses.ConfigurationSetName,
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = message.Subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Html = new Content { Data = message.Html, Charset = "UTF-8" },
                        Text = new Content { Data = message.Text, Charset = "UTF-8" },
                    },
                },
            },
        };

        var response = await _client.Value.SendEmailAsync(request, cancellationToken);
        return response.MessageId;
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}

/// <summary>SMTP through MailKit (MIT licence): local development with Mailpit (<c>localhost:1025</c>, no TLS).</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Smtp;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName, message.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (message.ReplyTo is not null)
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(Math.Max(5, options.Value.Dispatcher.SendTimeoutSeconds)).TotalMilliseconds };
        var security = smtp.Security switch
        {
            "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.None,
        };

        await client.ConnectAsync(smtp.Host, smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        return mime.MessageId;
    }
}
