using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Notifications;
using Zladovce.Application.Orders;
using Zladovce.Domain.Common;
using Zladovce.Domain.Marketing;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.Marketing;

/// <summary>Body of <c>POST /api/v1/newsletter/subscriptions</c>.</summary>
public sealed record NewsletterSubscribeRequest
{
    /// <summary>Up to 254 characters.</summary>
    public required string? Email { get; init; }

    /// <summary>Must be <c>true</c> (the visitor ticked the consent box).</summary>
    public required bool? Consent { get; init; }

    /// <summary>Honeypot: a hidden field people leave empty. Anything in it makes the request a silent no-op (same 202).</summary>
    public string? Website { get; init; }
}

public sealed class NewsletterSubscribeRequestValidator : AbstractValidator<NewsletterSubscribeRequest>
{
    public NewsletterSubscribeRequestValidator()
    {
        RuleFor(r => r.Email)
            .Must(e => !string.IsNullOrWhiteSpace(e) && e.Trim().Length <= CheckoutRules.MaxEmailLength && CheckoutRules.EmailPattern().IsMatch(e.Trim()))
            .WithMessage("Unesite ispravnu adresu e-pošte.");
        RuleFor(r => r.Consent).Equal(true).WithMessage("Za prijavu je potrebna vaša saglasnost.");
        RuleFor(r => r.Website).MaximumLength(500);
    }
}

/// <summary>Body of <c>POST /api/v1/newsletter/confirm</c> and <c>POST /api/v1/newsletter/unsubscribe</c>: the token from the email link.</summary>
public sealed record NewsletterTokenRequest
{
    public required string? Token { get; init; }
}

public sealed class NewsletterTokenRequestValidator : AbstractValidator<NewsletterTokenRequest>
{
    public NewsletterTokenRequestValidator() => RuleFor(r => r.Token).NotEmpty().WithMessage("Link nije ispravan.").MaximumLength(500);
}

/// <summary>202 response of public forms. The text is the same whatever happened (no enumeration of addresses).</summary>
public sealed record FormAcceptedResponse(string Message);

/// <param name="Status"><c>Confirmed</c> or <c>Unsubscribed</c>.</param>
public sealed record NewsletterStatusResponse(NewsletterSubscriberStatus Status, string Message);

/// <summary>Body of <c>POST /api/v1/contact</c>.</summary>
public sealed record ContactRequest
{
    /// <summary>1–100 characters.</summary>
    public required string? Name { get; init; }

    /// <summary>Reply address, up to 254 characters.</summary>
    public required string? Email { get; init; }

    /// <summary>1–5000 characters.</summary>
    public required string? Message { get; init; }

    /// <summary>Honeypot: hidden, must stay empty; otherwise the request is a silent no-op (same 202).</summary>
    public string? Website { get; init; }
}

public sealed class ContactRequestValidator : AbstractValidator<ContactRequest>
{
    public ContactRequestValidator()
    {
        RuleFor(r => r.Name)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Ime je obavezno.")
            .Must(v => v!.Trim().Length <= ContactMessage.MaxNameLength).WithMessage($"Ime može imati najviše {ContactMessage.MaxNameLength} znakova.");
        RuleFor(r => r.Email)
            .Must(e => !string.IsNullOrWhiteSpace(e) && e.Trim().Length <= CheckoutRules.MaxEmailLength && CheckoutRules.EmailPattern().IsMatch(e.Trim()))
            .WithMessage("Unesite ispravnu adresu e-pošte.");
        RuleFor(r => r.Message)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Poruka je obavezna.")
            .Must(v => v!.Trim().Length <= ContactMessage.MaxMessageLength).WithMessage($"Poruka može imati najviše {ContactMessage.MaxMessageLength} znakova.");
        RuleFor(r => r.Website).MaximumLength(500);
    }
}

