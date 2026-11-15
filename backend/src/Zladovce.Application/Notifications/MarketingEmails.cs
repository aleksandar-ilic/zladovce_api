using System.Text.Json;
using Zladovce.Domain.Notifications;

namespace Zladovce.Application.Notifications;

/// <summary>
/// Payload of <see cref="EmailTemplate.NewsletterConfirm"/>. No token is stored: the confirmation and unsubscribe links are
/// signed when the email is rendered (<see cref="INewsletterTokens"/>), from the subscriber id and the confirmation request time.
/// </summary>
public sealed record NewsletterConfirmPayload(int Version, Guid SubscriberId, DateTimeOffset ConfirmationSentAt)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static NewsletterConfirmPayload FromJson(string json) =>
        JsonSerializer.Deserialize<NewsletterConfirmPayload>(json, JsonOptions) ?? throw new InvalidOperationException("Empty email payload.");
}

/// <summary>Payload of <see cref="EmailTemplate.ContactMessage"/>: the message itself (the email goes to the store; Reply-To = <see cref="Email"/>).</summary>
public sealed record ContactMessagePayload(int Version, Guid MessageId, string Name, string Email, string Message, DateTimeOffset ReceivedAt)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ContactMessagePayload FromJson(string json) =>
        JsonSerializer.Deserialize<ContactMessagePayload>(json, JsonOptions) ?? throw new InvalidOperationException("Empty email payload.");
}

/// <summary>What a newsletter token says.</summary>
public enum NewsletterTokenPurpose
{
    Confirm,
    Unsubscribe,
}

/// <param name="IssuedAt">Confirm tokens: the subscriber's confirmation request time it was made for.</param>
public sealed record NewsletterToken(NewsletterTokenPurpose Purpose, Guid SubscriberId, DateTimeOffset? IssuedAt);

/// <summary>
/// Signed (HMAC-SHA256) newsletter tokens. Confirm tokens carry the confirmation request time (only the latest link works) and
/// expire after <see cref="ConfirmLifetime"/>; unsubscribe tokens never expire (one-click unsubscribe from any email).
/// </summary>
public interface INewsletterTokens
{
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromDays(7);

    Task<string> CreateAsync(NewsletterToken token, CancellationToken cancellationToken);

    /// <summary>Null when the token is malformed or its signature is wrong (expiry is checked by the caller).</summary>
    Task<NewsletterToken?> ReadAsync(string? token, CancellationToken cancellationToken);
}

/// <summary>The newsletter confirmation and the contact-form email, Serbian Latin, same layout as the order emails.</summary>
public static class MarketingEmailRenderer
{
    /// <param name="confirmUrl">Absolute link to the storefront confirmation page with the token in the fragment.</param>
    /// <param name="unsubscribeUrl">Absolute one-click unsubscribe link.</param>
    public static RenderedEmail RenderNewsletterConfirm(string confirmUrl, string unsubscribeUrl, EmailBranding branding)
    {
        var subject = $"Potvrdite prijavu na novosti – {branding.StoreName}";
        var w = new OrderEmailRenderer.Writer();
        w.Heading("Potvrdite prijavu na novosti");
        w.Paragraph("Zdravo!");
        w.Paragraph($"Hvala što želite da dobijate novosti prodavnice {branding.StoreName}. Da bismo bili sigurni da je adresa vaša, potvrdite prijavu klikom na dugme.");
        w.Button("Potvrdi prijavu", confirmUrl);
        w.Paragraph("Link važi 7 dana. Ako se niste vi prijavili, zanemarite ovu poruku: bez potvrde nećete dobijati naše poruke.");
        w.Link("Ne želim da dobijam poruke (odjava)", unsubscribeUrl);
        var footer = $"Ovu poruku ste dobili jer je vaša adresa e-pošte uneta u formu za prijavu na novosti prodavnice {branding.StoreName}. " +
            "Odjava je moguća u svakom trenutku putem linka iz poruke.";
        return new RenderedEmail(subject, w.Html(subject, "Potvrdite prijavu na novosti jednim klikom.", branding, footer), w.Text(subject, branding, footer));
    }

    public static RenderedEmail RenderContactMessage(ContactMessagePayload payload, EmailBranding branding)
    {
        var subject = $"Nova poruka sa sajta: {payload.Name}";
        var w = new OrderEmailRenderer.Writer();
        w.Heading("Nova poruka sa kontakt forme");
        w.KeyValues(
        [
            ("Ime", payload.Name),
            ("E-pošta", payload.Email),
            ("Primljeno", OrderEmailRenderer.FormatDateTime(payload.ReceivedAt)),
        ]);
        w.MessageBox("Poruka", payload.Message);
        w.Paragraph("Odgovorom na ovu poruku pišete direktno pošiljaocu. Poruka je sačuvana i u administraciji (Kontakt poruke).");
        var footer = $"Ovu poruku je poslala kontakt forma sajta prodavnice {branding.StoreName}.";
        return new RenderedEmail(subject, w.Html(subject, $"Poruka od {payload.Name}.", branding, footer), w.Text(subject, branding, footer));
    }
}
