using System.Globalization;

namespace MadeeAutomation.Domain.Regional;

/// <summary>
/// The days a report covers, in one time zone (the client's own, or the admin's for the MadeeVision report).
/// From and To are whole local days, both included. FromUtc / ToUtc are the matching moments
/// (ToUtc = midnight after the last day, not included) for database queries.
/// Nothing here reads the clock: "today" is passed in, so it can be checked exactly.
/// </summary>
public sealed class ReportPeriod
{
    public const string Today = "today";
    public const string Last7 = "7d";
    public const string ThisMonth = "this-month";
    public const string LastMonth = "last-month";
    public const string Last30 = "30d";
    public const string Custom = "custom";

    /// <summary>The choices shown above every report, in order.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Presets = new[]
    {
        (Today, "Today"), (Last7, "Last 7 days"), (ThisMonth, "This month"), (LastMonth, "Last month"), (Last30, "Last 30 days")
    };

    /// <summary>The longest custom range (so one report can't ask for years of rows).</summary>
    public const int MaxDays = 366;

    public string Key { get; }
    public DateOnly From { get; }
    public DateOnly To { get; }
    public string? TimeZoneId { get; }

    private ReportPeriod(string key, DateOnly from, DateOnly to, string? timeZoneId)
    {
        Key = key;
        From = from;
        To = to;
        TimeZoneId = timeZoneId;
    }

    public DateTime FromUtc => LocalTime.FromLocal(From.ToDateTime(TimeOnly.MinValue), TimeZoneId).UtcDateTime;
    public DateTime ToUtc => LocalTime.FromLocal(To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneId).UtcDateTime;
    public int Days => To.DayNumber - From.DayNumber + 1;

    public IEnumerable<DateOnly> Dates()
    {
        for (var d = From; d <= To; d = d.AddDays(1)) yield return d;
    }

    /// <summary>"1 Nov 2026 - 13 Nov 2026", or one date for a single day.</summary>
    public string Label => From == To
        ? From.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
        : From.Year == To.Year
            ? $"{From.ToString("d MMM", CultureInfo.InvariantCulture)} - {To.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : $"{From.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} - {To.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>For file names: 2026-11-01_to_2026-11-30.</summary>
    public string FileLabel => $"{From:yyyy-MM-dd}_to_{To:yyyy-MM-dd}";

    /// <summary>
    /// The period to compare with: the month before for the month choices (1-13 Nov compares with 1-13 Oct),
    /// otherwise the same number of days just before.
    /// </summary>
    public ReportPeriod Previous()
    {
        if (Key == LastMonth || (Key == ThisMonth && From.Day == 1))
        {
            var start = From.AddMonths(-1);
            var length = Key == LastMonth ? DateTime.DaysInMonth(start.Year, start.Month) : Math.Min(Days, DateTime.DaysInMonth(start.Year, start.Month));
            return new ReportPeriod(Custom, start, start.AddDays(length - 1), TimeZoneId);
        }
        return new ReportPeriod(Custom, From.AddDays(-Days), From.AddDays(-1), TimeZoneId);
    }

    /// <summary>
    /// Works out the period from what the page sent. Anything missing or wrong falls back to "this month".
    /// error explains a custom range that could not be used (shown above the report).
    /// </summary>
    public static ReportPeriod Resolve(string? key, DateOnly? from, DateOnly? to, DateOnly today, string? timeZoneId, out string? error)
    {
        error = null;
        switch (key)
        {
            case Today: return new ReportPeriod(Today, today, today, timeZoneId);
            case Last7: return new ReportPeriod(Last7, today.AddDays(-6), today, timeZoneId);
            case Last30: return new ReportPeriod(Last30, today.AddDays(-29), today, timeZoneId);
            case LastMonth:
            {
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return new ReportPeriod(LastMonth, first, first.AddMonths(1).AddDays(-1), timeZoneId);
            }
            case Custom when from is { } f && to is { } t:
                if (t < f) { error = "The end date is before the start date - showing this month instead."; break; }
                if (t.DayNumber - f.DayNumber + 1 > MaxDays) { error = $"Please choose at most {MaxDays} days - showing this month instead."; break; }
                return new ReportPeriod(Custom, f, t, timeZoneId);
            case Custom:
                error = "Choose both a start and an end date - showing this month instead.";
                break;
        }
        return new ReportPeriod(ThisMonth, new DateOnly(today.Year, today.Month, 1), today, timeZoneId);
    }

    /// <summary>A whole calendar month (the monthly email).</summary>
    public static ReportPeriod Month(int year, int month, string? timeZoneId)
    {
        var first = new DateOnly(year, month, 1);
        return new ReportPeriod(LastMonth, first, first.AddMonths(1).AddDays(-1), timeZoneId);
    }
}
