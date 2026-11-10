using System.Globalization;
using System.Text;
using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Domain.Regional;

public record BusinessStatus(bool HoursConfigured, bool IsOpen, string StatusText, string WeekText, string? ClosedDatesText);

/// <summary>
/// Works out "are we open right now?" from the client's opening hours, closed dates and time zone.
/// Done by our code - exact for every country - and the AI is simply told the answer.
/// </summary>
public static class BusinessClock
{
    private static readonly DayOfWeek[] MondayFirst =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    public static string Short(DayOfWeek d) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d);
    public static string T(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static BusinessStatus GetStatus(IReadOnlyCollection<OpeningHours> hours, IReadOnlyCollection<ClosedDate> closedDates,
        string? timeZoneId, DateTime utcNow)
    {
        if (hours.Count == 0)
            return new BusinessStatus(false, false, "Opening hours are not set.", "", null);

        var local = LocalTime.ToLocal(utcNow, timeZoneId);
        var today = DateOnly.FromDateTime(local);
        var now = TimeOnly.FromDateTime(local);
        var closed = closedDates.Select(c => c.Date).ToHashSet();

        bool overnight(OpeningHours h) => h.CloseTime <= h.OpenTime;

        // Open now: a period that started today, or last night's overnight period still running.
        OpeningHours? current = hours.FirstOrDefault(h =>
                h.DayOfWeek == local.DayOfWeek && !closed.Contains(today) &&
                now >= h.OpenTime && (overnight(h) || now < h.CloseTime))
            ?? hours.FirstOrDefault(h =>
                h.DayOfWeek == local.AddDays(-1).DayOfWeek && overnight(h) && !closed.Contains(today.AddDays(-1)) &&
                now < h.CloseTime);

        string status;
        if (current is not null)
        {
            status = $"OPEN now (closes at {T(current.CloseTime)}).";
        }
        else
        {
            var next = NextOpening(hours, closed, local);
            var why = closed.Contains(today) ? "closed today" : "CLOSED now";
            status = next is null
                ? $"{char.ToUpper(why[0]) + why[1..]}."
                : $"{char.ToUpper(why[0]) + why[1..]} (opens again {Describe(next.Value, local)}).";
        }

        var week = new StringBuilder();
        foreach (var d in MondayFirst)
        {
            var periods = hours.Where(h => h.DayOfWeek == d).OrderBy(h => h.OpenTime).ToList();
            week.Append(Short(d)).Append(' ')
                .Append(periods.Count == 0 ? "closed" : string.Join(" and ", periods.Select(p => $"{T(p.OpenTime)}-{T(p.CloseTime)}")))
                .Append("; ");
        }

        var upcoming = closedDates.Where(c => c.Date >= today && c.Date <= today.AddDays(60)).OrderBy(c => c.Date).ToList();
        var closedText = upcoming.Count == 0
            ? null
            : string.Join("; ", upcoming.Select(c =>
                $"{c.Date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(c.Reason) ? "" : " (" + c.Reason + ")")}"));

        return new BusinessStatus(true, current is not null, status, week.ToString().TrimEnd(' ', ';'), closedText);
    }

    /// <summary>The next moment the business opens, in local time (looks up to 2 weeks ahead).</summary>
    public static DateTime? NextOpening(IReadOnlyCollection<OpeningHours> hours, ISet<DateOnly> closed, DateTime local)
    {
        for (var i = 0; i <= 14; i++)
        {
            var day = DateOnly.FromDateTime(local).AddDays(i);
            if (closed.Contains(day)) continue;
            foreach (var h in hours.Where(h => h.DayOfWeek == day.DayOfWeek).OrderBy(h => h.OpenTime))
            {
                var start = day.ToDateTime(h.OpenTime);
                if (start > local) return start;
            }
        }
        return null;
    }

    private static string Describe(DateTime when, DateTime local)
    {
        var day = DateOnly.FromDateTime(when);
        var today = DateOnly.FromDateTime(local);
        var dayText = day == today ? "today"
            : day == today.AddDays(1) ? "tomorrow"
            : when.ToString("dddd d MMM", CultureInfo.InvariantCulture);
        return $"{dayText} at {when.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
