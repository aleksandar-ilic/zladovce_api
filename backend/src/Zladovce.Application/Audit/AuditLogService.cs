using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Paging;
using Zladovce.Domain.Common;

namespace Zladovce.Application.Audit;

/// <param name="Summary">Human-readable Serbian description, e.g. "Proizvod – izmena (name, isActive)".</param>
/// <param name="Changes">The stored change document (<c>{ "field": { "old": …, "new": … } }</c> or action data); never secrets.</param>
public sealed record AuditLogItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Action,
    string? EntityType,
    string? EntityId,
    Guid? AdminUserId,
    string? AdminName,
    string? AdminEmail,
    string? IpAddress,
    string Summary,
    JsonElement? Changes);

/// <param name="From">First day (Serbian local date), inclusive.</param>
/// <param name="To">Last day, inclusive.</param>
public sealed record AuditLogCriteria(Guid? AdminUserId, string? EntityType, string? EntityId, string? Action, DateOnly? From, DateOnly? To, PageRequest Page);

/// <summary>Distinct values for the filter drop-downs.</summary>
public sealed record AuditLogFiltersResponse(IReadOnlyList<AuditFilterOption> EntityTypes, IReadOnlyList<string> Actions);

public sealed record AuditFilterOption(string Value, string Label);

public interface IAuditLogService
{
    Task<PagedResult<AuditLogItem>> ListAsync(AuditLogCriteria criteria, CancellationToken cancellationToken);
    Task<AuditLogFiltersResponse> FiltersAsync(CancellationToken cancellationToken);
}

