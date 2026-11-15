using Zladovce.Domain.Common;

namespace Zladovce.Domain.Marketing;

/// <summary>Derived state of a newsletter subscriber.</summary>
public enum NewsletterSubscriberStatus
{
    /// <summary>Signed up, confirmation link not used yet (double opt-in).</summary>
    Pending,

    /// <summary>Confirmed the address: counts as a subscriber.</summary>
    Confirmed,

    /// <summary>Unsubscribed. Signing up again starts a new double opt-in.</summary>
    Unsubscribed,
}

/// <summary>
/// An email address that asked for the newsletter (table <c>newsletter_subscribers</c>). Double opt-in: only
/// <see cref="NewsletterSubscriberStatus.Confirmed"/> addresses are subscribers. No campaigns are sent by the platform (A10).
/// </summary>
public sealed class NewsletterSubscriber
{
    /// <summary>A pending address gets the confirmation email again only after this long (repeated sign-ups are not a mail cannon).</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(30);

    private NewsletterSubscriber()
    {
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;

    /// <summary>When consent was given (the latest sign-up).</summary>
    public DateTimeOffset ConsentedAt { get; private set; }

    /// <summary>When the latest confirmation email was requested; part of the confirmation token, so only the latest link works.</summary>
    public DateTimeOffset ConfirmationSentAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? UnsubscribedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public NewsletterSubscriberStatus Status =>
        UnsubscribedAt is not null ? NewsletterSubscriberStatus.Unsubscribed
        : ConfirmedAt is not null ? NewsletterSubscriberStatus.Confirmed
        : NewsletterSubscriberStatus.Pending;

    public static NewsletterSubscriber SignUp(string email, DateTimeOffset now)
    {
        var address = Text.NullIfBlank(email) ?? throw new DomainException("Email is required.");
        return new NewsletterSubscriber
        {
            Id = Guid.CreateVersion7(now),
            Email = address,
            ConsentedAt = now,
            ConfirmationSentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The same address signs up again. Returns true when a confirmation email must be sent: an unsubscribed address starts a new
    /// double opt-in; a pending one gets the link again after <see cref="ResendInterval"/>; a confirmed one needs nothing.
    /// </summary>
    public bool SignUpAgain(DateTimeOffset now)
    {
        switch (Status)
        {
            case NewsletterSubscriberStatus.Confirmed:
                return false;
            case NewsletterSubscriberStatus.Unsubscribed:
                UnsubscribedAt = null;
                ConfirmedAt = null;
                ConsentedAt = now;
                ConfirmationSentAt = now;
                UpdatedAt = now;
                return true;
            default:
                if (now - ConfirmationSentAt < ResendInterval)
                {
                    return false;
                }

                ConsentedAt = now;
                ConfirmationSentAt = now;
                UpdatedAt = now;
                return true;
        }
    }

    /// <summary>Confirms a pending address. Returns false when nothing changed (already confirmed).</summary>
    public bool Confirm(DateTimeOffset now)
    {
        if (Status != NewsletterSubscriberStatus.Pending)
        {
            return false;
        }

        ConfirmedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Unsubscribes. Repeating it changes nothing.</summary>
    public bool Unsubscribe(DateTimeOffset now)
    {
        if (UnsubscribedAt is not null)
        {
            return false;
        }

        UnsubscribedAt = now;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>
/// A message sent through the contact form (table <c>contact_messages</c>). Kept so that the administration can read it even when
/// the email to the store is delayed or lost; it is also emailed to the store contact address.
/// </summary>
public sealed class ContactMessage
{
    public const int MaxNameLength = 100;
    public const int MaxMessageLength = 5000;

    private ContactMessage()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string Message { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public Guid? ReadByAdminId { get; private set; }

    public static ContactMessage Create(string name, string email, string message, DateTimeOffset now)
    {
        var n = Text.NullIfBlank(name) ?? throw new DomainException("Name is required.");
        var e = Text.NullIfBlank(email) ?? throw new DomainException("Email is required.");
        var m = Text.NullIfBlank(message) ?? throw new DomainException("Message is required.");
        if (n.Length > MaxNameLength || m.Length > MaxMessageLength)
        {
            throw new DomainException("Contact message is too long.");
        }

        return new ContactMessage { Id = Guid.CreateVersion7(now), Name = n, Email = e, Message = m, CreatedAt = now };
    }

    public void MarkRead(Guid adminId, DateTimeOffset now)
    {
        if (ReadAt is not null)
        {
            return;
        }

        ReadAt = now;
        ReadByAdminId = adminId;
    }

    public void MarkUnread()
    {
        ReadAt = null;
        ReadByAdminId = null;
    }
}
