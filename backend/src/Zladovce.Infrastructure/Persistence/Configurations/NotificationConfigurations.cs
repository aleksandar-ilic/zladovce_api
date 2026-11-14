using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Tracking;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class OrderTrackingTokenConfiguration : IEntityTypeConfiguration<OrderTrackingToken>
{
    public void Configure(EntityTypeBuilder<OrderTrackingToken> builder)
    {
        builder.ToTable("order_tracking_tokens", t => t.HasCheckConstraint("ck_order_tracking_tokens_token_hash", "octet_length(token_hash) = 32"));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasOne<Order>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => t.OrderId);
        builder.Property(t => t.TokenHash).HasColumnType("bytea").IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.RevokedAt);
        builder.Property(t => t.LastUsedAt);
    }
}

internal sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public static readonly string StatusValues = string.Join(", ", Enum.GetNames<EmailOutboxStatus>().Select(n => $"'{n}'"));
    public static readonly string TemplateValues = string.Join(", ", Enum.GetNames<EmailTemplate>().Select(n => $"'{n}'"));

    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.ToTable("email_outbox", t =>
        {
            t.HasCheckConstraint("ck_email_outbox_status", $"status IN ({StatusValues})");
            t.HasCheckConstraint("ck_email_outbox_template", $"template IN ({TemplateValues})");
            t.HasCheckConstraint("ck_email_outbox_attempt_count", "attempt_count >= 0");
            t.HasCheckConstraint("ck_email_outbox_sent", "(status = 'Sent') = (sent_at IS NOT NULL)");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.DedupKey).HasColumnType("text").IsRequired();
        builder.HasIndex(m => m.DedupKey).IsUnique();
        builder.Property(m => m.Template).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.HasOne<Order>().WithMany().HasForeignKey(m => m.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.OrderId, m.CreatedAt });
        builder.Property(m => m.Recipient).HasColumnType("citext").IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.EncryptedToken).HasColumnType("bytea");
        builder.Property(m => m.Status).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(m => m.AttemptCount).IsRequired();
        builder.Property(m => m.NextAttemptAt).IsRequired();
        builder.Property(m => m.LockedUntil);
        builder.Property(m => m.LastError).HasColumnType("text");
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.SentAt);

        // The worker's queue: only rows that can still be sent are in the index.
        builder.HasIndex(m => m.NextAttemptAt)
            .HasDatabaseName("ix_email_outbox_due")
            .HasFilter("status IN ('Pending', 'Processing')");

        // Admin list: newest first, optionally by status.
        builder.HasIndex(m => new { m.Status, m.CreatedAt }).IsDescending(false, true);
    }
}

internal sealed class EmailLogConfiguration : IEntityTypeConfiguration<EmailLog>
{
    public void Configure(EntityTypeBuilder<EmailLog> builder)
    {
        builder.ToTable("email_logs", t => t.HasCheckConstraint("ck_email_logs_attempt_number", "attempt_number > 0"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.HasOne<EmailOutboxMessage>().WithMany().HasForeignKey(l => l.OutboxId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => l.OutboxId);
        builder.Property(l => l.AttemptNumber).IsRequired();
        builder.Property(l => l.Succeeded).IsRequired();
        builder.Property(l => l.SesMessageId).HasColumnType("text");
        builder.Property(l => l.Error).HasColumnType("text");
        builder.Property(l => l.CreatedAt).IsRequired();
    }
}
