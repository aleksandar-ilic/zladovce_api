using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Domain.Notifications;

namespace Zladovce.Application.Notifications;

/// <summary>
/// An outbox email as the administration sees it: state and delivery data only, never the body or the payload.
/// </summary>
/// <param name="Template"><c>OrderReceived</c>, <c>OrderAccepted</c>, <c>OrderPreparing</c>, <c>OrderShipped</c>, <c>OrderDelivered</c>, <c>OrderCancelled</c>.</param>
/// <param name="Status"><c>Pending</c>, <c>Processing</c>, <c>Sent</c>, <c>Dead</c>.</param>
/// <param name="Attempts">Attempts since it was queued or last retried by an administrator.</param>
/// <param name="LastError">Error of the last failed attempt (provider message, recipient address removed).</param>
/// <param name="NextAttemptAt">When the next attempt is due (Pending).</param>
/// <param name="IsResent">True for a confirmation resent by an administrator.</param>
public sealed record AdminEmailItem(
    Guid Id,
    EmailTemplate Template,
    EmailOutboxStatus Status,
    Guid? OrderId,
    string? OrderNumber,
    string Recipient,
    int Attempts,
    string? LastError,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    bool IsResent);

/// <summary>One send attempt.</summary>
/// <param name="ProviderMessageId">SES message id (or the SMTP server's answer) of a successful attempt.</param>
public sealed record AdminEmailAttempt(int AttemptNumber, bool Succeeded, string? ProviderMessageId, string? Error, DateTimeOffset CreatedAt);

/// <summary>Response of <c>GET /api/v1/admin/emails/{id}</c>.</summary>
/// <param name="AttemptLog">Every attempt, oldest first.</param>
public sealed record AdminEmailDetail(AdminEmailItem Email, IReadOnlyList<AdminEmailAttempt> AttemptLog);

/// <param name="Statuses">Any of these; empty = all.</param>
public sealed record AdminEmailListCriteria(IReadOnlySet<EmailOutboxStatus> Statuses, Guid? OrderId, PageRequest Page);

public interface IAdminEmailService
{
    Task<PagedResult<AdminEmailItem>> ListAsync(AdminEmailListCriteria criteria, CancellationToken cancellationToken);

    Task<AdminEmailDetail> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The emails of one order, oldest first (order detail).</summary>
    Task<IReadOnlyList<AdminEmailItem>> ForOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a Dead email again: Pending, due now, attempts start over. When its tracking code is gone (erased or never
    /// stored) a new code is issued for the order. Other states: 409 <c>EMAIL_NOT_RETRYABLE</c>.
    /// </summary>
    Task<AdminEmailItem> RetryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Queues a new confirmation ("Porudžbina primljena" with the current order data) with a new tracking code.</summary>
    Task<AdminEmailItem> ResendConfirmationAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class AdminEmailService(
    IZladovceDbContext db,
    IOrderNotifier notifier,
    ITrackingCodeProtector protector,
    IAuditLogger audit,
    Orders.IOrderLocks orderLocks,
    TimeProvider timeProvider) : IAdminEmailService
{
    public const string RetriedAction = "Email.Retried";
    public const string ConfirmationResentAction = "Order.ConfirmationResent";

    public async Task<PagedResult<AdminEmailItem>> ListAsync(AdminEmailListCriteria criteria, CancellationToken cancellationToken)
    {
        var query = db.EmailOutbox.AsNoTracking();
        if (criteria.Statuses.Count > 0)
        {
            var statuses = criteria.Statuses.ToList();
            query = query.Where(m => statuses.Contains(m.Status));
        }

        if (criteria.OrderId is { } orderId)
        {
            query = query.Where(m => m.OrderId == orderId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize))
            .ToListAsync(cancellationToken);

        return PagedResult<AdminEmailItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<AdminEmailDetail> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await Project(db.EmailOutbox.AsNoTracking().Where(m => m.Id == id)).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();
        var attempts = await db.EmailLogs.AsNoTracking()
            .Where(l => l.OutboxId == id)
            .OrderBy(l => l.AttemptNumber).ThenBy(l => l.CreatedAt)
            .Select(l => new AdminEmailAttempt(l.AttemptNumber, l.Succeeded, l.SesMessageId, l.Error, l.CreatedAt))
            .ToListAsync(cancellationToken);
        return new AdminEmailDetail(item, attempts);
    }

    public async Task<IReadOnlyList<AdminEmailItem>> ForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await Project(db.EmailOutbox.AsNoTracking().Where(m => m.OrderId == orderId).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            .ToListAsync(cancellationToken);

    public async Task<AdminEmailItem> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var message = await db.EmailOutbox.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException();
        if (message.Status != EmailOutboxStatus.Dead)
        {
            throw NotRetryable();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        byte[]? newToken = null;
        if (message.EncryptedToken is null && message.OrderId is { } orderId)
        {
            newToken = await protector.ProtectAsync(notifier.IssueCode(orderId), message.Id, cancellationToken);
        }

        // Conditional update: of two administrators retrying at once (or a retry racing anything else) exactly one wins.
        var now = timeProvider.GetUtcNow();
        var updated = await db.EmailOutbox
            .Where(m => m.Id == id && m.Status == EmailOutboxStatus.Dead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Pending)
                .SetProperty(m => m.AttemptCount, 0)
                .SetProperty(m => m.NextAttemptAt, now)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.EncryptedToken, m => newToken ?? m.EncryptedToken), cancellationToken);
        if (updated == 0)
        {
            throw NotRetryable();
        }

        audit.Record(RetriedAction, "EmailOutbox", message.Id.ToString(), new
        {
            template = message.Template.ToString(),
            orderId = message.OrderId,
            newTrackingCode = newToken is not null,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return await Project(db.EmailOutbox.AsNoTracking().Where(m => m.Id == id)).SingleAsync(cancellationToken);
    }

    public async Task<AdminEmailItem> ResendConfirmationAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The order row is locked so that two resends at the same moment get the numbers n and n + 1.
        await orderLocks.LockAsync(orderId, cancellationToken);
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new NotFoundException();
        var address = await db.Addresses.AsNoTracking().SingleAsync(a => a.Id == order.ShippingAddressId, cancellationToken);

        var prefix = $"order:{orderId:D}:resend:";
        var sequence = await db.EmailOutbox.CountAsync(m => m.OrderId == orderId && m.DedupKey.StartsWith(prefix), cancellationToken) + 1;

        var message = await notifier.ResendConfirmationAsync(order, address, sequence, cancellationToken);
        audit.Record(ConfirmationResentAction, "Order", orderId.ToString(), new { outboxId = message.Id, sequence });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return await Project(db.EmailOutbox.AsNoTracking().Where(m => m.Id == message.Id)).SingleAsync(cancellationToken);
    }

    private static ConflictException NotRetryable() =>
        new(ErrorTexts.EmailNotRetryableDetail, ErrorCodes.EmailNotRetryable, ErrorTexts.EmailNotRetryableTitle);

    private IQueryable<AdminEmailItem> Project(IQueryable<EmailOutboxMessage> query) =>
        from m in query
        join o in db.Orders on m.OrderId equals (Guid?)o.Id into orders
        from o in orders.DefaultIfEmpty()
        select new AdminEmailItem(
            m.Id,
            m.Template,
            m.Status,
            m.OrderId,
            o != null ? o.OrderNumber : null,
            m.Recipient,
            m.AttemptCount,
            m.LastError,
            m.NextAttemptAt,
            m.CreatedAt,
            m.SentAt,
            m.DedupKey.Contains(":resend:"));
}
