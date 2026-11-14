using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;
using Zladovce.Application.Inventory;
using Zladovce.Domain.Inventory;

namespace Zladovce.Api.Endpoints;

/// <summary>Query of <c>GET /api/v1/admin/products</c>.</summary>
public sealed class AdminProductListQuery
{
    public static readonly string[] SortValues = ["newest", "oldest", "name", "nameDesc", "updated"];
    public static readonly string[] StatusValues = ["active", "inactive"];

    /// <summary>Search in product name, product SKU and variant SKUs; ignores case and diacritics ("dzem" finds "Džem").</summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    /// <summary>Category id.</summary>
    [FromQuery(Name = "category")]
    public Guid? Category { get; set; }

    /// <summary><c>active</c> or <c>inactive</c>; omitted = both.</summary>
    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary><c>true</c> lists archived products only; default lists products that are not archived.</summary>
    [FromQuery(Name = "archived")]
    public bool? Archived { get; set; }

    /// <summary><c>newest</c> (default), <c>oldest</c>, <c>name</c>, <c>nameDesc</c>, <c>updated</c>.</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    /// <summary>Page number from 1 (default 1).</summary>
    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    /// <summary>1–100 (default 20).</summary>
    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public AdminProductListCriteria ToCriteria() => new(
        Q,
        Category,
        Status switch { "active" => true, "inactive" => false, _ => null },
        Archived ?? false,
        Sort switch
        {
            "oldest" => AdminProductSort.Oldest,
            "name" => AdminProductSort.Name,
            "nameDesc" => AdminProductSort.NameDescending,
            "updated" => AdminProductSort.Updated,
            _ => AdminProductSort.Newest,
        },
        PageRequest.From(Page, PageSize));
}

public sealed class AdminProductListQueryValidator : AbstractValidator<AdminProductListQuery>
{
    public AdminProductListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.Status).OneOf(AdminProductListQuery.StatusValues);
        RuleFor(q => q.Sort).OneOf(AdminProductListQuery.SortValues);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Query of <c>GET /api/v1/catalog/products</c>.</summary>
public sealed class PublicProductListQuery
{
    public static readonly string[] SortValues = ["newest", "priceAsc", "priceDesc", "name", "bestselling"];

    /// <summary>Category slug.</summary>
    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    /// <summary>Search in product names; ignores case and diacritics.</summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    /// <summary>Lowest listing price (RSD), inclusive.</summary>
    [FromQuery(Name = "minPrice")]
    public decimal? MinPrice { get; set; }

    /// <summary>Highest listing price (RSD), inclusive.</summary>
    [FromQuery(Name = "maxPrice")]
    public decimal? MaxPrice { get; set; }

    /// <summary><c>true</c>: only products with at least one variant on stock.</summary>
    [FromQuery(Name = "inStock")]
    public bool? InStock { get; set; }

    /// <summary><c>true</c>: featured products only; <c>false</c>: not featured only.</summary>
    [FromQuery(Name = "featured")]
    public bool? Featured { get; set; }

    /// <summary>
    /// <c>newest</c> (default), <c>priceAsc</c>, <c>priceDesc</c>, <c>name</c>, <c>bestselling</c>.
    /// <c>bestselling</c> is ordered like <c>newest</c> until orders exist.
    /// </summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public PublicProductListCriteria ToCriteria() => new(
        Category,
        Q,
        MinPrice,
        MaxPrice,
        InStock,
        Featured,
        Sort switch
        {
            "priceAsc" => PublicProductSort.PriceAscending,
            "priceDesc" => PublicProductSort.PriceDescending,
            "name" => PublicProductSort.Name,
            "bestselling" => PublicProductSort.Bestselling,
            _ => PublicProductSort.Newest,
        },
        PageRequest.From(Page, PageSize));
}

public sealed class PublicProductListQueryValidator : AbstractValidator<PublicProductListQuery>
{
    public PublicProductListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.Category).MaximumLength(120);
        RuleFor(q => q.MinPrice).GreaterThanOrEqualTo(0);
        RuleFor(q => q.MaxPrice).GreaterThanOrEqualTo(0);
        RuleFor(q => q.MaxPrice)
            .Must((query, max) => max is null || query.MinPrice is null || max >= query.MinPrice)
            .WithMessage("Najviša cena ne može biti manja od najniže.");
        RuleFor(q => q.Sort).OneOf(PublicProductListQuery.SortValues);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Query of <c>GET /api/v1/admin/inventory</c>.</summary>
public sealed class InventoryListQuery
{
    public static readonly string[] SortValues = ["product", "stockAsc", "stockDesc"];
    public static readonly string[] StatusValues = ["ok", "low", "out"];

    /// <summary>Search in product name, variant name and SKU; ignores case and diacritics.</summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    /// <summary><c>ok</c>, <c>low</c> or <c>out</c>; omitted = all.</summary>
    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary><c>product</c> (default: product name, then variant order), <c>stockAsc</c>, <c>stockDesc</c>.</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public InventoryListCriteria ToCriteria() => new(
        Q,
        Status switch { "ok" => StockStatus.Ok, "low" => StockStatus.Low, "out" => StockStatus.Out, _ => null },
        Sort switch { "stockAsc" => InventorySort.StockAscending, "stockDesc" => InventorySort.StockDescending, _ => InventorySort.Product },
        PageRequest.From(Page, PageSize));
}

public sealed class InventoryListQueryValidator : AbstractValidator<InventoryListQuery>
{
    public InventoryListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.Status).OneOf(InventoryListQuery.StatusValues);
        RuleFor(q => q.Sort).OneOf(InventoryListQuery.SortValues);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Paging only (movement history).</summary>
public sealed class PageQuery
{
    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }
}

public sealed class PageQueryValidator : AbstractValidator<PageQuery>
{
    public PageQueryValidator() => this.PagingRules(q => q.Page, q => q.PageSize);
}
