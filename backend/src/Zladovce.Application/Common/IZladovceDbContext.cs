using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Zladovce.Domain.Audit;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.Common;

/// <summary>
/// The one database context shared by all modules, as seen by application services.
/// Implemented by the EF Core context in the Infrastructure project.
/// </summary>
public interface IZladovceDbContext
{
    DbSet<AdminUser> AdminUsers { get; }
    DbSet<StoreSettingsRecord> StoreSettings { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductVariant> ProductVariants { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<InventoryItem> Inventory { get; }
    DbSet<InventoryMovement> InventoryMovements { get; }

    /// <summary>Transactions. Modules share one context so that a business operation is one transaction.</summary>
    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
