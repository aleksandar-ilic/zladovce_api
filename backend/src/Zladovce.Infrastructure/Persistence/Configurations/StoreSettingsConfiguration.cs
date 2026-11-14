using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Identity;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class StoreSettingsConfiguration : IEntityTypeConfiguration<StoreSettingsRecord>
{
    private const string Money = "numeric(12,2)";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<StoreSettingsRecord> builder)
    {
        builder.ToTable("store_settings", t =>
        {
            t.HasCheckConstraint("ck_store_settings_singleton", "id = 1");
            t.HasCheckConstraint("ck_store_settings_shipping_price", "shipping_price >= 0");
            t.HasCheckConstraint("ck_store_settings_free_shipping_threshold", "free_shipping_threshold IS NULL OR free_shipping_threshold > 0");
            t.HasCheckConstraint("ck_store_settings_min_order_amount", "min_order_amount IS NULL OR min_order_amount > 0");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.StoreName).HasColumnType("text").IsRequired();
        builder.Property(s => s.LogoKey).HasColumnType("text");
        builder.Property(s => s.ContactEmail).HasColumnType("citext");
        builder.Property(s => s.Phone).HasColumnType("text");
        builder.Property(s => s.BusinessAddress).HasColumnType("text");

        builder.Property(s => s.ShippingEnabled).IsRequired();
        builder.Property(s => s.ShippingPrice).HasColumnType(Money).IsRequired();
        builder.Property(s => s.FreeShippingThreshold).HasColumnType(Money);
        builder.Property(s => s.MinOrderAmount).HasColumnType(Money);
        builder.Property(s => s.EstimatedDeliveryText).HasColumnType("text");
        builder.Property(s => s.DeliveryInfo).HasColumnType("text");

        builder.Property(s => s.FacebookUrl).HasColumnType("text");
        builder.Property(s => s.InstagramUrl).HasColumnType("text");
        builder.Property(s => s.OtherLinks)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                links => JsonSerializer.Serialize(links, JsonOptions),
                json => JsonSerializer.Deserialize<List<StoreLink>>(json, JsonOptions) ?? new List<StoreLink>(),
                new ValueComparer<IReadOnlyList<StoreLink>>(
                    (a, b) => a!.SequenceEqual(b!),
                    links => links.Aggregate(0, (hash, link) => HashCode.Combine(hash, link.GetHashCode())),
                    links => links.ToList()));

        builder.Property(s => s.DefaultSeoTitle).HasColumnType("text");
        builder.Property(s => s.DefaultSeoDescription).HasColumnType("text");

        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(s => s.UpdatedByAdminId)
            .OnDelete(DeleteBehavior.Restrict);

        // Optimistic concurrency through the PostgreSQL xmin system column.
        builder.Property(s => s.Version).IsRowVersion();
    }
}
