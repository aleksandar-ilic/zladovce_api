using System.Text.Json;
using Zladovce.Application.Common;
using Zladovce.Domain.Audit;

namespace Zladovce.Application.Audit;

/// <summary>Well-known audit action names (<c>Entity.Verb</c>).</summary>
public static class AuditActions
{
    /// <summary>An administrator signed in for the first time and the local record was created.</summary>
    public const string AuthLogin = "Auth.Login";
}

public interface IAuditLogger
{
    /// <summary>
    /// Adds an audit entry to the current unit of work. The entry is stored when the caller saves,
    /// so it commits or rolls back together with the change it describes.
    /// </summary>
    /// <param name="action">Action name, see <see cref="AuditActions"/>.</param>
    /// <param name="entityType">Affected entity type, when there is one.</param>
    /// <param name="entityId">Affected entity id, when there is one.</param>
    /// <param name="changes">Object serialised to JSON. Must not contain secrets.</param>
    /// <param name="adminUserId">Overrides the current administrator (used before the request context is populated).</param>
    void Record(string action, string? entityType = null, string? entityId = null, object? changes = null, Guid? adminUserId = null);
}

public sealed class AuditLogger(IZladovceDbContext db, IRequestContext requestContext, TimeProvider timeProvider) : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(string action, string? entityType = null, string? entityId = null, object? changes = null, Guid? adminUserId = null)
    {
        var entry = AuditLog.Create(
            action,
            adminUserId ?? requestContext.Admin?.Id,
            entityType,
            entityId,
            changes is null ? null : JsonSerializer.Serialize(changes, JsonOptions),
            requestContext.IpAddress,
            timeProvider.GetUtcNow());

        db.AuditLogs.Add(entry);
    }
}
