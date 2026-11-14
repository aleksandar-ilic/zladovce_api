using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Domain.Audit;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Customers;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;
using Zladovce.Domain.StoreSettings;
using Zladovce.Domain.Tracking;
using Zladovce.Infrastructure.RateLimiting;

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
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistoryEntry> OrderStatusHistory => Set<OrderStatusHistoryEntry>();
    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();
    public DbSet<OrderNumberCounter> OrderNumberCounters => Set<OrderNumberCounter>();
    public DbSet<CourierCompany> CourierCompanies => Set<CourierCompany>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<OrderTrackingToken> OrderTrackingTokens => Set<OrderTrackingToken>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<RateLimitBucket> RateLimitBuckets => Set<RateLimitBucket>();

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
