using FluentValidation;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Common;

namespace Zladovce.Application.Analytics;

/// <summary>Period filter values of the dashboard, the reports and the expense list.</summary>
public static class PeriodKeys
{
    public const string Today = "today";
    public const string Yesterday = "yesterday";
    public const string Last7 = "last7";
    public const string Last30 = "last30";
    public const string ThisMonth = "thisMonth";
    public const string LastMonth = "lastMonth";
    public const string ThisYear = "thisYear";
    public const string Custom = "custom";

    public static readonly string[] All = [Today, Yesterday, Last7, Last30, ThisMonth, LastMonth, ThisYear, Custom];

    /// <summary>Longest custom range (days).</summary>
    public const int MaxCustomDays = 3 * 366;
}

/// <summary>How series are bucketed.</summary>
public enum SeriesGranularity
{
    Day,
    Month,
}

/// <summary>
/// A period of Serbian local calendar days (inclusive) and the UTC interval it covers: from local midnight of
/// <see cref="From"/> to local midnight after <see cref="To"/> (23, 24 or 25 hours per day around DST changes).
/// </summary>
public sealed record DateRange(DateOnly From, DateOnly To)
{
    public DateTimeOffset StartUtc => SerbianTime.StartOfDay(From);

    /// <summary>Exclusive end.</summary>
    public DateTimeOffset EndUtc => SerbianTime.StartOfDay(To.AddDays(1));

    public int Days => To.DayNumber - From.DayNumber + 1;

    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;
}

/// <summary>A resolved period filter and the comparable previous period (for trend arrows).</summary>
/// <param name="Key">The requested key, e.g. <c>last7</c>.</param>
public sealed record AnalyticsPeriod(string Key, DateRange Current, DateRange Previous)
{
    /// <summary>Monthly buckets for <c>thisYear</c> and for ranges longer than 92 days, daily otherwise.</summary>
    public SeriesGranularity Granularity => Key == PeriodKeys.ThisYear || Current.Days > 92 ? SeriesGranularity.Month : SeriesGranularity.Day;

    /// <summary>
    /// Resolves a period at <paramref name="now"/> in Europe/Belgrade. Current periods end today (<c>thisMonth</c> = the 1st to
    /// today). Previous period: the same number of days immediately before for today, yesterday, last7, last30 and custom; for
    /// thisMonth the previous month from the 1st to the same day of the month (clamped to its last day); for lastMonth the month
    /// before it; for thisYear the previous year from 1 January to the same day (29 February → 28 February).
    /// </summary>
    public static AnalyticsPeriod Resolve(string? key, DateOnly? from, DateOnly? to, DateTimeOffset now)
    {
        var today = SerbianTime.DateOf(now);
        key ??= PeriodKeys.Last30;
        switch (key)
        {
            case PeriodKeys.Today:
                return Shifted(key, new DateRange(today, today));
            case PeriodKeys.Yesterday:
                return Shifted(key, new DateRange(today.AddDays(-1), today.AddDays(-1)));
            case PeriodKeys.Last7:
                return Shifted(key, new DateRange(today.AddDays(-6), today));
            case PeriodKeys.Last30:
                return Shifted(key, new DateRange(today.AddDays(-29), today));
            case PeriodKeys.ThisMonth:
            {
                var first = new DateOnly(today.Year, today.Month, 1);
                var previousFirst = first.AddMonths(-1);
                var previousTo = new DateOnly(previousFirst.Year, previousFirst.Month, Math.Min(today.Day, DateTime.DaysInMonth(previousFirst.Year, previousFirst.Month)));
                return new AnalyticsPeriod(key, new DateRange(first, today), new DateRange(previousFirst, previousTo));
            }

            case PeriodKeys.LastMonth:
            {
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                var before = first.AddMonths(-1);
                return new AnalyticsPeriod(key, new DateRange(first, first.AddMonths(1).AddDays(-1)), new DateRange(before, first.AddDays(-1)));
            }

            case PeriodKeys.ThisYear:
            {
                var first = new DateOnly(today.Year, 1, 1);
                var previousFirst = first.AddYears(-1);
                var previousTo = new DateOnly(today.Year - 1, today.Month, Math.Min(today.Day, DateTime.DaysInMonth(today.Year - 1, today.Month)));
                return new AnalyticsPeriod(key, new DateRange(first, today), new DateRange(previousFirst, previousTo));
            }

            case PeriodKeys.Custom:
                if (from is null || to is null)
                {
                    throw new RequestValidationException(from is null ? "from" : "to", "Za izabrani period unesite datume „od” i „do”.");
                }

                return Shifted(key, new DateRange(from.Value, to.Value));
            default:
                throw new RequestValidationException("period", $"Dozvoljene vrednosti: {string.Join(", ", PeriodKeys.All)}.");
        }
    }

