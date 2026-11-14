using System.Net;
using Zladovce.Domain.Common;

namespace Zladovce.Domain.Audit;

/// <summary>Append-only audit entry (table <c>audit_logs</c>).</summary>
public sealed class AuditLog
{
    private AuditLog()
    {
    }

    public Guid Id { get; private set; }
    public Guid? AdminUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }

    /// <summary>JSON document with changed fields. Never contains secrets.</summary>
    public string? Changes { get; private set; }
    public IPAddress? IpAddress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Create(
        string action,
        Guid? adminUserId,
        string? entityType,
        string? entityId,
        string? changesJson,
        IPAddress? ipAddress,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new DomainException("Audit action is required.");
        }

        return new AuditLog
        {
            Id = Guid.CreateVersion7(now),
            AdminUserId = adminUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Changes = changesJson,
            IpAddress = ipAddress,
            CreatedAt = now,
        };
    }
}
