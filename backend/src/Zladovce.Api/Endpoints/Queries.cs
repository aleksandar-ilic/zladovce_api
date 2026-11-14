using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Application.Catalog;
using Zladovce.Application.Common;
using Zladovce.Application.Inventory;
using Zladovce.Application.Orders;
using Zladovce.Domain.Inventory;
using Zladovce.Domain.Orders;

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
    /// <c>bestselling</c>: pieces sold in orders delivered in the last 90 days (configurable), ties newest first.
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

    /// <summary>
    /// <c>ok</c>, <c>low</c> or <c>out</c>; several values comma-separated (<c>status=low,out</c>) and/or repeated
    /// (<c>status=low&amp;status=out</c>) return rows in any of them; omitted = all. The dashboard low-stock list is
    /// exactly <c>status=low,out</c>.
    /// </summary>
    [FromQuery(Name = "status")]
    public string[]? Status { get; set; }

    /// <summary>The individual status values of <see cref="Status"/> (split at commas, trimmed, empty parts dropped).</summary>
    public IEnumerable<string> StatusValuesRequested() =>
        (Status ?? []).SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary><c>product</c> (default: product name, then variant order), <c>stockAsc</c>, <c>stockDesc</c>.</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public InventoryListCriteria ToCriteria() => new(
        Q,
        StatusValuesRequested()
            .Select(value => value switch { "ok" => StockStatus.Ok, "low" => StockStatus.Low, _ => StockStatus.Out })
            .ToHashSet(),
        Sort switch { "stockAsc" => InventorySort.StockAscending, "stockDesc" => InventorySort.StockDescending, _ => InventorySort.Product },
        PageRequest.From(Page, PageSize));
}

public sealed class InventoryListQueryValidator : AbstractValidator<InventoryListQuery>
{
    public InventoryListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.StatusValuesRequested())
            .Must(values => values.All(value => InventoryListQuery.StatusValues.Contains(value, StringComparer.Ordinal)))
            .OverridePropertyName("status")
            .WithMessage($"Dozvoljene vrednosti: {string.Join(", ", InventoryListQuery.StatusValues)}.");
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

/// <summary>Query of <c>GET /api/v1/admin/orders</c>.</summary>
public sealed class AdminOrderListQuery
{
    public static readonly string[] SortValues = ["newest", "oldest", "totalDesc", "totalAsc"];

    /// <summary>
    /// Search in order number, customer first and last name (ignores case and Serbian diacritics), email and phone
    /// ("064 123", "+381 64 123" and "64123" all find +381641234567).
    /// </summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    /// <summary>
    /// <c>Received</c>, <c>Accepted</c>, <c>Preparing</c>, <c>Shipped</c>, <c>Delivered</c>, <c>Cancelled</c>, <c>Rejected</c>,
    /// <c>DeliveryFailed</c>; several values comma-separated and/or repeated match any of them; omitted = all.
    /// </summary>
    [FromQuery(Name = "status")]
    public string[]? Status { get; set; }

    /// <summary>First day placed (yyyy-MM-dd, Serbian local date), inclusive.</summary>
    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    /// <summary>Last day placed (yyyy-MM-dd, Serbian local date), inclusive.</summary>
    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }

    /// <summary><c>newest</c> (default), <c>oldest</c>, <c>totalDesc</c>, <c>totalAsc</c>.</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public IEnumerable<string> StatusValuesRequested() =>
        (Status ?? []).SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public AdminOrderListCriteria ToCriteria() => new(
        Q,
        StatusValuesRequested().Select(Enum.Parse<OrderStatus>).ToHashSet(),
        From,
        To,
        Sort switch
        {
            "oldest" => AdminOrderSort.Oldest,
            "totalDesc" => AdminOrderSort.TotalDescending,
            "totalAsc" => AdminOrderSort.TotalAscending,
            _ => AdminOrderSort.Newest,
        },
        PageRequest.From(Page, PageSize));
}

public sealed class AdminOrderListQueryValidator : AbstractValidator<AdminOrderListQuery>
{
    private static readonly string[] Statuses = Enum.GetNames<OrderStatus>();

    public AdminOrderListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.StatusValuesRequested())
            .Must(values => values.All(value => Statuses.Contains(value, StringComparer.Ordinal)))
            .OverridePropertyName("status")
            .WithMessage($"Dozvoljene vrednosti: {string.Join(", ", Statuses)}.");
        RuleFor(q => q.To)
            .Must((query, to) => to is null || query.From is null || to >= query.From)
            .WithMessage("Datum „do” ne može biti pre datuma „od”.");
        RuleFor(q => q.Sort).OneOf(AdminOrderListQuery.SortValues);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}
