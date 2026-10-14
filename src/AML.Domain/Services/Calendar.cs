namespace AML.Domain.Services;

/// <summary>Working-day calendar evaluated in the institution's time zone (weekends + public holidays).</summary>
public sealed class WorkingCalendar
{
    private readonly TimeZoneInfo _tz;
    private readonly HashSet<DayOfWeek> _weekend;
    private readonly HashSet<DateOnly> _holidays;

    public WorkingCalendar(TimeZoneInfo tz, IEnumerable<DayOfWeek> weekend, IEnumerable<DateOnly> holidays)
    {
        _tz = tz;
        _weekend = weekend.ToHashSet();
        _holidays = holidays.ToHashSet();
    }

    public static WorkingCalendar Create(string timeZoneId, string weekendCsv, IEnumerable<DateOnly> holidays) =>
        new(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId),
            weekendCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Enum.Parse<DayOfWeek>),
            holidays);

    public TimeZoneInfo TimeZone => _tz;

    public bool IsWorkingDay(DateOnly d) => !_weekend.Contains(d.DayOfWeek) && !_holidays.Contains(d);

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _tz));

    /// <summary>Adds N working days to the local date of <paramref name="startUtc"/>; due at end of that local day (UTC).</summary>
    public DateTime AddWorkingDays(DateTime startUtc, int days)
    {
        var d = LocalDate(startUtc);
        var added = 0;
        while (added < days)
        {
            d = d.AddDays(1);
            if (IsWorkingDay(d)) added++;
        }
        var endLocal = d.ToDateTime(new TimeOnly(23, 59, 59));
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), _tz);
    }

    public int WorkingDaysBetween(DateTime fromUtc, DateTime toUtc)
    {
        var a = LocalDate(fromUtc);
        var b = LocalDate(toUtc);
        var n = 0;
        for (var d = a.AddDays(1); d <= b; d = d.AddDays(1)) if (IsWorkingDay(d)) n++;
        return n;
    }
}

/// <summary>Effective-dated FX conversion. A missing rate is reported, never defaulted to 1.</summary>
public static class FxConverter
{
    public sealed record Rate(long Id, string From, string To, decimal Value, DateTime EffectiveFrom, int Version, string Source);
    public sealed record Conversion(string Status, decimal? BaseAmount, Rate? RateUsed);

    public static Conversion Convert(decimal amount, string currency, string baseCurrency, DateTime atUtc, IEnumerable<Rate> rates)
    {
        if (string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase))
            return new Conversion("SameCurrency", amount, null);
        var r = rates.Where(x => string.Equals(x.From, currency, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(x.To, baseCurrency, StringComparison.OrdinalIgnoreCase) && x.EffectiveFrom <= atUtc)
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Version).FirstOrDefault();
        if (r is not null) return new Conversion("Converted", Math.Round(amount * r.Value, 4, MidpointRounding.AwayFromZero), r);
        var inv = rates.Where(x => string.Equals(x.From, baseCurrency, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(x.To, currency, StringComparison.OrdinalIgnoreCase) && x.EffectiveFrom <= atUtc && x.Value != 0)
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Version).FirstOrDefault();
        if (inv is not null) return new Conversion("Converted", Math.Round(amount / inv.Value, 4, MidpointRounding.AwayFromZero), inv);
        return new Conversion("Missing", null, null);
    }
}
