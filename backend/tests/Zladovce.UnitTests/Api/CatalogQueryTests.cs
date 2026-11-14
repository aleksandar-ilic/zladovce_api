using Zladovce.Api.Endpoints;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Inventory;
using Zladovce.Domain.Inventory;

namespace Zladovce.UnitTests.Api;

public class CatalogQueryTests
{
    [Theory]
    [InlineData(null, PublicProductSort.Newest)]
    [InlineData("priceAsc", PublicProductSort.PriceAscending)]
    [InlineData("priceDesc", PublicProductSort.PriceDescending)]
    [InlineData("name", PublicProductSort.Name)]
    [InlineData("bestselling", PublicProductSort.Bestselling)]
    public void Public_sort_values_map(string? sort, PublicProductSort expected) =>
        Assert.Equal(expected, new PublicProductListQuery { Sort = sort }.ToCriteria().Sort);

    [Fact]
    public void Public_query_rejects_unknown_sort_and_inverted_price_range()
    {
        var result = new PublicProductListQueryValidator().Validate(new PublicProductListQuery { Sort = "random", MinPrice = 500, MaxPrice = 100, PageSize = 1000 });

        Assert.Contains(result.Errors, e => e.PropertyName == "Sort");
        Assert.Contains(result.Errors, e => e.PropertyName == "MaxPrice");
        Assert.Contains(result.Errors, e => e.PropertyName == "pageSize");
    }

    [Fact]
    public void Admin_status_and_archived_map_to_criteria()
    {
        var criteria = new AdminProductListQuery { Status = "inactive", Archived = true, Sort = "nameDesc", Page = 2, PageSize = 10 }.ToCriteria();

        Assert.False(criteria.IsActive);
        Assert.True(criteria.Archived);
        Assert.Equal(AdminProductSort.NameDescending, criteria.Sort);
        Assert.Equal(10, criteria.Page.Skip);
    }

    [Fact]
    public void Admin_query_rejects_unknown_status() =>
        Assert.Contains(new AdminProductListQueryValidator().Validate(new AdminProductListQuery { Status = "deleted" }).Errors, e => e.PropertyName == "Status");

    [Theory]
    [InlineData("ok", StockStatus.Ok)]
    [InlineData("low", StockStatus.Low)]
    [InlineData("out", StockStatus.Out)]
    public void Inventory_status_maps(string status, StockStatus expected) =>
        Assert.Equal(expected, new InventoryListQuery { Status = status }.ToCriteria().Status);

    [Fact]
    public void Inventory_sort_maps() =>
        Assert.Equal(InventorySort.StockAscending, new InventoryListQuery { Sort = "stockAsc" }.ToCriteria().Sort);

    [Fact]
    public void Variant_ids_accept_repeated_and_comma_separated_values()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var ids = PublicCatalogEndpoints.ParseVariantIds([$"{a},{b}", a.ToString()]);

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Variant_ids_must_be_valid_and_limited()
    {
        Assert.Throws<RequestValidationException>(() => PublicCatalogEndpoints.ParseVariantIds(null));
        Assert.Throws<RequestValidationException>(() => PublicCatalogEndpoints.ParseVariantIds(["nope"]));
        Assert.Throws<RequestValidationException>(() =>
            PublicCatalogEndpoints.ParseVariantIds([string.Join(',', Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()))]));
    }
}
