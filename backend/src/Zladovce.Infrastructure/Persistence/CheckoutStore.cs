using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Orders;

namespace Zladovce.Infrastructure.Persistence;

/// <summary>PostgreSQL statements of checkout, run on the request's context (and therefore in its transaction).</summary>
public sealed class CheckoutStore(ZladovceDbContext db) : ICheckoutStore
{
    public async Task LockIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var lockName = "order:" + idempotencyKey.ToString("D");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
    }

    public async Task<Guid> UpsertCustomerAsync(string email, string firstName, string lastName, string phone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7(now);
        var ids = await db.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO customers (id, email, first_name, last_name, phone, created_at, updated_at)
                VALUES ({id}, {email}, {firstName}, {lastName}, {phone}, {now}, {now})
                ON CONFLICT (email) DO UPDATE
                    SET first_name = EXCLUDED.first_name, last_name = EXCLUDED.last_name, phone = EXCLUDED.phone, updated_at = EXCLUDED.updated_at
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return ids.Single();
    }

    public async Task<int> NextOrderSequenceAsync(int year, CancellationToken cancellationToken)
    {
        var values = await db.Database.SqlQuery<int>(
                $"""
                INSERT INTO order_number_counters (year, last_value) VALUES ({year}, 1)
                ON CONFLICT (year) DO UPDATE SET last_value = order_number_counters.last_value + 1
                RETURNING last_value AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return values.Single();
    }
}

/// <summary><see cref="IOrderLocks"/> on the request's context (joins its transaction).</summary>
public sealed class OrderLocks(ZladovceDbContext db) : IOrderLocks
{
    public async Task LockAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT id FROM orders WHERE id = {orderId} FOR UPDATE", cancellationToken);
}

/// <summary>One advisory lock (fixed key) for every change of administrator roles or activity, released at commit.</summary>
public sealed class AdminUserLocks(ZladovceDbContext db) : Application.Identity.IAdminUserLocks
{
    /// <summary>Arbitrary constant key of the lock.</summary>
    public const long LockKey = 7_200_001;

    public async Task LockAdministratorsAsync(CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
}
