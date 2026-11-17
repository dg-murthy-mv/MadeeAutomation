using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Domain.Regional;

/// <summary>A weekly session of one staff member / doctor.</summary>
public sealed record SlotSession(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>
/// One staff member as the slot finder sees them. Sessions empty = they work the business's opening
/// hours; otherwise ONLY their own sessions count (a doctor seeing patients Mon 10-13 and 17-20).
/// </summary>
public sealed record SlotStaff(Guid Id, string Name, string? Gender, int DaysOffMask, bool DoesHomeService, IReadOnlyCollection<Guid> ServiceIds,
    IReadOnlyCollection<SlotSession>? Sessions = null)
{
    public bool IsOffOn(DayOfWeek day) => (DaysOffMask & (1 << (int)day)) != 0;
    public bool HasSessions => Sessions is { Count: > 0 };
}

/// <summary>Time a staff member is already taken (a booking, with its travel and clean-up time).</summary>
public sealed record BusyBlock(Guid StaffId, DateTime FromUtc, DateTime UntilUtc);

/// <summary>A start time that can be booked, and who is free for it (best choice first).</summary>
public sealed record FreeSlot(DateTime LocalStart, DateTime StartUtc, IReadOnlyList<Guid> FreeStaff);

/// <summary>Everything the slot finder needs for one service on one day.</summary>
public sealed class SlotQuery
{
    public DateOnly Date { get; init; }
    public Guid ServiceId { get; init; }
    public int DurationMinutes { get; init; }
    public int BufferMinutes { get; init; }

    /// <summary>Home service: travel is blocked before and after, and only staff who do home service count.</summary>
    public bool AtHome { get; init; }
    public int TravelMinutes { get; init; }

    /// <summary>"any", "F", "M", or "staff" with StaffId set.</summary>
    public string Preference { get; init; } = "any";
    public Guid? StaffId { get; init; }

    public IReadOnlyCollection<OpeningHours> Hours { get; init; } = Array.Empty<OpeningHours>();
    public IReadOnlyCollection<DateOnly> ClosedDates { get; init; } = Array.Empty<DateOnly>();
    public IReadOnlyCollection<SlotStaff> Staff { get; init; } = Array.Empty<SlotStaff>();
    public IReadOnlyCollection<BusyBlock> Busy { get; init; } = Array.Empty<BusyBlock>();

    public string? TimeZoneId { get; init; }
    public DateTime UtcNow { get; init; }
    public int StepMinutes { get; init; } = 30;
    public int MinNoticeMinutes { get; init; } = 60;
}

/// <summary>
/// Works out the start times that can really be booked: inside opening hours - or, for someone with their
/// own sessions (a doctor), inside their session - with the whole service fitting before it ends; not on
/// a closed date, not too soon, and with at least one suitable staff member who is working that day and
/// free for the whole time (travel + service + clean-up).
///
/// "Suitable" = can do the service, active (the caller passes only active staff), matches the customer's
/// preference (female / male / a named person), and does home service when the booking is at home.
/// When several are free, the one with the fewest bookings that day comes first, so work is shared.
/// Pure code: no database, no clock - easy to test.
/// </summary>
public static class SlotFinder
{
    public static List<FreeSlot> Find(SlotQuery q)
    {
        var result = new List<FreeSlot>();
        if (q.DurationMinutes <= 0 || q.ClosedDates.Contains(q.Date)) return result;

        var candidates = Candidates(q).ToList();
        if (candidates.Count == 0) return result;

        var step = Math.Clamp(q.StepMinutes, 5, 240);
        var earliestUtc = q.UtcNow.AddMinutes(Math.Max(0, q.MinNoticeMinutes));
        var travel = q.AtHome ? Math.Max(0, q.TravelMinutes) : 0;
        var dayStart = q.Date.ToDateTime(TimeOnly.MinValue);

        // How busy each person already is that day: the less busy one gets the booking.
        var load = candidates.ToDictionary(s => s.Id, s => q.Busy.Count(b => b.StaffId == s.Id &&
            LocalTime.ToLocal(b.FromUtc, q.TimeZoneId).Date == dayStart.Date));

        // The windows each person works that day: their own sessions, or else the opening hours.
        // Overnight periods (18:00-02:00) end the next day.
        List<(DateTime Open, DateTime Close)> Windows(IEnumerable<(TimeOnly Open, TimeOnly Close)> periods) =>
            periods.Select(p =>
            {
                var open = dayStart + p.Open.ToTimeSpan();
                var close = dayStart + p.Close.ToTimeSpan();
                if (close <= open) close = close.AddDays(1);
                return (open, close);
            }).ToList();
        var businessWindows = Windows(q.Hours.Where(h => h.DayOfWeek == q.Date.DayOfWeek).Select(h => (h.OpenTime, h.CloseTime)));
        var windows = candidates.ToDictionary(s => s.Id, s => s.HasSessions
            ? Windows(s.Sessions!.Where(x => x.Day == q.Date.DayOfWeek).Select(x => (x.Start, x.End)))
            : businessWindows);

        var seen = new HashSet<DateTime>();
        foreach (var (open, close) in windows.Values.SelectMany(w => w).Distinct().OrderBy(w => w.Item1))
        {
            for (var start = open; start.AddMinutes(q.DurationMinutes) <= close; start = start.AddMinutes(step))
            {
                if (!seen.Add(start)) continue;
                var startUtc = LocalTime.FromLocal(start, q.TimeZoneId).UtcDateTime;
                if (startUtc < earliestUtc) continue;

                var end = start.AddMinutes(q.DurationMinutes);
                var busyFrom = startUtc.AddMinutes(-travel);
                var busyUntil = startUtc.AddMinutes(q.DurationMinutes + Math.Max(0, q.BufferMinutes) + travel);

                var free = candidates
                    .Where(s => windows[s.Id].Any(w => w.Open <= start && end <= w.Close))
                    .Where(s => !q.Busy.Any(b => b.StaffId == s.Id && b.FromUtc < busyUntil && busyFrom < b.UntilUtc))
                    .OrderBy(s => load[s.Id])
                    .Select(s => s.Id)
                    .ToList();
                if (free.Count > 0) result.Add(new FreeSlot(start, startUtc, free));
            }
        }

        return result.OrderBy(s => s.LocalStart).ToList();
    }

    /// <summary>Staff who could do this booking at all on that day (before looking at times).</summary>
    public static IEnumerable<SlotStaff> Candidates(SlotQuery q) => q.Staff.Where(s =>
        s.ServiceIds.Contains(q.ServiceId) &&
        !s.IsOffOn(q.Date.DayOfWeek) &&
        (!q.AtHome || s.DoesHomeService) &&
        q.Preference switch
        {
            "F" => s.Gender == "F",
            "M" => s.Gender == "M",
            "staff" => s.Id == q.StaffId,
            _ => true
        });

    /// <summary>The busy window a booking at startUtc would take.</summary>
    public static (DateTime FromUtc, DateTime UntilUtc) BusyWindow(DateTime startUtc, int durationMinutes, int bufferMinutes, bool atHome, int travelMinutes)
    {
        var travel = atHome ? Math.Max(0, travelMinutes) : 0;
        return (startUtc.AddMinutes(-travel), startUtc.AddMinutes(durationMinutes + Math.Max(0, bufferMinutes) + travel));
    }
}
