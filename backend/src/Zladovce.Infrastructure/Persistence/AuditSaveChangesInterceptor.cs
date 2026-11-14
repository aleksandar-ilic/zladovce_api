using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zladovce.Application.Common;
using Zladovce.Domain.Audit;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Inventory;

namespace Zladovce.Infrastructure.Persistence;

/// <summary>
/// Writes an <c>audit_logs</c> row for every create, update, archive, restore and delete of an audited entity
/// made by an administrator, in the same save (and therefore the same transaction) as the change itself.
/// The entry lists the changed fields as <c>{ "field": { "old": …, "new": … } }</c>.
/// Changes made without an administrator (public requests, background work) are not audited here.
/// Stock quantity changes do not go through the change tracker; the inventory service records those explicitly.
/// </summary>
public sealed class AuditSaveChangesInterceptor(IRequestContext requestContext, TimeProvider timeProvider) : SaveChangesInterceptor
{
    /// <summary>Audited entity types and the name they have in <c>audit_logs.entity_type</c>.</summary>
    public static readonly IReadOnlyDictionary<Type, string> AuditedEntities = new Dictionary<Type, string>
    {
        [typeof(Category)] = "Category",
        [typeof(Product)] = "Product",
        [typeof(ProductVariant)] = "ProductVariant",
        [typeof(ProductImage)] = "ProductImage",
        [typeof(InventoryItem)] = "Inventory",
    };

    /// <summary>Bookkeeping columns that say nothing about what the administrator changed.</summary>
    private static readonly HashSet<string> IgnoredProperties = ["UpdatedAt", "CreatedAt", "Version"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditEntries(DbContext? context)
    {
        if (context is null || requestContext.Admin is not { } admin)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && AuditedEntities.ContainsKey(e.Metadata.ClrType))
            .ToList();

        foreach (var entry in entries)
        {
            var entityType = AuditedEntities[entry.Metadata.ClrType];
            var (verb, changes) = Describe(entry);
            if (changes.Count == 0)
            {
                continue;
            }

            context.Set<AuditLog>().Add(AuditLog.Create(
                $"{entityType}.{verb}",
                admin.Id,
                entityType,
                KeyOf(entry),
                JsonSerializer.Serialize(changes, JsonOptions),
                requestContext.IpAddress,
                now));
        }
    }

    private static (string Verb, Dictionary<string, FieldChange> Changes) Describe(EntityEntry entry)
    {
        var changes = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        var verb = entry.State switch
        {
            EntityState.Added => "Created",
            EntityState.Deleted => "Deleted",
            _ => "Updated",
        };

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (IgnoredProperties.Contains(name) || property.Metadata.IsShadowProperty())
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (property.CurrentValue is not null)
                    {
                        changes[ToCamelCase(name)] = new FieldChange(null, property.CurrentValue);
                    }

                    break;

                case EntityState.Deleted:
                    if (property.OriginalValue is not null)
                    {
                        changes[ToCamelCase(name)] = new FieldChange(property.OriginalValue, null);
                    }

                    break;

                default:
                    if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                    {
                        changes[ToCamelCase(name)] = new FieldChange(property.OriginalValue, property.CurrentValue);
                        if (name == "DeletedAt")
                        {
                            verb = property.CurrentValue is null ? "Restored" : "Archived";
                        }
                    }

                    break;
            }
        }

        return (verb, changes);
    }

    private static string KeyOf(EntityEntry entry) =>
        string.Join(',', entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString()));

    private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private sealed record FieldChange(object? Old, object? New);
}
