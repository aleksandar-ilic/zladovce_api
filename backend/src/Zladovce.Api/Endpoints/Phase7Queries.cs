using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Application.Analytics;
using Zladovce.Application.Audit;
using Zladovce.Application.Common;
using Zladovce.Application.Content;
using Zladovce.Domain.Finance;
using Zladovce.Domain.Marketing;

namespace Zladovce.Api.Endpoints;

/// <summary>Period filter of the dashboard and the reports.</summary>
public class PeriodQuery : IPeriodQuery
{
    /// <summary>
    /// <c>today</c>, <c>yesterday</c>, <c>last7</c>, <c>last30</c> (default), <c>thisMonth</c>, <c>lastMonth</c>, <c>thisYear</c>,
    /// <c>custom</c> (with <c>from</c> and <c>to</c>). Days are Serbian local days (Europe/Belgrade).
    /// </summary>
    [FromQuery(Name = "period")]
    public string? Period { get; set; }

    /// <summary>First day (yyyy-MM-dd), <c>custom</c> only.</summary>
    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    /// <summary>Last day (yyyy-MM-dd), inclusive, <c>custom</c> only. At most three years after <c>from</c>.</summary>
    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }

    public AnalyticsPeriod Resolve(TimeProvider clock) => AnalyticsPeriod.Resolve(Period, From, To, clock.GetUtcNow());
}

public sealed class PeriodQueryValidator : AbstractValidator<PeriodQuery>
{
    public PeriodQueryValidator() => this.PeriodRules();
}

public sealed class TopProductsQuery : PeriodQuery
{
    /// <summary>1–50 (default 5) per list.</summary>
    [FromQuery(Name = "limit")]
    public int? Limit { get; set; }
}

public sealed class TopProductsQueryValidator : AbstractValidator<TopProductsQuery>
{
    public TopProductsQueryValidator()
    {
        this.PeriodRules();
        RuleFor(q => q.Limit).InclusiveBetween(1, 50).WithMessage("Broj stavki mora biti između 1 i 50.");
    }
}

public sealed class SalesReportQuery : PeriodQuery
{
    /// <summary><c>json</c> (default) or <c>csv</c>.</summary>
    [FromQuery(Name = "format")]
    public string? Format { get; set; }

    /// <summary>CSV only: <c>variant</c> (default, by product and variant) or <c>category</c>.</summary>
    [FromQuery(Name = "groupBy")]
    public string? GroupBy { get; set; }
}

public sealed class SalesReportQueryValidator : AbstractValidator<SalesReportQuery>
{
    public SalesReportQueryValidator()
    {
        this.PeriodRules();
        RuleFor(q => q.Format).OneOf("json", "csv");
        RuleFor(q => q.GroupBy).OneOf("variant", "category");
    }
}

public sealed class FormatQuery
{
    /// <summary><c>json</c> (default) or <c>csv</c>.</summary>
    [FromQuery(Name = "format")]
    public string? Format { get; set; }
}

public sealed class FormatQueryValidator : AbstractValidator<FormatQuery>
{
    public FormatQueryValidator() => RuleFor(q => q.Format).OneOf("json", "csv");
}

/// <summary>Query of <c>GET /api/v1/admin/expenses</c>.</summary>
public sealed class ExpenseListQuery : IPeriodQuery
{
    /// <summary>Same values as the dashboard; omitted = every expense.</summary>
    [FromQuery(Name = "period")]
    public string? Period { get; set; }

    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }

    /// <summary>One category value, e.g. <c>Ambalaza</c>.</summary>
    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public ExpenseListCriteria ToCriteria(TimeProvider clock) => new(
        Period is null ? null : AnalyticsPeriod.Resolve(Period, From, To, clock.GetUtcNow()),
        Category,
        PageRequest.From(Page, PageSize));
}

