using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Beauty;

/// <summary>A client's bookable services, staff and hours, loaded once per message.</summary>
public sealed class BookingCatalog
{
    public ClientConfig Client { get; init; } = null!;
    public List<ServiceItem> Services { get; init; } = new();   // active only
    public List<StaffMember> Staff { get; init; } = new();      // active only
    public List<OpeningHours> Hours { get; init; } = new();
    public List<DateOnly> ClosedDates { get; init; } = new();
    public DateTime UtcNow { get; init; }
    public DateTime LocalNow { get; init; }
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public ServiceItem? Service(Guid id) => Services.FirstOrDefault(s => s.Id == id);
    public StaffMember? StaffMember(Guid id) => Staff.FirstOrDefault(s => s.Id == id);

    /// <summary>Only services someone can actually do are offered to customers.</summary>
    public List<ServiceItem> Bookable => Services.Where(s => Staff.Any(m => m.Services.Any(x => x.ServiceItemId == s.Id))).ToList();

    public List<string> Categories => Bookable
        .Select(s => string.IsNullOrWhiteSpace(s.Category) ? BookingDesk.NoCategory : s.Category.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(c => c == BookingDesk.NoCategory ? "~" : c, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public List<ServiceItem> InCategory(string category) => Bookable
        .Where(s => category == BookingDesk.NoCategory
            ? string.IsNullOrWhiteSpace(s.Category)
            : string.Equals((s.Category ?? "").Trim(), category, StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>Staff who can do this service (and travel, for home service).</summary>
    public List<StaffMember> StaffFor(ServiceItem service, bool atHome) => Staff
        .Where(m => m.Services.Any(x => x.ServiceItemId == service.Id) && (!atHome || m.DoesHomeService))
        .ToList();

    /// <summary>Home service can be picked for this service.</summary>
    public bool HomeServiceFor(ServiceItem service) =>
        Client.HomeServiceEnabled && service.HomeServiceAllowed && StaffFor(service, true).Count > 0;

    public bool HoursConfigured => Hours.Count > 0;
}

/// <summary>
/// The booking logic shared by the chat and the Bookings page: free days and times, saving a booking
/// without double-booking anyone, and the texts staff and customers get.
/// </summary>
public class BookingDesk
{
    public const string NoCategory = "-";

    private readonly IBookingRepository _bookings;
    private readonly IBusinessProfileRepository _profile;
    private readonly TimeProvider _clock;

    public BookingDesk(IBookingRepository bookings, IBusinessProfileRepository profile, TimeProvider clock)
    {
        _bookings = bookings;
        _profile = profile;
        _clock = clock;
    }

    public async Task<BookingCatalog> LoadAsync(ClientConfig client, CancellationToken cancellationToken = default)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var localNow = LocalTime.ToLocal(utcNow, client.TimeZoneId);
        var today = DateOnly.FromDateTime(localNow);
        return new BookingCatalog
        {
            Client = client,
            Services = await _bookings.GetServicesAsync(client.Id, activeOnly: true, cancellationToken),
            Staff = await _bookings.GetStaffAsync(client.Id, activeOnly: true, cancellationToken),
            Hours = await _profile.GetHoursAsync(client.Id, cancellationToken),
            ClosedDates = (await _profile.GetClosedDatesAsync(client.Id, today, cancellationToken)).Select(c => c.Date).ToList(),
            UtcNow = utcNow,
            LocalNow = localNow
        };
    }

    /// <summary>What the customer is choosing: a service, where, and who.</summary>
    public sealed record Choice(ServiceItem Service, bool AtHome, string Preference, Guid? StaffId);

    private SlotQuery Query(BookingCatalog cat, Choice choice, DateOnly date, IReadOnlyCollection<BusyBlock> busy) => new()
    {
        Date = date,
        ServiceId = choice.Service.Id,
        DurationMinutes = choice.Service.DurationMinutes,
        BufferMinutes = choice.Service.BufferMinutes,
        AtHome = choice.AtHome,
        TravelMinutes = cat.Client.HomeServiceTravelMinutes,
        Preference = choice.Preference,
        StaffId = choice.StaffId,
        Hours = cat.Hours,
        ClosedDates = cat.ClosedDates,
        Staff = cat.Staff.Select(m => new SlotStaff(m.Id, m.Name, m.Gender, m.DaysOffMask, m.DoesHomeService,
            m.Services.Select(x => x.ServiceItemId).ToList())).ToList(),
        Busy = busy,
        TimeZoneId = cat.Client.TimeZoneId,
        UtcNow = cat.UtcNow,
        StepMinutes = cat.Client.BookingSlotMinutes,
        MinNoticeMinutes = cat.Client.BookingMinNoticeMinutes
    };

    private Task<List<BusyBlock>> BusyAsync(BookingCatalog cat, DateOnly from, DateOnly to, Guid? exceptBookingId, CancellationToken cancellationToken)
    {
        // A day either side: travel and overnight hours can reach into the next / previous day.
        var fromUtc = LocalTime.FromLocal(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), cat.Client.TimeZoneId).UtcDateTime;
        var toUtc = LocalTime.FromLocal(to.AddDays(2).ToDateTime(TimeOnly.MinValue), cat.Client.TimeZoneId).UtcDateTime;
        return _bookings.GetBusyAsync(cat.Client.Id, fromUtc, toUtc, exceptBookingId, cancellationToken);
    }

    /// <summary>Free start times on one day.</summary>
    public async Task<List<FreeSlot>> SlotsAsync(BookingCatalog cat, Choice choice, DateOnly date, Guid? exceptBookingId = null,
        CancellationToken cancellationToken = default)
    {
        var busy = await BusyAsync(cat, date, date, exceptBookingId, cancellationToken);
        return SlotFinder.Find(Query(cat, choice, date, busy));
    }

    /// <summary>The next days (within the client's "days ahead") that still have at least one free time.</summary>
    public async Task<List<(DateOnly Date, int FreeTimes)>> OpenDaysAsync(BookingCatalog cat, Choice choice, int max = 10, Guid? exceptBookingId = null,
        CancellationToken cancellationToken = default)
    {
        var days = Math.Clamp(cat.Client.BookingDaysAhead, 1, 90);
        var last = cat.Today.AddDays(days - 1);
        var busy = await BusyAsync(cat, cat.Today, last, exceptBookingId, cancellationToken);

        var result = new List<(DateOnly, int)>();
        for (var d = cat.Today; d <= last && result.Count < max; d = d.AddDays(1))
        {
            var free = SlotFinder.Find(Query(cat, choice, d, busy)).Count;
            if (free > 0) result.Add((d, free));
        }
        return result;
    }

    /// <summary>
    /// Saves the booking at that start time with the first staff member who is still free. Someone else
    /// may have taken the slot a moment ago - then the next free person is tried, and null means it's gone.
    /// </summary>
    public async Task<Booking?> CreateAsync(BookingCatalog cat, Choice choice, DateTime localStart, Booking details,
        CancellationToken cancellationToken = default)
    {
        var date = DateOnly.FromDateTime(localStart);
        var slot = (await SlotsAsync(cat, choice, date, null, cancellationToken)).FirstOrDefault(s => s.LocalStart == localStart);
        if (slot is null) return null;

        foreach (var staffId in slot.FreeStaff)
        {
            var staff = cat.StaffMember(staffId);
            if (staff is null) continue;

            var booking = Fill(details, cat, choice, slot, staff);
            if (await _bookings.TryAddAsync(booking, cancellationToken)) return booking;
        }
        return null;
    }

    /// <summary>
    /// Staff add a booking (walk-in, phone call) at any time they choose - opening hours and notice do not
    /// apply, but nobody is double-booked. staffId null = the least busy suitable person who is free.
    /// </summary>
    public async Task<Booking?> CreateByStaffAsync(BookingCatalog cat, Choice choice, DateTime localStart, Booking details, Guid? staffId,
        CancellationToken cancellationToken = default)
    {
        var startUtc = LocalTime.FromLocal(localStart, cat.Client.TimeZoneId).UtcDateTime;
        var date = DateOnly.FromDateTime(localStart);
        var busy = await BusyAsync(cat, date, date, null, cancellationToken);
        var people = staffId is { } one
            ? cat.Staff.Where(m => m.Id == one).ToList()
            : cat.StaffFor(choice.Service, choice.AtHome)
                .OrderBy(m => busy.Count(b => b.StaffId == m.Id)).ToList();

        var slot = new FreeSlot(localStart, startUtc, people.Select(p => p.Id).ToList());
        foreach (var staff in people)
        {
            var booking = Fill(details, cat, choice, slot, staff);
            if (await _bookings.TryAddAsync(booking, cancellationToken)) return booking;
        }
        return null;
    }

    /// <summary>Moves an existing booking to a new start time (same service, same choice). Null = no longer free.</summary>
    public async Task<(DateTime StartUtc, StaffMember Staff)?> RescheduleAsync(BookingCatalog cat, Booking existing, Choice choice, DateTime localStart,
        string changedBy, CancellationToken cancellationToken = default)
    {
        var date = DateOnly.FromDateTime(localStart);
        var slot = (await SlotsAsync(cat, choice, date, existing.Id, cancellationToken)).FirstOrDefault(s => s.LocalStart == localStart);
        if (slot is null) return null;

        foreach (var staffId in slot.FreeStaff)
        {
            if (cat.StaffMember(staffId) is not { } staff) continue;
            var (from, until) = SlotFinder.BusyWindow(slot.StartUtc, choice.Service.DurationMinutes, choice.Service.BufferMinutes, choice.AtHome,
                cat.Client.HomeServiceTravelMinutes);
            if (await _bookings.TryRescheduleAsync(cat.Client.Id, existing.Id, slot.StartUtc, slot.StartUtc.AddMinutes(choice.Service.DurationMinutes),
                    from, until, date, staff.Id, staff.Name, changedBy, cancellationToken))
                return (slot.StartUtc, staff);
        }
        return null;
    }

    private static Booking Fill(Booking d, BookingCatalog cat, Choice choice, FreeSlot slot, StaffMember staff)
    {
        var service = choice.Service;
        var (from, until) = SlotFinder.BusyWindow(slot.StartUtc, service.DurationMinutes, service.BufferMinutes, choice.AtHome,
            cat.Client.HomeServiceTravelMinutes);
        var fee = choice.AtHome ? cat.Client.HomeServiceFee : null;
        return new Booking
        {
            Id = d.Id,
            ClientConfigId = cat.Client.Id,
            Source = d.Source,
            Channel = d.Channel,
            CustomerId = d.CustomerId,
            CustomerName = d.CustomerName,
            CustomerPhone = d.CustomerPhone,
            CustomerNote = d.CustomerNote,
            Language = d.Language,
            HomeAddress = choice.AtHome ? d.HomeAddress : null,
            Status = d.Status,
            StatusChangedBy = d.StatusChangedBy,
            StatusChangedAtUtc = d.StatusChangedAtUtc,
            ReminderDueUtc = d.ReminderDueUtc,
            ServiceItemId = service.Id,
            ServiceName = service.Name,
            DurationMinutes = service.DurationMinutes,
            Currency = cat.Client.Currency is { Length: 3 } c ? c.ToUpperInvariant() : null,
            Price = service.Price,
            HomeServiceFee = fee,
            Total = service.Price is { } p ? p + (fee ?? 0) : null,
            StaffMemberId = staff.Id,
            StaffName = staff.Name,
            StaffPreference = choice.Preference,
            StartUtc = slot.StartUtc,
            EndUtc = slot.StartUtc.AddMinutes(service.DurationMinutes),
            BusyFromUtc = from,
            BusyUntilUtc = until,
            LocalDate = DateOnly.FromDateTime(slot.LocalStart),
            AtHome = choice.AtHome
        };
    }

    // ---------------------------------------------------------------------
    // Reminders
    // ---------------------------------------------------------------------

    /// <summary>
    /// When the reminder goes out: a day before; if the booking is sooner than that, 2 hours before;
    /// if even that has passed (or reminders are off, or nobody to send to), no reminder.
    /// </summary>
    public static DateTime? ReminderDue(Booking booking, ClientConfig client, DateTime utcNow)
    {
        if (!client.BookingRemindersEnabled || string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel))
            return null;
        var dayBefore = booking.StartUtc.AddHours(-24);
        if (dayBefore > utcNow.AddMinutes(30)) return dayBefore;
        var twoHours = booking.StartUtc.AddHours(-2);
        return twoHours > utcNow.AddMinutes(10) ? twoHours : null;
    }

    // ---------------------------------------------------------------------
    // Texts (English - our side is English only; customer messages are translated by the notifier)
    // ---------------------------------------------------------------------

    /// <summary>12-hour clock except in Thailand, where 24-hour is usual.</summary>
    public static string Time(DateTime local, string? country) =>
        string.Equals(country, "TH", StringComparison.OrdinalIgnoreCase)
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("h:mm tt", CultureInfo.InvariantCulture);

    public static string Day(DateOnly date, DateOnly today) =>
        date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"Sat 15 Nov, 3:00 PM"</summary>
    public static string When(DateTime local, string? country) =>
        local.ToString("ddd d MMM", CultureInfo.InvariantCulture) + ", " + Time(local, country);

    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr {minutes % 60} min";

    /// <summary>"60 min · ₱800.00"</summary>
    public static string ServiceLine(ServiceItem s, string? currency) =>
        Duration(s.DurationMinutes) + (s.Price is { } p ? " · " + Money.Format(p, currency) : "");

    /// <summary>The message the client's staff get for a new / changed / cancelled booking.</summary>
    public static string StaffMessage(Booking b, ClientConfig client, string headline)
    {
        var local = LocalTime.ToLocal(b.StartUtc, client.TimeZoneId);
        var sb = new StringBuilder();
        sb.AppendLine($"{headline} {b.Reference} - {client.ClientName}");
        sb.AppendLine($"{b.ServiceName} ({Duration(b.DurationMinutes)})");
        sb.AppendLine($"When: {When(local, client.Country)}");
        sb.AppendLine($"Staff: {b.StaffName ?? "-"}" + (b.StaffPreference switch { "F" => " (asked for female)", "M" => " (asked for male)", "staff" => " (asked for this person)", _ => "" }));
        if (b.AtHome) sb.AppendLine($"HOME SERVICE: {b.HomeAddress ?? "(no address)"}");
        sb.AppendLine($"Customer: {b.CustomerName ?? "-"}" + (string.IsNullOrWhiteSpace(b.CustomerPhone) ? "" : $", {b.CustomerPhone}") +
                      (string.IsNullOrWhiteSpace(b.Channel) ? "" : $" ({Channels.DisplayName(b.Channel)})"));
        if (b.Total is { } t) sb.AppendLine($"Price: {Money.Format(t, b.Currency)}" + (b.HomeServiceFee is > 0 ? $" (incl. home service {Money.Format(b.HomeServiceFee.Value, b.Currency)})" : ""));
        if (!string.IsNullOrWhiteSpace(b.CustomerNote)) sb.AppendLine($"Note: {b.CustomerNote}");
        if (!string.IsNullOrWhiteSpace(b.StatusReason)) sb.AppendLine($"Reason: {b.StatusReason}");
        if (b.Status == BookingStatus.Pending) sb.AppendLine("Please confirm or decline it on the Bookings page.");
        return sb.ToString().TrimEnd();
    }
}
