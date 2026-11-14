using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Reports;

/// <summary>One bar of a chart or one line of a ranked list.</summary>
public sealed record ReportPoint(string Label, decimal Value, string? Text = null, string? Note = null);

/// <summary>A number at the top of a report, with the same number for the period before (for the arrow).</summary>
public sealed record ReportTile(string Key, string Label, string Value, string? Note = null, decimal? Current = null, decimal? Previous = null)
{
    /// <summary>Change against the period before in %, or null when there is nothing to compare with.</summary>
    public int? ChangePercent => Current is { } c && Previous is { } p && p > 0 ? (int)Math.Round((c - p) / p * 100m) : null;
}

public enum ClientReportKind { Orders, Bookings, MessagesOnly }

/// <summary>Everything one client's report page shows. Built by ReportBuilder - no database code here.</summary>
public sealed class ClientReport
{
    public required ClientConfig Client { get; init; }
    public required ReportPeriod Period { get; init; }
    public required ReportPeriod PreviousPeriod { get; init; }
    public ClientReportKind Kind { get; init; }
    public bool HoursConfigured { get; init; }

    public List<ReportTile> Tiles { get; } = new();

    // ---- Messages ----
    public int Messages { get; set; }
    public int MessagesBefore { get; set; }
    public int Customers { get; set; }
    public int NewCustomers { get; set; }
    public int ReturningCustomers { get; set; }
    public int Typed { get; set; }
    public int Tapped { get; set; }
    /// <summary>Messages that came in while the business was closed. Null when opening hours are not set.</summary>
    public int? AfterHoursMessages { get; set; }
    /// <summary>Chats the bot handed to staff (questions it could not answer, a failed reply, "Talk to staff").</summary>
    public int NeededStaff { get; set; }
    public List<ReportPoint> MessagesPerDay { get; } = new();
    public List<ReportPoint> MessagesByHour { get; } = new();
    public List<ReportPoint> Channels { get; } = new();
    public List<ReportPoint> Languages { get; } = new();

    // ---- Orders (cafés, bakeries, shops) ----
    public int Orders { get; set; }
    public int OrdersBefore { get; set; }
    public int OrdersCancelled { get; set; }
    public int OrdersCompleted { get; set; }
    public int OrdersWaiting { get; set; }
    public decimal Sales { get; set; }
    public decimal SalesBefore { get; set; }
    public int? AfterHoursOrders { get; set; }
    public List<ReportPoint> OrdersPerDay { get; } = new();
    public List<ReportPoint> OrdersByHour { get; } = new();
    public List<ReportPoint> TopItems { get; } = new();
    public List<Order> OrderList { get; } = new();

    // ---- Bookings (spas, salons, nails) ----
    /// <summary>Appointments in the period that still count: waiting, confirmed, completed or no-show.</summary>
    public int Appointments { get; set; }
    public int AppointmentsBefore { get; set; }
    public int Completed { get; set; }
    public int NoShows { get; set; }
    public int Upcoming { get; set; }
    public int CancelledOrDeclined { get; set; }
    public int WalkIns { get; set; }
    public int AtHome { get; set; }
    /// <summary>Paid for: the total of completed appointments.</summary>
    public decimal Revenue { get; set; }
    public decimal RevenueBefore { get; set; }
    /// <summary>Completed + still to come (waiting / confirmed).</summary>
    public decimal BookedValue { get; set; }
    /// <summary>Booking requests customers made in the chat during the period (any appointment date).</summary>
    public int ChatRequests { get; set; }
    public int? AfterHoursRequests { get; set; }
    public List<ReportPoint> AppointmentsPerDay { get; } = new();
    public List<ReportPoint> AppointmentsByHour { get; } = new();
    public List<ReportPoint> AppointmentsByWeekday { get; } = new();
    public List<ReportPoint> TopServices { get; } = new();
    public List<StaffLine> StaffLines { get; } = new();
    public List<Booking> BookingList { get; } = new();

    public int? NoShowRatePercent => Completed + NoShows > 0 ? (int)Math.Round(NoShows * 100m / (Completed + NoShows)) : null;
}

public sealed record StaffLine(string Name, int Booked, int Completed, int NoShows, decimal Revenue);
