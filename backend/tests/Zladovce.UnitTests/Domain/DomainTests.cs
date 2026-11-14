using Zladovce.Domain.Audit;
using Zladovce.Domain.Common;
using Zladovce.Domain.Identity;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.UnitTests.Domain;

public class StoreSettingsRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid AdminId = Guid.CreateVersion7();

    [Fact]
    public void Default_record_is_the_singleton_with_neutral_values()
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);

        Assert.Equal(1, settings.Id);
        Assert.Equal("Zladovce", settings.StoreName);
        Assert.True(settings.ShippingEnabled);
        Assert.Equal(0m, settings.ShippingPrice);
        Assert.Null(settings.FreeShippingThreshold);
        Assert.Null(settings.MinOrderAmount);
        Assert.Null(settings.ContactEmail);
        Assert.Null(settings.Phone);
        Assert.Null(settings.BusinessAddress);
        Assert.Empty(settings.OtherLinks);
        Assert.Null(settings.UpdatedByAdminId);
    }

    [Fact]
    public void Shipping_pricing_is_stored_with_the_administrator_and_time()
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);
        var later = Now.AddDays(1);

        settings.SetShippingPricing(true, 450.00m, 6000m, 1000m, AdminId, later);

        Assert.Equal(450.00m, settings.ShippingPrice);
        Assert.Equal(6000m, settings.FreeShippingThreshold);
        Assert.Equal(1000m, settings.MinOrderAmount);
        Assert.Equal(AdminId, settings.UpdatedByAdminId);
        Assert.Equal(later, settings.UpdatedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.005)]
    [InlineData(99999999999.99)]
    public void Shipping_price_must_be_non_negative_money(decimal price)
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);

        Assert.Throws<DomainException>(() => settings.SetShippingPricing(true, price, null, null, AdminId, Now));
        Assert.Equal(0m, settings.ShippingPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Thresholds_are_either_absent_or_positive(decimal value)
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);

        Assert.Throws<DomainException>(() => settings.SetShippingPricing(true, 300m, value, null, AdminId, Now));
        Assert.Throws<DomainException>(() => settings.SetShippingPricing(true, 300m, null, value, AdminId, Now));
    }

    [Fact]
    public void Changes_require_an_administrator()
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);

        Assert.Throws<DomainException>(() => settings.SetShippingPricing(true, 300m, null, null, Guid.Empty, Now));
        Assert.Throws<DomainException>(() => settings.Rename("Nova", Guid.Empty, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Store_name_is_required(string name)
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);

        Assert.Throws<DomainException>(() => settings.Rename(name, AdminId, Now));
        Assert.Equal("Zladovce", settings.StoreName);
    }

    [Theory]
    [InlineData(5999.99, 450)]
    [InlineData(6000, 0)]
    [InlineData(7500, 0)]
    public void Shipping_is_free_from_the_threshold(decimal subtotal, decimal expected)
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);
        settings.SetShippingPricing(true, 450m, 6000m, null, AdminId, Now);

        Assert.Equal(expected, settings.ShippingCostFor(subtotal));
    }

    [Fact]
    public void Without_a_threshold_shipping_is_always_charged()
    {
        var settings = StoreSettingsRecord.CreateDefault(Now);
        settings.SetShippingPricing(true, 450m, null, null, AdminId, Now);

        Assert.Equal(450m, settings.ShippingCostFor(1_000_000m));
        Assert.Throws<DomainException>(() => settings.ShippingCostFor(-1m));
    }
}

public class AdminUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_makes_an_active_admin_with_a_time_ordered_uuid_v7()
    {
        var admin = AdminUser.Create("sub-1", "  Ana@Example.com ", "Ana Anić", AdminRole.Owner, Now);

        Assert.Equal(7, admin.Id.Version);
        Assert.Equal("Ana@Example.com", admin.Email);
        Assert.Equal("Ana Anić", admin.FullName);
        Assert.Equal(AdminRole.Owner, admin.Role);
        Assert.True(admin.IsActive);
        Assert.Equal(Now, admin.LastLoginAt);
        Assert.Equal(Now, admin.CreatedAt);

        var later = AdminUser.Create("sub-2", "b@example.com", null, AdminRole.Staff, Now.AddSeconds(1));
        Assert.True(string.CompareOrdinal(admin.Id.ToString(), later.Id.ToString()) < 0);
    }

    [Fact]
    public void Full_name_falls_back_to_the_email()
    {
        var admin = AdminUser.Create("sub-1", "ana@example.com", "  ", AdminRole.Staff, Now);

        Assert.Equal("ana@example.com", admin.FullName);
    }

    [Theory]
    [InlineData("", "a@example.com")]
    [InlineData("sub", "")]
    [InlineData("sub", "not-an-email")]
    [InlineData("sub", "a@b@c")]
    [InlineData("sub", "a b@example.com")]
    public void Create_rejects_missing_subject_or_invalid_email(string subject, string email)
    {
        Assert.Throws<DomainException>(() => AdminUser.Create(subject, email, null, AdminRole.Staff, Now));
    }

    [Fact]
    public void Create_rejects_an_unknown_role()
    {
        Assert.Throws<DomainException>(() => AdminUser.Create("sub", "a@example.com", null, (AdminRole)99, Now));
    }

    [Fact]
    public void SyncRole_reports_whether_the_role_changed()
    {
        var admin = AdminUser.Create("sub", "a@example.com", null, AdminRole.Staff, Now);

        Assert.False(admin.SyncRole(AdminRole.Staff, Now.AddMinutes(1)));
        Assert.Equal(Now, admin.UpdatedAt);

        Assert.True(admin.SyncRole(AdminRole.Owner, Now.AddMinutes(2)));
        Assert.Equal(AdminRole.Owner, admin.Role);
        Assert.Equal(Now.AddMinutes(2), admin.UpdatedAt);
    }

    [Fact]
    public void TouchLogin_records_a_login_at_most_once_per_interval()
    {
        var admin = AdminUser.Create("sub", "a@example.com", null, AdminRole.Staff, Now);

        Assert.False(admin.TouchLogin(Now.AddMinutes(59)));
        Assert.Equal(Now, admin.LastLoginAt);

        Assert.True(admin.TouchLogin(Now.AddMinutes(60)));
        Assert.Equal(Now.AddMinutes(60), admin.LastLoginAt);
    }

    [Fact]
    public void Deactivate_and_activate_toggle_the_flag()
    {
        var admin = AdminUser.Create("sub", "a@example.com", null, AdminRole.Staff, Now);

        admin.Deactivate(Now.AddMinutes(1));
        Assert.False(admin.IsActive);

        admin.Activate(Now.AddMinutes(2));
        Assert.True(admin.IsActive);
        Assert.Equal(Now.AddMinutes(2), admin.UpdatedAt);
    }
}

public class AuditLogTests
{
    [Fact]
    public void Create_requires_an_action_and_assigns_a_uuid_v7()
    {
        var now = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var entry = AuditLog.Create("Auth.Login", null, "AdminUser", "1", """{"a":1}""", null, now);

        Assert.Equal(7, entry.Id.Version);
        Assert.Equal(now, entry.CreatedAt);
        Assert.Throws<DomainException>(() => AuditLog.Create(" ", null, null, null, null, null, now));
    }
}
