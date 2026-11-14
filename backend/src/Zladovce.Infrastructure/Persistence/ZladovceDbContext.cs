using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Domain.Audit;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Infrastructure.Persistence;

public sealed class ZladovceDbContext(DbContextOptions<ZladovceDbContext> options) : DbContext(options), IZladovceDbContext
{
    /// <summary>
    /// IMMUTABLE wrapper around <c>unaccent</c> (created by the CatalogAndInventory migration). The plain
    /// <c>unaccent()</c> is only STABLE, so it cannot be used in an index expression; the wrapper can.
    /// </summary>
    public const string UnaccentFunction = "zladovce_unaccent";

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<StoreSettingsRecord> StoreSettings => Set<StoreSettingsRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("unaccent");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.HasDbFunction(typeof(DbSearch).GetMethod(nameof(DbSearch.Unaccent), [typeof(string)])!)
            .HasName(UnaccentFunction)
            .IsBuiltIn(false);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ZladovceDbContext).Assembly);
    }
}