/// <summary>Reads <c>audit_logs</c> (Owner), newest first.</summary>
public sealed class AuditLogService(IZladovceDbContext db) : IAuditLogService
{
    public static readonly IReadOnlyDictionary<string, string> EntityLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Category"] = "Kategorija",
        ["Product"] = "Proizvod",
        ["ProductVariant"] = "Varijanta proizvoda",
        ["ProductImage"] = "Slika proizvoda",
        ["Inventory"] = "Zaliha",
        ["CourierCompany"] = "Kurirska služba",
        ["Shipment"] = "Pošiljka",
        ["StoreSettings"] = "Podešavanja prodavnice",
        ["Order"] = "Porudžbina",
        ["EmailOutbox"] = "E-poruka",
        ["AdminUser"] = "Administrator",
        ["BlogCategory"] = "Kategorija bloga",
        ["BlogPost"] = "Objava na blogu",
        ["CmsPage"] = "Stranica",
        ["FaqItem"] = "Pitanje (FAQ)",
        ["Testimonial"] = "Utisak kupca",
        ["Expense"] = "Trošak",
        ["ContactMessage"] = "Kontakt poruka",
    };

    private static readonly IReadOnlyDictionary<string, string> VerbLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Created"] = "dodavanje",
        ["Updated"] = "izmena",
        ["Archived"] = "arhiviranje",
        ["Restored"] = "vraćanje iz arhive",
        ["Deleted"] = "brisanje",
        ["Adjusted"] = "korekcija stanja",
        ["StatusChanged"] = "promena statusa",
        ["NoteAdded"] = "interna napomena",
        ["PaymentRecorded"] = "evidentirana naplata",
        ["PaymentCleared"] = "poništena naplata",
        ["ConfirmationResent"] = "ponovo poslata potvrda",
        ["TrackingRevoked"] = "opozvani kodovi za praćenje",
        ["Retried"] = "ponovno slanje",
        ["Login"] = "prva prijava",
        ["Invited"] = "pozvan",
        ["RoleChanged"] = "promena uloge",
        ["Deactivated"] = "deaktiviran",
        ["Activated"] = "aktiviran",
    };

    public async Task<PagedResult<AuditLogItem>> ListAsync(AuditLogCriteria criteria, CancellationToken cancellationToken)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (criteria.AdminUserId is { } adminId)
        {
            query = query.Where(a => a.AdminUserId == adminId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.EntityType))
        {
            query = query.Where(a => a.EntityType == criteria.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(criteria.EntityId))
        {
            query = query.Where(a => a.EntityId == criteria.EntityId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Action))
        {
            query = query.Where(a => a.Action == criteria.Action);
        }

        if (criteria.From is { } from)
        {
            var start = SerbianTime.StartOfDay(from);
            query = query.Where(a => a.CreatedAt >= start);
        }

        if (criteria.To is { } to)
        {
            var end = SerbianTime.StartOfDay(to.AddDays(1));
            query = query.Where(a => a.CreatedAt < end);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await (
                from a in query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip(criteria.Page.Skip).Take(criteria.Page.PageSize)
                join u in db.AdminUsers on a.AdminUserId equals (Guid?)u.Id into users
                from u in users.DefaultIfEmpty()
                select new { Log = a, Name = u != null ? u.FullName : null, Email = u != null ? u.Email : null })
            .ToListAsync(cancellationToken);

        var items = rows
            .OrderByDescending(r => r.Log.CreatedAt).ThenByDescending(r => r.Log.Id)
            .Select(r =>
            {
                JsonElement? changes = r.Log.Changes is null ? null : JsonDocument.Parse(r.Log.Changes).RootElement.Clone();
                return new AuditLogItem(
                    r.Log.Id, r.Log.CreatedAt, r.Log.Action, r.Log.EntityType, r.Log.EntityId, r.Log.AdminUserId, r.Name, r.Email,
                    r.Log.IpAddress?.ToString(), Summarize(r.Log.Action, r.Log.EntityType, changes), changes);
            })
            .ToList();

        return PagedResult<AuditLogItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<AuditLogFiltersResponse> FiltersAsync(CancellationToken cancellationToken)
    {
        var types = await db.AuditLogs.AsNoTracking().Where(a => a.EntityType != null).Select(a => a.EntityType!).Distinct().ToListAsync(cancellationToken);
        var actions = await db.AuditLogs.AsNoTracking().Select(a => a.Action).Distinct().ToListAsync(cancellationToken);
        return new AuditLogFiltersResponse(
            [.. types.Order(StringComparer.Ordinal).Select(t => new AuditFilterOption(t, EntityLabels.GetValueOrDefault(t, t)))],
            [.. actions.Order(StringComparer.Ordinal)]);
    }

    /// <summary>"{Entity label} – {verb label}" plus the changed field names for updates, e.g. "Proizvod – izmena (name, isActive)".</summary>
    public static string Summarize(string action, string? entityType, JsonElement? changes)
    {
        var dot = action.IndexOf('.');
        var entity = dot > 0 ? action[..dot] : entityType ?? action;
        var verb = dot > 0 ? action[(dot + 1)..] : string.Empty;
        var entityLabel = EntityLabels.GetValueOrDefault(entity, entityType is not null ? EntityLabels.GetValueOrDefault(entityType, entity) : entity);
        var verbLabel = VerbLabels.GetValueOrDefault(verb, verb);
        var text = verbLabel.Length == 0 ? entityLabel : $"{entityLabel} – {verbLabel}";

        if (verb == "Updated" && changes is { ValueKind: JsonValueKind.Object } document)
        {
            var fields = document.EnumerateObject().Select(p => p.Name).Take(8).ToList();
            if (fields.Count > 0)
            {
                text += $" ({string.Join(", ", fields)}{(document.EnumerateObject().Count() > fields.Count ? ", …" : string.Empty)})";
            }
        }
        else if (verb == "StatusChanged" && changes is { ValueKind: JsonValueKind.Object } status
            && status.TryGetProperty("status", out var change) && change.ValueKind == JsonValueKind.Object
            && change.TryGetProperty("old", out var from) && change.TryGetProperty("new", out var to))
        {
            text += $": {from} → {to}";
        }

        return text;
    }
}
