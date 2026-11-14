using System.Globalization;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Reports;

/// <summary>
/// Builds a client's report: reads plain rows (IReportRepository) and adds them up by day, hour, item,
/// service and staff in the CLIENT's own time zone. Used by the portal / Admin report page, the CSV
/// download and the monthly email, so all three always show the same numbers.
/// </summary>
public class ReportBuilder
{
    private readonly IReportRepository _reports;
    private readonly IBusinessProfileRepository _profile;

    public ReportBuilder(IReportRepository reports, IBusinessProfileRepository profile)
    {
        _reports = reports;
        _profile = profile;
    }

    public static ClientReportKind KindFor(ClientConfig client) => client.Vertical switch
    {
        VerticalPack.BeautyWellness => ClientReportKind.Bookings,
        VerticalPack.FnBRetail => ClientReportKind.Orders,
        _ => client.Workflows.Any(w => w.HandlerKey == "booking-message") ? ClientReportKind.Bookings
            : client.Workflows.Any(w => w.HandlerKey is "customer-message" or "order-parser") ? ClientReportKind.Orders
            : ClientReportKind.MessagesOnly
    };

    public async Task<ClientReport> BuildAsync(ClientConfig client, ReportPeriod period, CancellationToken cancellationToken = default)
    {
        var previous = period.Previous();
        var hours = await _profile.GetHoursAsync(client.Id, cancellationToken);
        var closed = (await _profile.GetClosedDatesAsync(client.Id, previous.From, cancellationToken)).Select(c => c.Date).ToHashSet();
        var tz = client.TimeZoneId;
        DateTime Local(DateTime utc) => LocalTime.ToLocal(utc, tz);
        bool Closed(DateTime utc) => !BusinessClock.IsOpenAt(hours, closed, Local(utc));

        var report = new ClientReport
        {
            Client = client,
            Period = period,
            PreviousPeriod = previous,
            Kind = KindFor(client),
            HoursConfigured = hours.Count > 0
        };

        // ---------------- Messages ----------------
        var messages = await _reports.GetMessagesAsync(client.Id, period.FromUtc, period.ToUtc, cancellationToken);
        var messagesBefore = await _reports.GetMessagesAsync(client.Id, previous.FromUtc, previous.ToUtc, cancellationToken);
        var fromCustomers = messages.Where(m => m.Kind != InboundKinds.Follow).ToList();
        report.Messages = fromCustomers.Count;
        report.MessagesBefore = messagesBefore.Count(m => m.Kind != InboundKinds.Follow);
        report.Typed = fromCustomers.Count(m => m.Kind == InboundKinds.Text);
        report.Tapped = fromCustomers.Count(m => m.Kind == InboundKinds.Tap);

        var customerKeys = fromCustomers.Where(m => !string.IsNullOrEmpty(m.CustomerId)).Select(m => Key(m.Channel, m.CustomerId!)).ToHashSet();
        var seenBefore = await _reports.GetCustomerKeysBeforeAsync(client.Id, period.FromUtc, cancellationToken);
        report.Customers = customerKeys.Count;
        report.ReturningCustomers = customerKeys.Count(seenBefore.Contains);
        report.NewCustomers = report.Customers - report.ReturningCustomers;
        if (report.HoursConfigured) report.AfterHoursMessages = fromCustomers.Count(m => Closed(m.ReceivedAtUtc));

        PerDay(report.MessagesPerDay, period, fromCustomers.Select(m => Local(m.ReceivedAtUtc)));
        ByHour(report.MessagesByHour, fromCustomers.Select(m => Local(m.ReceivedAtUtc)));
        report.Channels.AddRange(fromCustomers.GroupBy(m => m.Channel)
            .Select(g => new ReportPoint(Channels.DisplayName(g.Key), g.Count()))
            .OrderByDescending(p => p.Value));

        var runs = await _reports.GetRunsAsync(client.Id, period.FromUtc, period.ToUtc, cancellationToken);
        report.NeededStaff = runs.Count(r => HasStaffAlert(r.ActionsJson));

        // ---------------- Orders ----------------
        if (report.Kind == ClientReportKind.Orders)
        {
            var orders = await _reports.GetOrdersAsync(client.Id, period.FromUtc, period.ToUtc, cancellationToken);
            var ordersBefore = await _reports.GetOrdersAsync(client.Id, previous.FromUtc, previous.ToUtc, cancellationToken);
            var counted = orders.Where(o => o.Status != OrderStatus.Cancelled).ToList();
            var countedBefore = ordersBefore.Where(o => o.Status != OrderStatus.Cancelled).ToList();

            report.Orders = counted.Count;
            report.OrdersBefore = countedBefore.Count;
            report.OrdersCancelled = orders.Count - counted.Count;
            report.OrdersCompleted = counted.Count(o => o.Status == OrderStatus.Completed);
            report.OrdersWaiting = counted.Count(o => o.Status == OrderStatus.New);
            report.Sales = counted.Sum(o => o.Total ?? 0);
            report.SalesBefore = countedBefore.Sum(o => o.Total ?? 0);
            if (report.HoursConfigured) report.AfterHoursOrders = counted.Count(o => Closed(o.CreatedAtUtc));

            PerDay(report.OrdersPerDay, period, counted.Select(o => Local(o.CreatedAtUtc)));
            ByHour(report.OrdersByHour, counted.Select(o => Local(o.CreatedAtUtc)));
            report.TopItems.AddRange(counted.SelectMany(o => o.Items)
                .GroupBy(i => i.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Name = g.First().Name.Trim(),
                    Quantity = g.Sum(i => i.Quantity),
                    Sales = g.Sum(i => i.LineTotal ?? (i.UnitPrice ?? 0) * i.Quantity)
                })
                .OrderByDescending(x => x.Quantity).ThenByDescending(x => x.Sales).ThenBy(x => x.Name)
                .Take(10)
                .Select(x => new ReportPoint(x.Name, x.Quantity, $"{x.Quantity} sold", x.Sales > 0 ? Money.Format(x.Sales, client.Currency) : null)));
            Languages(report, counted.Select(o => o.Language));
            report.OrderList.AddRange(orders.OrderBy(o => o.CreatedAtUtc));
        }

