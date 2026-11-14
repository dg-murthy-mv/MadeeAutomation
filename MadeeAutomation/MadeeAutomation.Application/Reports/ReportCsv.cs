using System.Globalization;
using System.Text;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Reports;

/// <summary>
/// CSV downloads that open cleanly in Excel and Google Sheets: UTF-8 with a BOM (so ₱, ฿, ₹ and Thai
/// names show correctly), times in the client's own time zone, and cells starting with = + - @ made
/// harmless so a customer's name can never run as a formula.
/// </summary>
public static class ReportCsv
{
    public static byte[] Bookings(ClientReport report)
    {
        var tz = report.Client.TimeZoneId;
        var sb = new StringBuilder();
        Row(sb, "Reference", "Date", "Time", "Service", "Staff", "Customer", "Mobile", "Where", "Status", "Price", "Home service fee", "Total",
            "Currency", "Made by", "Channel", "Language", "Made at");
        foreach (var b in report.BookingList)
        {
            var start = LocalTime.ToLocal(b.StartUtc, tz);
            Row(sb, b.Reference, start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), start.ToString("HH:mm", CultureInfo.InvariantCulture),
                b.ServiceName, b.StaffName, b.CustomerName, b.CustomerPhone, b.AtHome ? "Home: " + b.HomeAddress : "At the shop",
                StatusText(b.Status), Num(b.Price), Num(b.HomeServiceFee), Num(b.Total), b.Currency,
                b.Source == Booking.SourceStaff ? "Staff (walk-in / phone)" : "Customer (chat)", Channels(b.Channel), ReportBuilder.LanguageName(b.Language),
                LocalTime.ToLocal(b.CreatedAtUtc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }
        return Bytes(sb);
    }

    public static byte[] Orders(ClientReport report)
    {
        var tz = report.Client.TimeZoneId;
        var sb = new StringBuilder();
        Row(sb, "Reference", "Date", "Time", "Customer", "Items", "Status", "Subtotal", "Tax", "Total", "Currency", "For (customer asked)",
            "Channel", "Language", "POS order id");
        foreach (var o in report.OrderList)
        {
            var at = LocalTime.ToLocal(o.CreatedAtUtc, tz);
            Row(sb, o.Reference, at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), at.ToString("HH:mm", CultureInfo.InvariantCulture),
                o.CustomerName, string.Join("; ", o.Items.Select(i => $"{i.Quantity} x {i.Name}")), o.Status.ToString(),
                Num(o.Subtotal), Num(o.TaxAmount), Num(o.Total), o.Currency, o.RequestedForText, Channels(o.Channel), ReportBuilder.LanguageName(o.Language), o.PosOrderId);
        }
        return Bytes(sb);
    }

    /// <summary>A client with neither orders nor bookings: the daily message counts.</summary>
    public static byte[] Messages(ClientReport report)
    {
        var sb = new StringBuilder();
        Row(sb, "Day", "Customer messages");
        foreach (var p in report.MessagesPerDay) Row(sb, p.Label, p.Value.ToString(CultureInfo.InvariantCulture));
        return Bytes(sb);
    }

    public static byte[] Clients(AdminReport report)
    {
        var sb = new StringBuilder();
        Row(sb, "Client", "Code", "Country", "Type", "Status", "Messages", "Messages (period before)", "Customers", "Orders", "Bookings",
            "Failed runs", "AI calls", "AI tokens", "AI cost (USD)", "Last customer message (UTC)");
        foreach (var r in report.Rows)
            Row(sb, r.Client.ClientName, r.Client.ClientCode, r.Client.Country, r.TypeText, r.Client.Status.ToString(),
                r.Activity.Messages.ToString(CultureInfo.InvariantCulture), r.MessagesBefore.ToString(CultureInfo.InvariantCulture),
                r.Activity.Customers.ToString(CultureInfo.InvariantCulture), r.Activity.Orders.ToString(CultureInfo.InvariantCulture),
                r.Activity.Bookings.ToString(CultureInfo.InvariantCulture), r.Activity.FailedRuns.ToString(CultureInfo.InvariantCulture),
                r.Activity.AiCalls.ToString(CultureInfo.InvariantCulture), r.Activity.AiTokens.ToString(CultureInfo.InvariantCulture),
                r.Activity.AiCostUsd.ToString("0.0000", CultureInfo.InvariantCulture),
                r.Activity.LastMessageUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        return Bytes(sb);
    }

    public static string StatusText(BookingStatus s) => s switch
    {
        BookingStatus.Pending => "To confirm",
        BookingStatus.NoShow => "No-show",
        _ => s.ToString()
    };

    private static string Channels(string? channel) => string.IsNullOrEmpty(channel) ? "" : MadeeAutomation.Domain.Enums.Channels.DisplayName(channel);

    private static string? Num(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Row(StringBuilder sb, params string?[] cells)
    {
        sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
    }

    /// <summary>Quotes every cell; a cell starting with = + - @ (or a tab / CR) gets a leading ' so Excel shows it as text.</summary>
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static byte[] Bytes(StringBuilder sb) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
}