public sealed record AdminSubscriberItem(
    Guid Id, string Email, NewsletterSubscriberStatus Status, DateTimeOffset ConsentedAt, DateTimeOffset? ConfirmedAt, DateTimeOffset? UnsubscribedAt, DateTimeOffset CreatedAt);

/// <param name="Counts">Number of addresses per status (whole table).</param>
public sealed record AdminSubscriberListResponse(PagedResult<AdminSubscriberItem> Subscribers, IReadOnlyDictionary<NewsletterSubscriberStatus, int> Counts);

public sealed record AdminContactMessageItem(Guid Id, string Name, string Email, string Message, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, string? ReadByAdminName);

public sealed record AdminContactMessageListResponse(PagedResult<AdminContactMessageItem> Messages, int UnreadCount);

public interface INewsletterService
{
    Task<FormAcceptedResponse> SubscribeAsync(NewsletterSubscribeRequest request, CancellationToken cancellationToken);
    Task<NewsletterStatusResponse> ConfirmAsync(string token, CancellationToken cancellationToken);
    Task<NewsletterStatusResponse> UnsubscribeAsync(string token, CancellationToken cancellationToken);
    Task<AdminSubscriberListResponse> ListAsync(NewsletterSubscriberStatus? status, string? search, PageRequest page, CancellationToken cancellationToken);
    Task<CsvFile> ExportConfirmedAsync(CancellationToken cancellationToken);
}

public interface IContactService
{
    /// <exception cref="ConflictException"><c>CONTACT_NOT_CONFIGURED</c> when the store has no contact email.</exception>
    Task<FormAcceptedResponse> SendAsync(ContactRequest request, CancellationToken cancellationToken);
    Task<AdminContactMessageListResponse> ListAsync(bool unreadOnly, PageRequest page, CancellationToken cancellationToken);
    Task<AdminContactMessageItem> SetReadAsync(Guid id, bool read, CancellationToken cancellationToken);
}