        // ---------------- Bookings ----------------
        if (report.Kind == ClientReportKind.Bookings)
        {
            var bookings = await _reports.GetBookingsAsync(client.Id, period.FromUtc, period.ToUtc, cancellationToken);
            var bookingsBefore = await _reports.GetBookingsAsync(client.Id, previous.FromUtc, previous.ToUtc, cancellationToken);
            bool InPeriod(Booking b, ReportPeriod p) => b.StartUtc >= p.FromUtc && b.StartUtc < p.ToUtc;
            bool Counts(Booking b) => b.Status is not (BookingStatus.Cancelled or BookingStatus.Declined);

            var appointments = bookings.Where(b => InPeriod(b, period)).ToList();
            var live = appointments.Where(Counts).ToList();
            var liveBefore = bookingsBefore.Where(b => InPeriod(b, previous) && Counts(b)).ToList();

            report.Appointments = live.Count;
            report.AppointmentsBefore = liveBefore.Count;
            report.Completed = live.Count(b => b.Status == BookingStatus.Completed);
            report.NoShows = live.Count(b => b.Status == BookingStatus.NoShow);
            report.Upcoming = live.Count(b => b.IsHolding);
            report.CancelledOrDeclined = appointments.Count - live.Count;
            report.WalkIns = live.Count(b => b.Source == Booking.SourceStaff);
            report.AtHome = live.Count(b => b.AtHome);
            report.Revenue = live.Where(b => b.Status == BookingStatus.Completed).Sum(b => b.Total ?? 0);
            report.RevenueBefore = liveBefore.Where(b => b.Status == BookingStatus.Completed).Sum(b => b.Total ?? 0);
            report.BookedValue = live.Where(b => b.Status != BookingStatus.NoShow).Sum(b => b.Total ?? 0);

            var requests = bookings.Where(b => b.Source == Booking.SourceChat && b.CreatedAtUtc >= period.FromUtc && b.CreatedAtUtc < period.ToUtc).ToList();
            report.ChatRequests = requests.Count;
            if (report.HoursConfigured) report.AfterHoursRequests = requests.Count(b => Closed(b.CreatedAtUtc));

            PerDay(report.AppointmentsPerDay, period, live.Select(b => Local(b.StartUtc)));
            ByHour(report.AppointmentsByHour, live.Select(b => Local(b.StartUtc)), onlyBusy: true);
            foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                report.AppointmentsByWeekday.Add(new ReportPoint(BusinessClock.Short(d), live.Count(b => Local(b.StartUtc).DayOfWeek == d)));

            report.TopServices.AddRange(live.GroupBy(b => b.ServiceName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.First().ServiceName.Trim(), Count = g.Count(), Value = g.Where(b => b.Status != BookingStatus.NoShow).Sum(b => b.Total ?? 0) })
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.Value).ThenBy(x => x.Name)
                .Take(10)
                .Select(x => new ReportPoint(x.Name, x.Count, $"{x.Count} booked", x.Value > 0 ? Money.Format(x.Value, client.Currency) : null)));

            report.StaffLines.AddRange(live.GroupBy(b => string.IsNullOrWhiteSpace(b.StaffName) ? "(not assigned)" : b.StaffName!.Trim())
                .Select(g => new StaffLine(g.Key, g.Count(), g.Count(b => b.Status == BookingStatus.Completed), g.Count(b => b.Status == BookingStatus.NoShow),
                    g.Where(b => b.Status == BookingStatus.Completed).Sum(b => b.Total ?? 0)))
                .OrderByDescending(s => s.Booked).ThenBy(s => s.Name));
            Languages(report, live.Where(b => b.Source == Booking.SourceChat).Select(b => b.Language));
            report.BookingList.AddRange(appointments.OrderBy(b => b.StartUtc));
        }

        AddTiles(report);
        return report;
    }

    private static void AddTiles(ClientReport r)
    {
        var c = r.Client.Currency;
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        if (r.Kind == ClientReportKind.Bookings)
        {
            r.Tiles.Add(new ReportTile("appointments", "Appointments", N(r.Appointments),
                $"{N(r.Completed)} completed · {N(r.Upcoming)} to come", r.Appointments, r.AppointmentsBefore));
            r.Tiles.Add(new ReportTile("revenue", "Revenue (completed)", Money.Format(r.Revenue, c),
                $"Booked value {Money.Format(r.BookedValue, c)}", r.Revenue, r.RevenueBefore));
            r.Tiles.Add(new ReportTile("noshow", "No-shows", N(r.NoShows),
                r.NoShowRatePercent is { } rate ? $"{rate}% of finished appointments" : "None finished yet"));
            r.Tiles.Add(new ReportTile("requests", "Booked in the chat", N(r.ChatRequests),
                r.AfterHoursRequests is { } ah ? $"{N(ah)} while you were closed" : "Set opening hours to see after-hours"));
        }
        else if (r.Kind == ClientReportKind.Orders)
        {
            r.Tiles.Add(new ReportTile("orders", "Orders", N(r.Orders),
                $"{N(r.OrdersCompleted)} completed · {N(r.OrdersCancelled)} cancelled", r.Orders, r.OrdersBefore));
            r.Tiles.Add(new ReportTile("sales", "Sales", Money.Format(r.Sales, c),
                r.Orders > 0 ? $"Average order {Money.Format(Math.Round(r.Sales / r.Orders, 2), c)}" : null, r.Sales, r.SalesBefore));
            r.Tiles.Add(new ReportTile("afterhours", "Orders while closed", r.AfterHoursOrders is { } ah ? N(ah) : "-",
                r.AfterHoursOrders is null ? "Set opening hours to see this" : "Taken by the bot after hours"));
        }

        r.Tiles.Add(new ReportTile("messages", "Customer messages", N(r.Messages),
            $"{N(r.Customers)} customers · {N(r.NewCustomers)} new", r.Messages, r.MessagesBefore));
        if (r.Kind == ClientReportKind.MessagesOnly)
            r.Tiles.Add(new ReportTile("afterhours", "Messages while closed", r.AfterHoursMessages is { } am ? N(am) : "-",
                r.AfterHoursMessages is null ? "Set opening hours to see this" : "Answered by the bot after hours"));
        r.Tiles.Add(new ReportTile("staff", "Handed to staff", N(r.NeededStaff),
            r.Messages > 0 ? $"{(int)Math.Round(r.NeededStaff * 100m / Math.Max(1, r.Customers))}% of customers" : null));
    }

    public static string Key(string channel, string customerId) => channel + ":" + customerId;

    /// <summary>A staff alert in the run (not the "new order / booking" message, which is routine).</summary>
    public static bool HasStaffAlert(string? actionsJson)
    {
        if (string.IsNullOrEmpty(actionsJson)) return false;
        try
        {
            var actions = JsonSerializer.Deserialize<List<ActionRecord>>(actionsJson) ?? new();
            return actions.Any(a => a.Type == nameof(ActionType.NotifyInternal) &&
                                    a.Parameters.GetValueOrDefault("kind") is not (NotifyInternalAction.OrderKind or NotifyInternalAction.BookingKind));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void PerDay(List<ReportPoint> into, ReportPeriod period, IEnumerable<DateTime> locals)
    {
        var counts = locals.GroupBy(DateOnly.FromDateTime).ToDictionary(g => g.Key, g => g.Count());
        foreach (var d in period.Dates())
            into.Add(new ReportPoint(d.ToString(period.Days > 7 ? "d MMM" : "ddd d", CultureInfo.InvariantCulture), counts.GetValueOrDefault(d)));
    }

    /// <summary>Hours of the day. onlyBusy: from the first to the last hour that had anything (appointments).</summary>
    private static void ByHour(List<ReportPoint> into, IEnumerable<DateTime> locals, bool onlyBusy = false)
    {
        var counts = new int[24];
        foreach (var t in locals) counts[t.Hour]++;
        int first = 0, last = 23;
        if (onlyBusy)
        {
            first = Array.FindIndex(counts, c => c > 0);
            last = Array.FindLastIndex(counts, c => c > 0);
            if (first < 0) return;
        }
        for (var h = first; h <= last; h++)
            into.Add(new ReportPoint(HourLabel(h), counts[h]));
    }

    public static string HourLabel(int hour) => hour == 0 ? "12a" : hour < 12 ? $"{hour}a" : hour == 12 ? "12p" : $"{hour - 12}p";

    private static void Languages(ClientReport report, IEnumerable<string?> languages)
    {
        report.Languages.AddRange(languages
            .Select(l => LanguageName(l))
            .GroupBy(l => l)
            .Select(g => new ReportPoint(g.Key, g.Count()))
            .OrderByDescending(p => p.Value));
    }

    public static string LanguageName(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0 || c.StartsWith("en")) return "English";
        return c switch
        {
            "tl" or "fil" => "Tagalog / Taglish",
            "th" => "Thai",
            "hi" => "Hindi",
            "ta" => "Tamil",
            "te" => "Telugu",
            "kn" => "Kannada",
            "ml" => "Malayalam",
            "ar" => "Arabic",
            "ceb" => "Cebuano",
            "zh" => "Chinese",
            "ja" => "Japanese",
            "ko" => "Korean",
            "ru" => "Russian",
            _ => TryCulture(c)
        };

        static string TryCulture(string c)
        {
            try { return CultureInfo.GetCultureInfo(c).EnglishName; }
            catch (CultureNotFoundException) { return c; }
        }
    }
}