    private static AnalyticsPeriod Shifted(string key, DateRange current) =>
        new(key, current, new DateRange(current.From.AddDays(-current.Days), current.From.AddDays(-1)));

    /// <summary>Buckets covering the current period: whole days, or calendar months cut to the period.</summary>
    public IReadOnlyList<DateRange> Buckets()
    {
        var buckets = new List<DateRange>();
        if (Granularity == SeriesGranularity.Day)
        {
            for (var day = Current.From; day <= Current.To; day = day.AddDays(1))
            {
                buckets.Add(new DateRange(day, day));
            }

            return buckets;
        }

        var start = Current.From;
        while (start <= Current.To)
        {
            var monthEnd = new DateOnly(start.Year, start.Month, 1).AddMonths(1).AddDays(-1);
            var end = monthEnd < Current.To ? monthEnd : Current.To;
            buckets.Add(new DateRange(start, end));
            start = end.AddDays(1);
        }

        return buckets;
    }
}

/// <summary>Shared query string of the analytics endpoints.</summary>
public interface IPeriodQuery
{
    string? Period { get; }
    DateOnly? From { get; }
    DateOnly? To { get; }
}

public static class PeriodQueryRules
{
    /// <summary><c>period</c> one of <see cref="PeriodKeys.All"/>; <c>from</c>/<c>to</c> required for custom, ordered, at most 3 years.</summary>
    public static void PeriodRules<T>(this AbstractValidator<T> validator)
        where T : IPeriodQuery
    {
        validator.RuleFor(q => q.Period)
            .Must(p => p is null || PeriodKeys.All.Contains(p, StringComparer.Ordinal))
            .WithMessage($"Dozvoljene vrednosti: {string.Join(", ", PeriodKeys.All)}.");
        validator.RuleFor(q => q.From).NotNull().When(q => q.Period == PeriodKeys.Custom)
            .WithMessage("Za izabrani period unesite datum „od”.");
        validator.RuleFor(q => q.To).NotNull().When(q => q.Period == PeriodKeys.Custom)
            .WithMessage("Za izabrani period unesite datum „do”.");
        validator.RuleFor(q => q.To)
            .Must((q, to) => to is null || q.From is null || to >= q.From)
            .WithMessage("Datum „do” ne može biti pre datuma „od”.");
        validator.RuleFor(q => q.To)
            .Must((q, to) => to is null || q.From is null || to.Value.DayNumber - q.From.Value.DayNumber < PeriodKeys.MaxCustomDays)
            .WithMessage("Period može trajati najviše tri godine.");
    }
}

/// <summary>Period as returned in responses.</summary>
/// <param name="From">First day (Serbian local date).</param>
/// <param name="To">Last day, inclusive.</param>
public sealed record PeriodRange(DateOnly From, DateOnly To);

/// <summary>Which period a response is about.</summary>
/// <param name="TimeZone">Always <c>Europe/Belgrade</c>: days start at local midnight.</param>
public sealed record PeriodInfo(string Period, PeriodRange Current, PeriodRange Previous, SeriesGranularity Granularity, string TimeZone)
{
    public static PeriodInfo From(AnalyticsPeriod p) => new(
        p.Key,
        new PeriodRange(p.Current.From, p.Current.To),
        new PeriodRange(p.Previous.From, p.Previous.To),
        p.Granularity,
        SerbianTime.TimeZoneId);
}