/// <summary>
/// Newsletter sign-up with double opt-in (A10: addresses are only collected; no campaigns are sent). Every sign-up answers the
/// same 202 whether the address is new, pending, confirmed or unsubscribed, so the form cannot tell whether an address is known.
/// </summary>
public sealed class NewsletterService(
    IZladovceDbContext db,
    IEmailOutbox outbox,
    INewsletterTokens tokens,
    TimeProvider timeProvider,
    ILogger<NewsletterService> logger) : INewsletterService
{
    public const string AcceptedMessage = "Hvala! Ako prijava još nije potvrđena, poslali smo vam poruku sa linkom za potvrdu. Proverite e-poštu.";

    public async Task<FormAcceptedResponse> SubscribeAsync(NewsletterSubscribeRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            logger.LogInformation("Newsletter sign-up ignored: honeypot field filled.");
            return new FormAcceptedResponse(AcceptedMessage);
        }

        var email = request.Email!.Trim();
        var now = timeProvider.GetUtcNow();
        var subscriber = await db.NewsletterSubscribers.SingleOrDefaultAsync(s => s.Email == email, cancellationToken);
        var send = true;
        if (subscriber is null)
        {
            subscriber = NewsletterSubscriber.SignUp(email, now);
            db.NewsletterSubscribers.Add(subscriber);
        }
        else
        {
            send = subscriber.SignUpAgain(now);
        }

        if (send)
        {
            await outbox.EnqueueAsync(
                new OutboxEmail(
                    EmailDedupKeys.NewsletterConfirm(subscriber.Id, subscriber.ConfirmationSentAt),
                    EmailTemplate.NewsletterConfirm,
                    null,
                    subscriber.Email,
                    new NewsletterConfirmPayload(NewsletterConfirmPayload.CurrentVersion, subscriber.Id, subscriber.ConfirmationSentAt).ToJson(),
                    null),
                cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same address signed up twice at the same moment: the other request won; the answer is the same.
            db.ChangeTracker.Clear();
        }

        return new FormAcceptedResponse(AcceptedMessage);
    }

    public async Task<NewsletterStatusResponse> ConfirmAsync(string token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var read = await tokens.ReadAsync(token, cancellationToken);
        if (read is not { Purpose: NewsletterTokenPurpose.Confirm, IssuedAt: { } issuedAt } || now - issuedAt > INewsletterTokens.ConfirmLifetime)
        {
            throw Invalid();
        }

        var subscriber = await db.NewsletterSubscribers.SingleOrDefaultAsync(s => s.Id == read.SubscriberId, cancellationToken);
        if (subscriber is null
            || subscriber.ConfirmationSentAt.ToUnixTimeMilliseconds() != issuedAt.ToUnixTimeMilliseconds()
            || subscriber.Status == NewsletterSubscriberStatus.Unsubscribed)
        {
            throw Invalid();
        }

        if (subscriber.Confirm(now))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new NewsletterStatusResponse(NewsletterSubscriberStatus.Confirmed, "Prijava je potvrđena. Hvala!");
    }

    public async Task<NewsletterStatusResponse> UnsubscribeAsync(string token, CancellationToken cancellationToken)
    {
        var read = await tokens.ReadAsync(token, cancellationToken);
        if (read is not { Purpose: NewsletterTokenPurpose.Unsubscribe })
        {
            throw Invalid();
        }

        var subscriber = await db.NewsletterSubscribers.SingleOrDefaultAsync(s => s.Id == read.SubscriberId, cancellationToken) ?? throw Invalid();
        if (subscriber.Unsubscribe(timeProvider.GetUtcNow()))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new NewsletterStatusResponse(NewsletterSubscriberStatus.Unsubscribed, "Odjavljeni ste. Više vam nećemo slati novosti.");
    }

    public async Task<AdminSubscriberListResponse> ListAsync(NewsletterSubscriberStatus? status, string? search, PageRequest page, CancellationToken cancellationToken)
    {
        var all = db.NewsletterSubscribers.AsNoTracking();
        var counts = new Dictionary<NewsletterSubscriberStatus, int>
        {
            [NewsletterSubscriberStatus.Pending] = await all.CountAsync(s => s.UnsubscribedAt == null && s.ConfirmedAt == null, cancellationToken),
            [NewsletterSubscriberStatus.Confirmed] = await all.CountAsync(s => s.UnsubscribedAt == null && s.ConfirmedAt != null, cancellationToken),
            [NewsletterSubscriberStatus.Unsubscribed] = await all.CountAsync(s => s.UnsubscribedAt != null, cancellationToken),
        };

        var query = status switch
        {
            NewsletterSubscriberStatus.Pending => all.Where(s => s.UnsubscribedAt == null && s.ConfirmedAt == null),
            NewsletterSubscriberStatus.Confirmed => all.Where(s => s.UnsubscribedAt == null && s.ConfirmedAt != null),
            NewsletterSubscriberStatus.Unsubscribed => all.Where(s => s.UnsubscribedAt != null),
            _ => all,
        };
        if (DbSearch.ContainsPattern(search) is { } pattern)
        {
            // Case-insensitive search.
            query = query.Where(s => EF.Functions.Like(s.Email.ToLower(), pattern.ToLower()));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var items = rows.Select(s => new AdminSubscriberItem(s.Id, s.Email, s.Status, s.ConsentedAt, s.ConfirmedAt, s.UnsubscribedAt, s.CreatedAt)).ToList();
        return new AdminSubscriberListResponse(PagedResult<AdminSubscriberItem>.Create(items, page.Page, page.PageSize, total), counts);
    }

    public async Task<CsvFile> ExportConfirmedAsync(CancellationToken cancellationToken)
    {
        var rows = await db.NewsletterSubscribers.AsNoTracking()
            .Where(s => s.UnsubscribedAt == null && s.ConfirmedAt != null)
            .OrderBy(s => s.ConfirmedAt).ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        var csv = new CsvWriter().Row("E-pošta", "Saglasnost data", "Potvrđeno");
        foreach (var s in rows)
        {
            csv.Text(s.Email).Text(OrderEmailRenderer.FormatDateTime(s.ConsentedAt)).Text(OrderEmailRenderer.FormatDateTime(s.ConfirmedAt!.Value)).EndRow();
        }

        return new CsvFile($"pretplatnici-{SerbianTime.DateOf(timeProvider.GetUtcNow()):yyyy-MM-dd}.csv", csv.ToBytes());
    }

    private static BadRequestException Invalid() =>
        new(ErrorTexts.NewsletterTokenInvalidDetail, ErrorCodes.NewsletterTokenInvalid, ErrorTexts.NewsletterTokenInvalidTitle);
}

/// <summary>
/// The contact form (A11): stores the message (<c>contact_messages</c>) and queues it as an email to the store contact address with
/// Reply-To = the sender, in one transaction. Without a store contact email the form is refused (409) instead of dropping mail.
/// </summary>
public sealed class ContactService(
    IZladovceDbContext db,
    IEmailOutbox outbox,
    IRequestContext requestContext,
    TimeProvider timeProvider,
    ILogger<ContactService> logger) : IContactService
{
    public const string AcceptedMessage = "Hvala na poruci! Odgovorićemo vam u najkraćem roku.";

    public async Task<FormAcceptedResponse> SendAsync(ContactRequest request, CancellationToken cancellationToken)
    {
        var storeEmail = await db.StoreSettings.AsNoTracking()
            .Where(s => s.Id == StoreSettingsRecord.SingletonId).Select(s => s.ContactEmail).SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(storeEmail))
        {
            throw new ConflictException(ErrorTexts.ContactNotConfiguredDetail, ErrorCodes.ContactNotConfigured, ErrorTexts.ContactNotConfiguredTitle);
        }

        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            logger.LogInformation("Contact message ignored: honeypot field filled.");
            return new FormAcceptedResponse(AcceptedMessage);
        }

        var now = timeProvider.GetUtcNow();
        var message = ContactMessage.Create(request.Name!, request.Email!, request.Message!, now);
        db.ContactMessages.Add(message);
        await outbox.EnqueueAsync(
            new OutboxEmail(
                EmailDedupKeys.ContactMessage(message.Id),
                EmailTemplate.ContactMessage,
                null,
                storeEmail,
                new ContactMessagePayload(ContactMessagePayload.CurrentVersion, message.Id, message.Name, message.Email, message.Message, now).ToJson(),
                null),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new FormAcceptedResponse(AcceptedMessage);
    }

    public async Task<AdminContactMessageListResponse> ListAsync(bool unreadOnly, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.ContactMessages.AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(m => m.ReadAt == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Skip(page.Skip).Take(page.PageSize))
            .ToListAsync(cancellationToken);
        var unread = await db.ContactMessages.CountAsync(m => m.ReadAt == null, cancellationToken);
        return new AdminContactMessageListResponse(PagedResult<AdminContactMessageItem>.Create(items, page.Page, page.PageSize, total), unread);
    }

    public async Task<AdminContactMessageItem> SetReadAsync(Guid id, bool read, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        var message = await db.ContactMessages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken) ?? throw new NotFoundException();
        if (read)
        {
            message.MarkRead(admin.Id, timeProvider.GetUtcNow());
        }
        else
        {
            message.MarkUnread();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await Project(db.ContactMessages.AsNoTracking().Where(m => m.Id == id)).SingleAsync(cancellationToken);
    }

    private IQueryable<AdminContactMessageItem> Project(IQueryable<ContactMessage> query) =>
        from m in query
        join a in db.AdminUsers on m.ReadByAdminId equals (Guid?)a.Id into admins
        from a in admins.DefaultIfEmpty()
        select new AdminContactMessageItem(m.Id, m.Name, m.Email, m.Message, m.CreatedAt, m.ReadAt, a != null ? a.FullName : null);
}
