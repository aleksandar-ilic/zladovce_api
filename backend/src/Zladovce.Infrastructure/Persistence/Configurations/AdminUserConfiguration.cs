using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zladovce.Domain.Identity;

namespace Zladovce.Infrastructure.Persistence.Configurations;

internal sealed class AdminUserConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> builder)
    {
        builder.ToTable("admin_users", t =>
            t.HasCheckConstraint("ck_admin_users_role", "role IN ('Owner', 'Staff')"));

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.CognitoSub).HasColumnType("text").IsRequired();
        builder.HasIndex(a => a.CognitoSub).IsUnique();

        builder.Property(a => a.Email).HasColumnType("citext").IsRequired();
        builder.HasIndex(a => a.Email).IsUnique();

        builder.Property(a => a.FullName).HasColumnType("text").IsRequired();
        builder.Property(a => a.Role).HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(a => a.IsActive).IsRequired();
        builder.Property(a => a.LastLoginAt);
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();
    }
}
