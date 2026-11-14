using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Customers;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Shipping;
using Zladovce.Infrastructure.RateLimiting;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal static class OrderStatusSql
{
    public static readonly string Values = string.Join(", ", Enum.GetNames<OrderStatus>().Select(n => $"'{n}'"));
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Email).HasColumnType("citext").IsRequired();
        builder.HasIndex(c => c.Email).IsUnique();
        builder.Property(c => c.FirstName).HasColumnType("text").IsRequired();
        builder.Property(c => c.LastName).HasColumnType("text").IsRequired();
        builder.Property(c => c.Phone).HasColumnType("text").IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}

internal sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("addresses", t => t.HasCheckConstraint("ck_addresses_postal_code", "postal_code ~ '^[0-9]{5}$'"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.HasOne<Customer>().WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.City).HasColumnType("text").IsRequired();
        builder.Property(a => a.Street).HasColumnType("text").IsRequired();
        builder.Property(a => a.PostalCode).HasColumnType("text").IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_status", $"status IN ({OrderStatusSql.Values})");
            t.HasCheckConstraint("ck_orders_payment_method", "payment_method IN ('CashOnDelivery')");
            t.HasCheckConstraint("ck_orders_order_number", "order_number ~ '^ZLD-[0-9]{4}-[0-9]{6,}$'");
            t.HasCheckConstraint("ck_orders_subtotal", "subtotal > 0");
            t.HasCheckConstraint("ck_orders_shipping_price", "shipping_price >= 0");
            t.HasCheckConstraint("ck_orders_total", "total = subtotal + shipping_price");
            t.HasCheckConstraint(
                "ck_orders_collected",
                "(collected_amount IS NULL AND collected_at IS NULL) OR (collected_amount > 0 AND collected_at IS NOT NULL)");
            t.HasCheckConstraint("ck_orders_request_hash", "octet_length(request_hash) = 32");
        });

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.OrderNumber).HasColumnType("text").IsRequired();
        builder.HasIndex(o => o.OrderNumber).IsUnique();
        builder.Property(o => o.IdempotencyKey).IsRequired();
        builder.HasIndex(o => o.IdempotencyKey).IsUnique();
        builder.Property(o => o.RequestHash).HasColumnType("bytea").IsRequired();

        builder.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(o => o.CustomerId);
        builder.HasOne<Address>().WithMany().HasForeignKey(o => o.ShippingAddressId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(o => o.ShippingAddressId);

        builder.Property(o => o.Status).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.HasIndex(o => new { o.Status, o.PlacedAt }).IsDescending(false, true);
        builder.HasIndex(o => o.PlacedAt);

        builder.Property(o => o.ContactFirstName).HasColumnType("text").IsRequired();
        builder.Property(o => o.ContactLastName).HasColumnType("text").IsRequired();
        builder.Property(o => o.ContactEmail).HasColumnType("citext").IsRequired();
        builder.HasIndex(o => o.ContactEmail);
        builder.Property(o => o.ContactPhone).HasColumnType("text").IsRequired();
        builder.HasIndex(o => o.ContactPhone);
        builder.Property(o => o.DeliveryNote).HasColumnType("text");
        builder.Property(o => o.PaymentMethod).HasConversion<string>().HasColumnType("text").IsRequired();

        builder.Property(o => o.Subtotal).HasColumnType(ColumnTypes.Money).IsRequired();
        builder.Property(o => o.ShippingPrice).HasColumnType(ColumnTypes.Money).IsRequired();
        builder.Property(o => o.Total).HasColumnType(ColumnTypes.Money).IsRequired();
        builder.Property(o => o.CollectedAmount).HasColumnType(ColumnTypes.Money);
        builder.Property(o => o.CollectedAt);

        builder.Property(o => o.PlacedAt).IsRequired();
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt).IsRequired();

        builder.Property(o => o.Version).IsRowVersion();

        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(o => o.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_order_items_unit_price", "unit_price > 0");
            t.HasCheckConstraint("ck_order_items_line_total", "line_total = unit_price * quantity");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.HasIndex(i => i.OrderId);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => i.VariantId);

        builder.Property(i => i.ProductName).HasColumnType("text").IsRequired();
        builder.Property(i => i.VariantName).HasColumnType("text").IsRequired();
        builder.Property(i => i.Sku).HasColumnType("text").IsRequired();
        builder.Property(i => i.UnitPrice).HasColumnType(ColumnTypes.Money).IsRequired();
        builder.Property(i => i.Quantity).IsRequired();
        builder.Property(i => i.LineTotal).HasColumnType(ColumnTypes.Money).IsRequired();
    }
}

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistoryEntry>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistoryEntry> builder)
    {
        builder.ToTable("order_status_history", t =>
        {
            t.HasCheckConstraint("ck_order_status_history_new_status", $"new_status IN ({OrderStatusSql.Values})");
            t.HasCheckConstraint(
                "ck_order_status_history_previous_status", $"previous_status IS NULL OR previous_status IN ({OrderStatusSql.Values})");
        });

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.HasOne<Order>().WithMany().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(h => new { h.OrderId, h.CreatedAt });

        builder.Property(h => h.PreviousStatus).HasConversion<string>().HasColumnType("text");
        builder.Property(h => h.NewStatus).HasConversion<string>().HasColumnType("text").IsRequired();

        // Delivered transitions in a period: bestsellers and (Phase 8) the delivered-value metric.
        builder.HasIndex(h => new { h.NewStatus, h.CreatedAt });

        builder.HasOne<AdminUser>().WithMany().HasForeignKey(h => h.ChangedByAdminId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(h => h.InternalNote).HasColumnType("text");
        builder.Property(h => h.CustomerMessage).HasColumnType("text");
        builder.Property(h => h.CreatedAt).IsRequired();
    }
}

