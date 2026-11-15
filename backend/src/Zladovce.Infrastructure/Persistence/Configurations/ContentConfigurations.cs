using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Content;
using Zladovce.Domain.Finance;
using Zladovce.Domain.Identity;
using Zladovce.Domain.Marketing;
using Zladovce.Domain.Site;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class BlogCategoryConfiguration : IEntityTypeConfiguration<BlogCategory>
{
    public void Configure(EntityTypeBuilder<BlogCategory> builder)
    {
        builder.ToTable("blog_categories", t => t.HasCheckConstraint("ck_blog_categories_sort_order", "sort_order >= 0"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasColumnType("text").IsRequired();
        builder.Property(c => c.Slug).HasColumnType("text").IsRequired();
        builder.HasIndex(c => c.Slug).IsUnique();
        builder.Property(c => c.SortOrder).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}

internal sealed class BlogPostConfiguration : IEntityTypeConfiguration<BlogPost>
{
    public void Configure(EntityTypeBuilder<BlogPost> builder)
    {
        builder.ToTable("blog_posts", t =>
        {
            t.HasCheckConstraint("ck_blog_posts_status", "status IN ('Draft', 'Published')");
            t.HasCheckConstraint("ck_blog_posts_published", "status <> 'Published' OR published_at IS NOT NULL");
            t.HasCheckConstraint("ck_blog_posts_archived", "deleted_at IS NULL OR status = 'Draft'");
            t.HasCheckConstraint(
                "ck_blog_posts_cover",
                "(cover_image_key IS NULL AND cover_image_width IS NULL AND cover_image_height IS NULL) OR " +
                "(cover_image_key IS NOT NULL AND cover_image_width > 0 AND cover_image_height > 0)");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasOne<BlogCategory>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.CategoryId);
        builder.HasOne<AdminUser>().WithMany().HasForeignKey(p => p.AuthorAdminId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.AuthorAdminId);
        builder.Property(p => p.Title).HasColumnType("text").IsRequired();
        builder.Property(p => p.Slug).HasColumnType("text").IsRequired();
        builder.HasIndex(p => p.Slug).IsUnique().HasFilter("deleted_at IS NULL");
        builder.Property(p => p.Excerpt).HasColumnType("text").IsRequired();
        builder.Property(p => p.CoverImageKey).HasColumnType("text");
        builder.Property(p => p.ContentHtml).HasColumnType("text").IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(p => p.SeoTitle).HasColumnType("text");
        builder.Property(p => p.SeoDescription).HasColumnType("text");
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // Public list: published, newest first.
        builder.HasIndex(p => new { p.Status, p.PublishedAt }).IsDescending(false, true).HasFilter("deleted_at IS NULL");
        builder.Property(p => p.Version).IsRowVersion();
    }
}

internal sealed class FaqItemConfiguration : IEntityTypeConfiguration<FaqItem>
{
    public void Configure(EntityTypeBuilder<FaqItem> builder)
    {
        builder.ToTable("faq_items", t => t.HasCheckConstraint("ck_faq_items_sort_order", "sort_order >= 0"));
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Question).HasColumnType("text").IsRequired();
        builder.Property(f => f.Answer).HasColumnType("text").IsRequired();
        builder.Property(f => f.SortOrder).IsRequired();
        builder.Property(f => f.IsPublished).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();
        builder.HasIndex(f => f.SortOrder);
    }
}

internal sealed class TestimonialConfiguration : IEntityTypeConfiguration<Testimonial>
{
    public void Configure(EntityTypeBuilder<Testimonial> builder)
    {
        builder.ToTable("testimonials", t => t.HasCheckConstraint("ck_testimonials_sort_order", "sort_order >= 0"));
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.AuthorName).HasColumnType("text").IsRequired();
        builder.Property(f => f.Place).HasColumnType("text");
        builder.Property(f => f.Text).HasColumnType("text").IsRequired();
        builder.Property(f => f.IsPublished).IsRequired();
        builder.Property(f => f.SortOrder).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();
    }
}

internal sealed class CmsPageConfiguration : IEntityTypeConfiguration<CmsPage>
{
    public static readonly string KeyValues = string.Join(", ", Enum.GetValues<CmsPageKey>().Select(k => $"'{CmsPageKeys.ToKey(k)}'"));

    public void Configure(EntityTypeBuilder<CmsPage> builder)
    {
        builder.ToTable("cms_pages", t => t.HasCheckConstraint("ck_cms_pages_key", $"key IN ({KeyValues})"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Key).HasColumnType("text").IsRequired();
        builder.HasIndex(p => p.Key).IsUnique();
        builder.Property(p => p.Title).HasColumnType("text");
        builder.Property(p => p.ContentJson).HasColumnName("content").HasColumnType("jsonb");
        builder.Property(p => p.SeoTitle).HasColumnType("text");
        builder.Property(p => p.SeoDescription).HasColumnType("text");
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.HasOne<AdminUser>().WithMany().HasForeignKey(p => p.UpdatedByAdminId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.Version).IsRowVersion();
        builder.Ignore(p => p.HasContent);
    }
}

internal sealed class NewsletterSubscriberConfiguration : IEntityTypeConfiguration<NewsletterSubscriber>
{
    public void Configure(EntityTypeBuilder<NewsletterSubscriber> builder)
    {
        builder.ToTable("newsletter_subscribers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Email).HasColumnType("citext").IsRequired();
        builder.HasIndex(s => s.Email).IsUnique();
        builder.Property(s => s.ConsentedAt).IsRequired();
        builder.Property(s => s.ConfirmationSentAt).IsRequired();
        builder.Property(s => s.ConfirmedAt);
        builder.Property(s => s.UnsubscribedAt);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Ignore(s => s.Status);
    }
}

internal sealed class ContactMessageConfiguration : IEntityTypeConfiguration<ContactMessage>
{
    public void Configure(EntityTypeBuilder<ContactMessage> builder)
    {
        builder.ToTable("contact_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Name).HasColumnType("text").IsRequired();
        builder.Property(m => m.Email).HasColumnType("citext").IsRequired();
        builder.Property(m => m.Message).HasColumnType("text").IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.HasIndex(m => m.CreatedAt).IsDescending();
        builder.HasOne<AdminUser>().WithMany().HasForeignKey(m => m.ReadByAdminId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        // No check constraint on the category: the list lives in ExpenseCategories and can change without a migration.
        builder.ToTable("expenses", t => t.HasCheckConstraint("ck_expenses_amount", "amount > 0"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ExpenseDate).HasColumnType("date").IsRequired();
        builder.HasIndex(e => e.ExpenseDate);
        builder.Property(e => e.Category).HasColumnType("text").IsRequired();
        builder.Property(e => e.Description).HasColumnType("text").IsRequired();
        builder.Property(e => e.Amount).HasColumnType("numeric(12,2)").IsRequired();
        builder.HasOne<AdminUser>().WithMany().HasForeignKey(e => e.CreatedByAdminId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}

internal sealed class SiteRebuildRequestConfiguration : IEntityTypeConfiguration<SiteRebuildRequest>
{
    public void Configure(EntityTypeBuilder<SiteRebuildRequest> builder)
    {
        builder.ToTable("site_rebuild_requests", t => t.HasCheckConstraint("ck_site_rebuild_requests_singleton", "id = 1"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Pending).IsRequired();
        builder.Property(r => r.LastResult).HasColumnType("text");
    }
}
