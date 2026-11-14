using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Domain.Common;

namespace Zladovce.Application.Common;

/// <summary>
/// Database functions usable in LINQ queries. They are translated to SQL by the persistence layer and
/// cannot be called in memory.
/// </summary>
public static class DbSearch
{
    /// <summary>PostgreSQL <c>zladovce_unaccent(text)</c>: removes diacritics ("Džem" → "Dzem").</summary>
    public static string Unaccent(string value) =>
        throw new InvalidOperationException("DbSearch.Unaccent can only be used inside a database query.");

    /// <summary>Escapes the LIKE wildcards of a user-entered search term.</summary>
    public static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>The trimmed search term as a "contains" pattern, or null when there is nothing to search for.</summary>
    public static string? ContainsPattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLike(trimmed)}%";
    }
}

/// <summary>Paging values of a list request, after defaults and limits were applied.</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public int Skip => (Page - 1) * PageSize;

    public static PageRequest From(int? page, int? pageSize) =>
        new(page ?? 1, pageSize ?? PagedResult<object>.DefaultPageSize);
}

public static class ValidationRules
{
    public const int MaxSearchLength = 100;

    public static void PagingRules<T>(this AbstractValidator<T> validator, Func<T, int?> page, Func<T, int?> pageSize)
    {
        validator.RuleFor(r => page(r)).GreaterThanOrEqualTo(1).OverridePropertyName("page")
            .WithMessage("Strana mora biti 1 ili veća.");
        validator.RuleFor(r => pageSize(r)).InclusiveBetween(1, PagedResult<object>.MaxPageSize).OverridePropertyName("pageSize")
            .WithMessage($"Veličina strane mora biti između 1 i {PagedResult<object>.MaxPageSize}.");
    }

    public static IRuleBuilderOptions<T, string?> OneOf<T>(this IRuleBuilder<T, string?> rule, params string[] allowed) =>
        rule.Must(value => value is null || allowed.Contains(value, StringComparer.Ordinal))
            .WithMessage($"Dozvoljene vrednosti: {string.Join(", ", allowed)}.");

    public static IRuleBuilderOptions<T, string?> ValidSlug<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => string.IsNullOrEmpty(value) || Slug.IsValid(value))
            .WithMessage("Adresa (slug) sme da sadrži samo mala slova bez kvačica, cifre i crtice.");
}

/// <summary>Chooses the slug of a new or renamed record.</summary>
public static class SlugAllocator
{
    private const int MaxAttempts = 50;

    /// <summary>
    /// An explicitly requested slug must be free (409 <c>SLUG_TAKEN</c> otherwise). Without one, the slug is
    /// generated from the name and gets a numeric suffix when it is already used.
    /// </summary>
    /// <param name="requested">Slug entered by the administrator, if any (already validated for format).</param>
    /// <param name="name">Name to derive the slug from.</param>
    /// <param name="isTaken">Whether a slug is used by another record that is not archived.</param>
    public static async Task<string> AllocateAsync(string? requested, string name, Func<string, Task<bool>> isTaken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (await isTaken(requested))
            {
                throw SlugTaken();
            }

            return requested;
        }

        var generated = Slug.Generate(name);
        if (generated.Length == 0)
        {
            throw new RequestValidationException("name", "Naziv mora da sadrži bar jedno slovo ili cifru.");
        }

        if (!await isTaken(generated))
        {
            return generated;
        }

        for (var suffix = 2; suffix <= MaxAttempts; suffix++)
        {
            var ending = $"-{suffix}";
            var stem = generated.Length + ending.Length > Slug.MaxLength ? generated[..(Slug.MaxLength - ending.Length)].TrimEnd('-') : generated;
            var candidate = stem + ending;
            if (!await isTaken(candidate))
            {
                return candidate;
            }
        }

        throw SlugTaken();
    }

    /// <summary>409 <c>SLUG_TAKEN</c> with <c>errors.slug</c>.</summary>
    public static ConflictException SlugTaken() => new(
        ErrorTexts.SlugTakenDetail,
        ErrorCodes.SlugTaken,
        ErrorTexts.SlugTakenTitle,
        new Dictionary<string, string[]> { ["slug"] = [ErrorTexts.SlugTakenDetail] });
}