internal sealed class OrderNoteConfiguration : IEntityTypeConfiguration<OrderNote>
{
    public void Configure(EntityTypeBuilder<OrderNote> builder)
    {
        builder.ToTable("order_notes", t => t.HasCheckConstraint("ck_order_notes_note", "btrim(note) <> ''"));
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.HasOne<Order>().WithMany().HasForeignKey(n => n.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(n => new { n.OrderId, n.CreatedAt });
        builder.HasOne<AdminUser>().WithMany().HasForeignKey(n => n.AdminUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(n => n.Note).HasColumnType("text").IsRequired();
        builder.Property(n => n.CreatedAt).IsRequired();
    }
}

internal sealed class OrderNumberCounterConfiguration : IEntityTypeConfiguration<OrderNumberCounter>
{
    public void Configure(EntityTypeBuilder<OrderNumberCounter> builder)
    {
        builder.ToTable("order_number_counters", t =>
        {
            t.HasCheckConstraint("ck_order_number_counters_year", "year BETWEEN 2000 AND 9999");
            t.HasCheckConstraint("ck_order_number_counters_last_value", "last_value > 0");
        });
        builder.HasKey(c => c.Year);
        builder.Property(c => c.Year).ValueGeneratedNever();
        builder.Property(c => c.LastValue).IsRequired();
    }
}

internal sealed class CourierCompanyConfiguration : IEntityTypeConfiguration<CourierCompany>
{
    public void Configure(EntityTypeBuilder<CourierCompany> builder)
    {
        builder.ToTable("courier_companies", t =>
        {
            t.HasCheckConstraint("ck_courier_companies_sort_order", "sort_order >= 0");
            t.HasCheckConstraint(
                "ck_courier_companies_tracking_url_template",
                "tracking_url_template IS NULL OR (tracking_url_template LIKE 'https://%' AND position('{trackingNumber}' in tracking_url_template) > 0)");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasColumnType("citext").IsRequired();
        builder.HasIndex(c => c.Name).IsUnique();
        builder.Property(c => c.TrackingUrlTemplate).HasColumnType("text");
        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.SortOrder).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("shipments");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasOne<Order>().WithMany().HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.OrderId);
        builder.HasOne<CourierCompany>().WithMany().HasForeignKey(s => s.CourierCompanyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.CourierCompanyId);
        builder.Property(s => s.TrackingNumber).HasColumnType("text");
        builder.Property(s => s.ShippedAt).IsRequired();
        builder.Property(s => s.Note).HasColumnType("text");
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
    }
}

internal sealed class RateLimitBucketConfiguration : IEntityTypeConfiguration<RateLimitBucket>
{
    public void Configure(EntityTypeBuilder<RateLimitBucket> builder)
    {
        builder.ToTable("rate_limit_buckets", t => t.HasCheckConstraint("ck_rate_limit_buckets_count", "count > 0"));
        builder.HasKey(b => new { b.BucketKey, b.WindowStart });
        builder.Property(b => b.BucketKey).HasColumnType("text");
        builder.Property(b => b.Count).IsRequired();

        // Expired windows are purged by window start.
        builder.HasIndex(b => b.WindowStart);
    }
}
