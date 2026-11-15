using Zladovce.Application.Common.Paging;
using Zladovce.Application.Identity;
using Zladovce.Application.StoreSettings;
using Zladovce.Domain.Identity;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.UnitTests.Application;

public class AdminRoleMapperTests
{
    [Fact]
    public void Owner_group_maps_to_owner() =>
        Assert.Equal(AdminRole.Owner, AdminRoleMapper.FromGroups(["Owner"]));

    [Fact]
    public void Staff_group_maps_to_staff() =>
        Assert.Equal(AdminRole.Staff, AdminRoleMapper.FromGroups(["Staff"]));

    [Theory]
    [InlineData("Staff", "Owner")]
    [InlineData("Owner", "Staff")]
    [InlineData("Other", "Owner")]
    public void Owner_wins_when_several_groups_are_present(string first, string second) =>
        Assert.Equal(AdminRole.Owner, AdminRoleMapper.FromGroups([first, second]));

    [Fact]
    public void Unknown_groups_are_ignored() =>
        Assert.Equal(AdminRole.Staff, AdminRoleMapper.FromGroups(["Customers", "Staff", "eu-central-1_Google"]));

    [Fact]
    public void No_administrator_group_means_no_role()
    {
        Assert.Null(AdminRoleMapper.FromGroups(null));
        Assert.Null(AdminRoleMapper.FromGroups([]));
        Assert.Null(AdminRoleMapper.FromGroups(["Customers"]));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("OWNER")]
    [InlineData("staff ")]
    [InlineData("Admin")]
    public void Group_names_are_matched_exactly(string group)
    {
        Assert.Null(AdminRoleMapper.FromGroups([group]));
        Assert.Null(AdminRoleMapper.FromName(group));
    }

    [Theory]
    [InlineData(AdminRole.Owner, "Owner")]
    [InlineData(AdminRole.Staff, "Staff")]
    public void Names_round_trip(AdminRole role, string name)
    {
        Assert.Equal(name, AdminRoleMapper.ToName(role));
        Assert.Equal(role, AdminRoleMapper.FromName(name));
    }
}

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(101, 100, 2)]
    public void Total_pages_is_rounded_up(int totalItems, int pageSize, int expectedPages)
    {
        var result = PagedResult<int>.Create([], 1, pageSize, totalItems);

        Assert.Equal(expectedPages, result.TotalPages);
        Assert.Equal(totalItems, result.TotalItems);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Page_and_page_size_are_bounded(int page, int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PagedResult<int>.Create([], page, pageSize, 0));
}

public class PublicStoreResponseTests
{
    [Fact]
    public void Maps_the_default_record_without_inventing_contact_data()
    {
        var settings = StoreSettingsRecord.CreateDefault(DateTimeOffset.UnixEpoch);

        var response = PublicStoreResponse.From(settings, new Zladovce.Application.Media.MediaOptions { BaseUrl = "https://cdn.example.test" });

        Assert.Equal("Zladovce", response.StoreName);
        Assert.Null(response.LogoKey);
        Assert.Null(response.Logo);
        Assert.Equal(new PublicStoreContact(null, null, null), response.Contact);
        Assert.Empty(response.Social.OtherLinks);
        Assert.True(response.Shipping.Enabled);
        Assert.Equal(0m, response.Shipping.Price);
        Assert.Null(response.Shipping.FreeShippingThreshold);
        Assert.Null(response.Seo.DefaultTitle);
    }
}
