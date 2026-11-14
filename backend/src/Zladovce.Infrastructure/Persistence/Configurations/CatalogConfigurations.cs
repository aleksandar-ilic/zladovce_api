using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal static class ColumnTypes
{
    public const string Money = "numeric(12,2)";
    public const string UnitAmount = "numeric(10,3)";
    public const string NotDeleted = "deleted_at IS NULL";
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", t =>
        {
            t.HasCheckConstraint("ck_categories_sort_order", "sort_order >= 0");
            t.HasCheckConstraint(
                "ck_categories_image",
                "(image_key IS NULL AND image_width IS NULL AND image_height IS NULL) OR (image_key IS NOT NULL AND image_width > 0 AND image_height > 0)");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasColumnType("text").IsRequired();
        builder.Property(c => c.Slug).HasColumnType("text").IsRequired();
        builder.HasIndex(c => c.Slug).IsUnique().HasFilter(ColumnTypes.NotDeleted);

        builder.Property(c => c.Description).HasColumnType("text");
        builder.Property(c => c.ImageKey).HasColumnType("text");
        builder.Property(c => c.ImageWidth);
        builder.Property(c => c.ImageHeight);
        builder.Property(c => c.SortOrder).IsRequired();
        builder.Property(c => c.IsActive).IsRequired();
        builder.Property(c => c.SeoTitle).HasColumnType("text");
        builder.Property(c => c.SeoDescription).HasColumnType("text");
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
        builder.Property(c => c.DeletedAt);

        builder.Ignore(c => c.IsArchived);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.Name).HasColumnType("text").IsRequired();
        builder.Property(p => p.Slug).HasColumnType("text").IsRequired();
        builder.HasIndex(p => p.Slug).IsUnique().HasFilter(ColumnTypes.NotDeleted);

        builder.Property(p => p.ShortDescription).HasColumnType("text").IsRequired();
        builder.Property(p => p.Description).HasColumnType("text").IsRequired();

        builder.Property(p => p.Sku).HasColumnType("text").IsRequired();
        builder.HasIndex(p => p.Sku).IsUnique().HasFilter(ColumnTypes.NotDeleted);

        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.IsFeatured).IsRequired();
        builder.HasIndex(p => new { p.IsActive, p.IsFeatured });

        builder.Property(p => p.SeoTitle).HasColumnType("text");
        builder.Property(p => p.SeoDescription).HasColumnType("text");
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.Property(p => p.DeletedAt);

        // Optimistic concurrency through the PostgreSQL xmin system column.
        builder.Property(p => p.Version).IsRowVersion();

        builder.HasMany(p => p.Variants)
            .WithOne()
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(p => p.Images)
            .WithOne()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Images).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(p => p.IsArchived);
        builder.Ignore(p => p.CurrentVariants);
        builder.Ignore(p => p.OrderedImages);
        builder.Ignore(p => p.MainImage);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants", t =>
        {
            t.HasCheckConstraint("ck_product_variants_regular_price", "regular_price > 0");
            t.HasCheckConstraint(
                "ck_product_variants_discount_price",
                "discount_price IS NULL OR (discount_price > 0 AND discount_price < regular_price)");
            t.HasCheckConstraint("ck_product_variants_unit_amount", "unit_amount > 0");
            t.HasCheckConstraint("ck_product_variants_measurement_unit", "measurement_unit IN ('g', 'kg', 'ml', 'l', 'kom')");
            t.HasCheckConstraint("ck_product_variants_sort_order", "sort_order >= 0");
        });

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.HasIndex(v => v.ProductId);

        builder.Property(v => v.Name).HasColumnType("text").IsRequired();
        builder.Property(v => v.Sku).HasColumnType("text").IsRequired();
        builder.HasIndex(v => v.Sku).IsUnique().HasFilter(ColumnTypes.NotDeleted);

        builder.Property(v => v.UnitAmount).HasColumnType(ColumnTypes.UnitAmount).IsRequired();
        builder.Property(v => v.MeasurementUnit)
            .HasColumnType("text")
            .IsRequired()
            .HasConversion(unit => MeasurementUnitCodes.ToCode(unit), code => MeasurementUnitCodes.FromCode(code));

        builder.Property(v => v.RegularPrice).HasColumnType(ColumnTypes.Money).IsRequired();
        builder.Property(v => v.DiscountPrice).HasColumnType(ColumnTypes.Money);
        builder.Property(v => v.IsActive).IsRequired();
        builder.Property(v => v.SortOrder).IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Property(v => v.UpdatedAt).IsRequired();
        builder.Property(v => v.DeletedAt);

        builder.Ignore(v => v.IsArchived);
        builder.Ignore(v => v.IsSellable);
        builder.Ignore(v => v.EffectivePrice);
        builder.Ignore(v => v.HasDiscount);
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("product_images", t =>
        {
            t.HasCheckConstraint("ck_product_images_dimensions", "width > 0 AND height > 0");
            t.HasCheckConstraint("ck_product_images_sort_order", "sort_order >= 0");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.HasIndex(i => i.ProductId, "ix_product_images_product_id");

        // At most one main image per product.
        builder.HasIndex(i => i.ProductId, "ux_product_images_one_main").IsUnique().HasFilter("is_main").HasDatabaseName("ux_product_images_one_main");

        builder.Property(i => i.StorageKey).HasColumnType("text").IsRequired();
        builder.Property(i => i.AltText).HasColumnType("text");
        builder.Property(i => i.IsMain).IsRequired();
        builder.Property(i => i.SortOrder).IsRequired();
        builder.Property(i => i.Width).IsRequired();
        builder.Property(i => i.Height).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();
    }
}
