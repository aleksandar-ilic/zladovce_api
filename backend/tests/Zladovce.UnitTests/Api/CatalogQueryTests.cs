using System.Text.Json.Serialization.Metadata;
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
        Assert.Equal([expected], new InventoryListQuery { Status = [status] }.ToCriteria().Statuses);

    [Fact]
    public void Inventory_status_accepts_comma_separated_and_repeated_values()
    {
        var criteria = new InventoryListQuery { Status = ["low, out", "low", ""] }.ToCriteria();
        Assert.Equal(new HashSet<StockStatus> { StockStatus.Low, StockStatus.Out }, criteria.Statuses);
    }

    [Fact]
    public void Inventory_without_status_has_no_status_filter() =>
        Assert.Empty(new InventoryListQuery().ToCriteria().Statuses);

    [Theory]
    [InlineData("low,empty")]
    [InlineData("LOW")]
    public void Inventory_query_rejects_unknown_status_values(string status) =>
        Assert.Contains(new InventoryListQueryValidator().Validate(new InventoryListQuery { Status = [status] }).Errors, e => e.PropertyName == "status");

    [Fact]
    public void Inventory_query_accepts_several_known_status_values() =>
        Assert.True(new InventoryListQueryValidator().Validate(new InventoryListQuery { Status = ["low,out", "ok"] }).IsValid);

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

public class RequiredMembersTests
{
    private static System.Text.Json.JsonSerializerOptions Options()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            .WithAddedModifier(Zladovce.Api.Validation.RequiredMembers.Modifier);
        return options;
    }

    private static Dictionary<string, List<string>> Missing(object? model)
    {
        var errors = new Dictionary<string, List<string>>();
        Zladovce.Api.Validation.RequiredMembers.CollectMissing(model, string.Empty, errors);
        return errors;
    }

    [Fact]
    public void Missing_members_are_reported_with_nested_paths_and_null_counts_as_sent()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<Zladovce.Application.Catalog.CreateProductRequest>(
            """{ "categoryId": null, "name": "x", "sku": "X", "variants": [ { "name": "a", "sku": "A", "unitAmount": 1, "measurementUnit": "g", "regularPrice": 1, "discountPrice": null } ] }""",
            Options());

        Assert.Equal(
            ["variants[0].initialStock", "variants[0].isActive", "variants[0].lowStockThreshold"],
            Missing(request).Keys.Order());
    }

    [Fact]
    public void Empty_object_misses_every_required_member_and_optional_ones_are_not_reported()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<Zladovce.Application.Catalog.AttachProductImageRequest>("{}", Options());
        Assert.Equal(["uploadKey"], Missing(request).Keys);
    }

    [Fact]
    public void Objects_not_created_by_the_deserializer_are_not_checked() =>
        Assert.Empty(Missing(new Zladovce.Application.Inventory.SetLowStockThresholdRequest { LowStockThreshold = 1 }));
}

