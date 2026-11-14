using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Inventory;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> builder)
    {
        builder.ToTable("inventory", t =>
        {
            // The last line of defence against overselling: stock can never be stored below zero.
            t.HasCheckConstraint("ck_inventory_quantity_on_hand", "quantity_on_hand >= 0");
            t.HasCheckConstraint("ck_inventory_low_stock_threshold", "low_stock_threshold >= 0");
        });

        builder.HasKey(i => i.VariantId);
        builder.Property(i => i.VariantId).ValueGeneratedNever();

        builder.HasOne<ProductVariant>()
            .WithOne()
            .HasForeignKey<InventoryItem>(i => i.VariantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(i => i.QuantityOnHand).IsRequired();
        builder.Property(i => i.LowStockThreshold).IsRequired();
        builder.Property(i => i.UpdatedAt).IsRequired();

        builder.Ignore(i => i.Status);
    }
}

internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.ToTable("inventory_movements", t =>
        {
            t.HasCheckConstraint(
                "ck_inventory_movements_movement_type",
                "movement_type IN ('InitialStock', 'ManualAdjustment', 'OrderPlaced', 'OrderCancelled', 'ReturnRestock')");
            t.HasCheckConstraint("ck_inventory_movements_quantity_delta", "quantity_delta <> 0");
            t.HasCheckConstraint("ck_inventory_movements_quantity_after", "quantity_after >= 0");
            t.HasCheckConstraint(
                "ck_inventory_movements_reason",
                "movement_type <> 'ManualAdjustment' OR (reason IS NOT NULL AND btrim(reason) <> '')");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(m => m.VariantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.VariantId, m.CreatedAt }).IsDescending(false, true);

        builder.Property(m => m.MovementType).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(m => m.QuantityDelta).IsRequired();
        builder.Property(m => m.QuantityAfter).IsRequired();
        builder.Property(m => m.Reason).HasColumnType("text");

        // Plain column for now: the orders table (Phase 5) adds the foreign key and its index.
        builder.Property(m => m.OrderId);

        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(m => m.AdminUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(m => m.CreatedAt).IsRequired();
    }
}
