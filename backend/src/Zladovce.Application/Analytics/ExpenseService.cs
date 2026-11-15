using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Common;
using Zladovce.Domain.Finance;

namespace Zladovce.Application.Analytics;

/// <summary>Body of <c>POST /api/v1/admin/expenses</c> and <c>PUT /api/v1/admin/expenses/{id}</c>. Every member must be sent.</summary>
public sealed record SaveExpenseRequest
{
    /// <summary>Day of the expense (yyyy-MM-dd, Serbian local date).</summary>
    public required DateOnly? ExpenseDate { get; init; }

    /// <summary>One of <c>GET /admin/expenses/categories</c>: <c>Ambalaza</c>, <c>Sirovine</c>, <c>Dostava</c>, <c>Marketing</c>, <c>Ostalo</c>.</summary>
    public required string? Category { get; init; }

    /// <summary>1–500 characters.</summary>
    public required string? Description { get; init; }

    /// <summary>RSD, more than 0, at most two decimals.</summary>
    public required decimal? Amount { get; init; }
}

public sealed class SaveExpenseRequestValidator : AbstractValidator<SaveExpenseRequest>
{
    public SaveExpenseRequestValidator()
    {
        RuleFor(r => r.ExpenseDate).NotNull().WithMessage("Datum je obavezan.");
        RuleFor(r => r.Category)
            .NotNull().WithMessage("Kategorija je obavezna.")
            .Must(c => c is null || ExpenseCategories.IsValid(c))
            .WithMessage($"Dozvoljene kategorije: {string.Join(", ", ExpenseCategories.All.Select(c => c.Value))}.");
        RuleFor(r => r.Description)
            .Cascade(CascadeMode.Stop)
            .Must(d => !string.IsNullOrWhiteSpace(d)).WithMessage("Opis je obavezan.")
            .Must(d => d!.Trim().Length <= Expense.MaxDescriptionLength).WithMessage($"Opis može imati najviše {Expense.MaxDescriptionLength} znakova.");
        RuleFor(r => r.Amount)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Iznos je obavezan.")
            .GreaterThan(0).WithMessage("Iznos mora biti veći od nule.")
            .Must(a => Pricing.HasAtMostTwoDecimals(a!.Value) && a.Value <= Pricing.MaxPrice).WithMessage("Iznos može imati najviše dve decimale.");
    }
}

/// <param name="CategoryLabel">Serbian label, e.g. "Ambalaža".</param>
/// <param name="CreatedByAdminName">Who entered it.</param>
public sealed record ExpenseResponse(
    Guid Id,
    DateOnly ExpenseDate,
    string Category,
    string CategoryLabel,
    string Description,
    decimal Amount,
    Guid CreatedByAdminId,
    string? CreatedByAdminName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ExpenseCategoryTotal(string Category, string CategoryLabel, decimal Amount, int Count);

/// <summary>Response of <c>GET /api/v1/admin/expenses</c>.</summary>
/// <param name="Period">The filter, or null when all expenses are listed.</param>
/// <param name="Totals">Every category of the fixed list (zero included) plus categories of old expenses no longer in the list.</param>
public sealed record ExpenseListResponse(PeriodInfo? Period, PagedResult<ExpenseResponse> Expenses, decimal Total, IReadOnlyList<ExpenseCategoryTotal> Totals, string Currency);

public sealed record ExpenseCategoryOption(string Value, string Label);

/// <param name="Period">Null = every expense.</param>
/// <param name="Category">Only this category.</param>
public sealed record ExpenseListCriteria(AnalyticsPeriod? Period, string? Category, PageRequest Page);

public interface IExpenseService
{
    IReadOnlyList<ExpenseCategoryOption> Categories();
    Task<ExpenseListResponse> ListAsync(ExpenseListCriteria criteria, CancellationToken cancellationToken);
    Task<ExpenseResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ExpenseResponse> CreateAsync(SaveExpenseRequest request, CancellationToken cancellationToken);
    Task<ExpenseResponse> UpdateAsync(Guid id, SaveExpenseRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Expenses (Owner). Changes are audited by the interceptor (<c>Expense.Created/Updated/Deleted</c>).</summary>
public sealed class ExpenseService(IZladovceDbContext db, IRequestContext requestContext, TimeProvider timeProvider) : IExpenseService
{
    public IReadOnlyList<ExpenseCategoryOption> Categories() => [.. ExpenseCategories.All.Select(c => new ExpenseCategoryOption(c.Value, c.Label))];

    public async Task<ExpenseListResponse> ListAsync(ExpenseListCriteria criteria, CancellationToken cancellationToken)
    {
        var query = db.Expenses.AsNoTracking();
        if (criteria.Period is { } period)
        {
            var (from, to) = (period.Current.From, period.Current.To);
            query = query.Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to);
        }

        if (criteria.Category is { } category)
        {
            query = query.Where(e => e.Category == category);
        }

        var sums = await query.GroupBy(e => e.Category)
            .Select(g => new { Category = g.Key, Amount = g.Sum(e => e.Amount), Count = g.Count() })
            .ToListAsync(cancellationToken);
        var totals = ExpenseCategories.All
            .Where(c => criteria.Category is null || c.Value == criteria.Category)
            .Select(c => new ExpenseCategoryTotal(c.Value, c.Label, DashboardService.M(sums.FirstOrDefault(s => s.Category == c.Value)?.Amount ?? 0m), sums.FirstOrDefault(s => s.Category == c.Value)?.Count ?? 0))
            .Concat(sums.Where(s => !ExpenseCategories.IsValid(s.Category)).Select(s => new ExpenseCategoryTotal(s.Category, s.Category, s.Amount, s.Count)))
            .ToList();

        var count = sums.Sum(s => s.Count);
        var items = await Project(query.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize))
            .ToListAsync(cancellationToken);

        return new ExpenseListResponse(
            criteria.Period is null ? null : PeriodInfo.From(criteria.Period),
            PagedResult<ExpenseResponse>.Create([.. items.Select(Finish)], criteria.Page.Page, criteria.Page.PageSize, count),
            DashboardService.M(totals.Sum(t => t.Amount)),
            totals,
            Money.Currency);
    }

    public async Task<ExpenseResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Finish(await Project(db.Expenses.AsNoTracking().Where(e => e.Id == id)).SingleOrDefaultAsync(cancellationToken) ?? throw new NotFoundException());

    public async Task<ExpenseResponse> CreateAsync(SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        var expense = Expense.Create(request.ExpenseDate!.Value, request.Category!, request.Description!, request.Amount!.Value, admin.Id, timeProvider.GetUtcNow());
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(expense.Id, cancellationToken);
    }

    public async Task<ExpenseResponse> UpdateAsync(Guid id, SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == id, cancellationToken) ?? throw new NotFoundException();
        expense.Update(request.ExpenseDate!.Value, request.Category!, request.Description!, request.Amount!.Value, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == id, cancellationToken) ?? throw new NotFoundException();
        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<ExpenseResponse> Project(IQueryable<Expense> query) =>
        from e in query
        join a in db.AdminUsers on e.CreatedByAdminId equals a.Id into admins
        from a in admins.DefaultIfEmpty()
        select new ExpenseResponse(e.Id, e.ExpenseDate, e.Category, e.Category, e.Description, e.Amount, e.CreatedByAdminId,
            a != null ? a.FullName : null, e.CreatedAt, e.UpdatedAt);

    private static ExpenseResponse Finish(ExpenseResponse response) => response with { CategoryLabel = ExpenseCategories.LabelOf(response.Category) };
}
