using Zladovce.Domain.Common;

namespace Zladovce.Domain.Finance;

/// <summary>One expense category: stored value and Serbian label.</summary>
public sealed record ExpenseCategory(string Value, string Label);

/// <summary>
/// The fixed list of expense categories (answer to open question 19 is pending). To change it, edit <see cref="All"/> only:
/// the column has no check constraint, so no migration is needed; values that are removed stay readable in old expenses
/// (their label falls back to the value).
/// </summary>
public static class ExpenseCategories
{
    public static readonly IReadOnlyList<ExpenseCategory> All =
    [
        new("Ambalaza", "Ambalaža"),
        new("Sirovine", "Sirovine"),
        new("Dostava", "Dostava"),
        new("Marketing", "Marketing"),
        new("Ostalo", "Ostalo"),
    ];

    public static bool IsValid(string? value) => All.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal));

    public static string LabelOf(string value) => All.FirstOrDefault(c => c.Value == value)?.Label ?? value;
}

/// <summary>An expense entered by the Owner (table <c>expenses</c>), used for "Troškovi" and "Procenjena dobit" (P1.8).</summary>
public sealed class Expense
{
    public const int MaxDescriptionLength = 500;

    private Expense()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Serbian local date the expense belongs to.</summary>
    public DateOnly ExpenseDate { get; private set; }
    public string Category { get; private set; } = null!;
    public string Description { get; private set; } = null!;

    /// <summary>RSD, more than 0, two decimals.</summary>
    public decimal Amount { get; private set; }
    public Guid CreatedByAdminId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Expense Create(DateOnly date, string category, string description, decimal amount, Guid adminId, DateTimeOffset now)
    {
        if (adminId == Guid.Empty)
        {
            throw new DomainException("An expense is entered by an administrator.");
        }

        var expense = new Expense { Id = Guid.CreateVersion7(now), CreatedByAdminId = adminId, CreatedAt = now };
        expense.Update(date, category, description, amount, now);
        return expense;
    }

    public void Update(DateOnly date, string category, string description, decimal amount, DateTimeOffset now)
    {
        if (!ExpenseCategories.IsValid(category))
        {
            throw new DomainException("Unknown expense category.");
        }

        var text = Text.NullIfBlank(description) ?? throw new DomainException("Description is required.");
        if (text.Length > MaxDescriptionLength)
        {
            throw new DomainException("Description is too long.");
        }

        if (amount <= 0 || Money.Round(amount) != amount || amount > 9_999_999_999.99m)
        {
            throw new DomainException("Amount must be positive with at most two decimals.");
        }

        ExpenseDate = date;
        Category = category;
        Description = text;
        Amount = amount;
        UpdatedAt = now;
    }
}
