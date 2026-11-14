using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Audit;
using Zladovce.Domain.Identity;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(a => a.AdminUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(a => a.Action).HasColumnType("text").IsRequired();
        builder.Property(a => a.EntityType).HasColumnType("text");
        builder.Property(a => a.EntityId).HasColumnType("text");
        builder.Property(a => a.Changes).HasColumnType("jsonb");
        builder.Property(a => a.IpAddress).HasColumnType("inet");
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.CreatedAt);
    }
}
