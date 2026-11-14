namespace Zladovce.Domain.Common;

/// <summary>
/// Serbian local time (Europe/Belgrade). Storage is UTC; the order-number year, date filters and reports use local time.
/// Uses the system time zone database and falls back to the EU rule (CET/CEST, last Sunday of March / October at 01:00 UTC)
/// when the host has no time zone data, so the result never depends on the container image.
/// </summary>
public static class SerbianTime
{
    public const string TimeZoneId = "Europe/Belgrade";

    public static TimeZoneInfo Zone { get; } = Resolve();

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Calendar year in Serbia at <paramref name="instant"/>.</summary>
    public static int YearOf(DateTimeOffset instant) => ToLocal(instant).Year;

    /// <summary>Calendar date in Serbia at <paramref name="instant"/>.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    /// <summary>The UTC instant at which <paramref name="date"/> starts in Serbia (local midnight).</summary>
    public static DateTimeOffset StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return CentralEuropean();
        }
    }

    /// <summary>The EU daylight saving rule for UTC+1, used only when the host has no time zone data.</summary>
    public static TimeZoneInfo CentralEuropean()
    {
        // 02:00 local standard time = 01:00 UTC (start); 03:00 local daylight time = 01:00 UTC (end).
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(TimeZoneId, TimeSpan.FromHours(1), "Central European Time", "CET", "CEST", [rule]);
    }
}