public sealed class ExpenseListQueryValidator : AbstractValidator<ExpenseListQuery>
{
    public ExpenseListQueryValidator()
    {
        this.PeriodRules();
        RuleFor(q => q.Category).Must(c => c is null || ExpenseCategories.IsValid(c))
            .WithMessage($"Dozvoljene kategorije: {string.Join(", ", ExpenseCategories.All.Select(c => c.Value))}.");
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Query of <c>GET /api/v1/admin/blog/posts</c>.</summary>
public sealed class AdminBlogPostListQuery
{
    /// <summary>Search in title and excerpt (ignores case and diacritics).</summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    /// <summary><c>Draft</c>, <c>Scheduled</c> or <c>Published</c>; omitted = all (archived posts are never listed).</summary>
    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    [FromQuery(Name = "categoryId")]
    public Guid? CategoryId { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public AdminBlogPostListCriteria ToCriteria() => new(
        Q, Status is null ? null : Enum.Parse<BlogPostState>(Status), CategoryId, PageRequest.From(Page, PageSize));
}

public sealed class AdminBlogPostListQueryValidator : AbstractValidator<AdminBlogPostListQuery>
{
    public AdminBlogPostListQueryValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        RuleFor(q => q.Status).OneOf(Enum.GetNames<BlogPostState>());
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Query of <c>GET /api/v1/blog/posts</c>.</summary>
public sealed class PublicBlogPostListQuery
{
    /// <summary>Blog category slug.</summary>
    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    /// <summary>1–100 (default 20).</summary>
    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }
}

public sealed class PublicBlogPostListQueryValidator : AbstractValidator<PublicBlogPostListQuery>
{
    public PublicBlogPostListQueryValidator()
    {
        RuleFor(q => q.Category).MaximumLength(120);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

public sealed class LimitQuery
{
    /// <summary>1–12 (default 3).</summary>
    [FromQuery(Name = "limit")]
    public int? Limit { get; set; }
}

public sealed class LimitQueryValidator : AbstractValidator<LimitQuery>
{
    public LimitQueryValidator() => RuleFor(q => q.Limit).InclusiveBetween(1, 12).WithMessage("Broj objava mora biti između 1 i 12.");
}

/// <summary>Query of <c>GET /api/v1/admin/newsletter/subscribers</c>.</summary>
public sealed class SubscriberListQuery
{
    /// <summary><c>Pending</c>, <c>Confirmed</c> or <c>Unsubscribed</c>; omitted = all.</summary>
    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>Part of the email address.</summary>
    [FromQuery(Name = "q")]
    public string? Q { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }
}

public sealed class SubscriberListQueryValidator : AbstractValidator<SubscriberListQuery>
{
    public SubscriberListQueryValidator()
    {
        RuleFor(q => q.Status).OneOf(Enum.GetNames<NewsletterSubscriberStatus>());
        RuleFor(q => q.Q).MaximumLength(ValidationRules.MaxSearchLength);
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}

/// <summary>Query of <c>GET /api/v1/admin/contact-messages</c>.</summary>
public sealed class ContactMessageListQuery
{
    /// <summary><c>true</c>: unread only.</summary>
    [FromQuery(Name = "unreadOnly")]
    public bool? UnreadOnly { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }
}

public sealed class ContactMessageListQueryValidator : AbstractValidator<ContactMessageListQuery>
{
    public ContactMessageListQueryValidator() => this.PagingRules(q => q.Page, q => q.PageSize);
}

/// <summary>Query of <c>GET /api/v1/admin/audit-logs</c>.</summary>
public sealed class AuditLogListQuery
{
    [FromQuery(Name = "adminUserId")]
    public Guid? AdminUserId { get; set; }

    /// <summary>e.g. <c>Product</c>, <c>Order</c>, <c>BlogPost</c> (see <c>GET /admin/audit-logs/filters</c>).</summary>
    [FromQuery(Name = "entityType")]
    public string? EntityType { get; set; }

    [FromQuery(Name = "entityId")]
    public string? EntityId { get; set; }

    /// <summary>e.g. <c>Product.Updated</c>, <c>Order.StatusChanged</c>.</summary>
    [FromQuery(Name = "action")]
    public string? Action { get; set; }

    /// <summary>First day (yyyy-MM-dd, Serbian local date), inclusive.</summary>
    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    /// <summary>Last day, inclusive.</summary>
    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public AuditLogCriteria ToCriteria() => new(AdminUserId, EntityType, EntityId, Action, From, To, PageRequest.From(Page, PageSize));
}

public sealed class AuditLogListQueryValidator : AbstractValidator<AuditLogListQuery>
{
    public AuditLogListQueryValidator()
    {
        RuleFor(q => q.EntityType).MaximumLength(100);
        RuleFor(q => q.EntityId).MaximumLength(200);
        RuleFor(q => q.Action).MaximumLength(100);
        RuleFor(q => q.To).Must((q, to) => to is null || q.From is null || to >= q.From).WithMessage("Datum „do” ne može biti pre datuma „od”.");
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}
