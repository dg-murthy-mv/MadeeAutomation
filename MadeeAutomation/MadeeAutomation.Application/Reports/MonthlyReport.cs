using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Reports;

/// <summary>
/// The monthly email: on the 1st of each month from 8 AM (client time), the owner gets last month's
/// numbers by email (ClientConfig.StaffAlertEmail), with a link to the full report. Once per month per
/// client (ClientConfig.MonthlyReportSentFor). Switched off per client with MonthlyReportEnabled.
///
/// Config (WebApi): AdminWeb:PublicBaseUrl - the Admin / portal address for the link (left out when not set).
/// </summary>
public class MonthlyReportRunner
{
    public const int SendFromHour = 8;

    private readonly IReportRepository _reports;
    private readonly ReportBuilder _builder;
    private readonly IEmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MonthlyReportRunner> _logger;

    public MonthlyReportRunner(IReportRepository reports, ReportBuilder builder, IEmailSender email, IConfiguration configuration,
        ILogger<MonthlyReportRunner> logger)
    {
        _reports = reports;
        _builder = builder;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The month to report on at this moment, or null when it's not the time: from 8 AM on the 1st until the
    /// end of the 3rd (so a server that was down on the 1st still sends, but installing mid-month doesn't
    /// suddenly email everyone last month's report).
    /// </summary>
    public static (int Year, int Month)? DueMonth(DateTime utcNow, ClientConfig client)
    {
        var local = LocalTime.ToLocal(utcNow, client.TimeZoneId);
        if (local.Day > 3 || (local.Day == 1 && local.Hour < SendFromHour)) return null;
        var last = new DateTime(local.Year, local.Month, 1).AddMonths(-1);
        return (last.Year, last.Month);
    }

    public static string MonthKey(int year, int month) => $"{year:0000}-{month:00}";

    /// <returns>How many emails were sent.</returns>
    public async Task<int> RunAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        if (!_email.IsConfigured) return 0;
        var sent = 0;
        foreach (var client in await _reports.GetMonthlyReportClientsAsync(cancellationToken))
        {
            if (DueMonth(utcNow, client) is not { } due) continue;
            var key = MonthKey(due.Year, due.Month);
            if (client.MonthlyReportSentFor == key) continue;

            var period = ReportPeriod.Month(due.Year, due.Month, client.TimeZoneId);
            // A client added after that month ended has nothing to report yet.
            if (client.CreatedAtUtc >= period.ToUtc) continue;

            var previousKey = client.MonthlyReportSentFor;
            if (!await _reports.TryClaimMonthlyReportAsync(client.Id, key, cancellationToken)) continue;
            try
            {
                var report = await _builder.BuildAsync(client, period, cancellationToken);
                if (report.Messages == 0 && report.Orders == 0 && report.Appointments == 0)
                {
                    _logger.LogInformation("Monthly report for {Client} ({Month}) skipped - nothing happened that month", client.ClientCode, key);
                    continue;
                }

                var result = await _email.SendAsync(client.StaffAlertEmail!, Subject(report), Body(report, _configuration["AdminWeb:PublicBaseUrl"]), cancellationToken);
                if (result.Success)
                {
                    sent++;
                    _logger.LogInformation("Monthly report for {Client} ({Month}) emailed", client.ClientCode, key);
                }
                else
                {
                    _logger.LogWarning("Monthly report for {Client} ({Month}) could not be emailed: {Error} - will try again", client.ClientCode, key, result.Error);
                    await _reports.ReleaseMonthlyReportAsync(client.Id, key, previousKey, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Monthly report for {Client} ({Month}) failed - will try again", client.ClientCode, key);
                await _reports.ReleaseMonthlyReportAsync(client.Id, key, previousKey, CancellationToken.None);
            }
        }
        return sent;
    }

    public static string Subject(ClientReport r) =>
        $"{r.Client.ClientName}: your {r.Period.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} report";

    public static string Body(ClientReport r, string? publicBaseUrl)
    {
        var c = r.Client.Currency;
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        static string Change(decimal now, decimal before) =>
            before > 0 ? $" ({(now >= before ? "+" : "")}{(int)Math.Round((now - before) / before * 100m)}% vs the month before)" : "";

        var sb = new StringBuilder();
        sb.AppendLine($"Hello {r.Client.ClientName},");
        sb.AppendLine();
        sb.AppendLine($"Here is what your chat assistant did in {r.Period.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}:");
        sb.AppendLine();

        if (r.Kind == ClientReportKind.Bookings)
        {
            sb.AppendLine($"- Appointments: {N(r.Appointments)}{Change(r.Appointments, r.AppointmentsBefore)}");
            sb.AppendLine($"  {N(r.Completed)} completed, {N(r.NoShows)} no-shows, {N(r.CancelledOrDeclined)} cancelled or declined");
            sb.AppendLine($"- Revenue from completed appointments: {Money.Format(r.Revenue, c)}{Change(r.Revenue, r.RevenueBefore)}");
            sb.AppendLine($"- Booked by customers in the chat: {N(r.ChatRequests)}" +
                          (r.AfterHoursRequests is > 0 ? $" ({N(r.AfterHoursRequests.Value)} while you were closed)" : ""));
            if (r.TopServices.Count > 0)
                sb.AppendLine($"- Most booked: {string.Join(", ", r.TopServices.Take(3).Select(s => $"{s.Label} ({s.Value:0})"))}");
            if (r.StaffLines.Count > 0)
                sb.AppendLine($"- Busiest: {string.Join(", ", r.StaffLines.Take(3).Select(s => $"{s.Name} ({s.Booked})"))}");
        }
        else if (r.Kind == ClientReportKind.Orders)
        {
            sb.AppendLine($"- Orders: {N(r.Orders)}{Change(r.Orders, r.OrdersBefore)}");
            sb.AppendLine($"- Sales: {Money.Format(r.Sales, c)}{Change(r.Sales, r.SalesBefore)}");
            if (r.AfterHoursOrders is > 0) sb.AppendLine($"- Orders taken while you were closed: {N(r.AfterHoursOrders.Value)}");
            if (r.TopItems.Count > 0)
                sb.AppendLine($"- Best sellers: {string.Join(", ", r.TopItems.Take(3).Select(i => $"{i.Label} ({i.Value:0})"))}");
        }

        sb.AppendLine($"- Customer messages: {N(r.Messages)} from {N(r.Customers)} customers ({N(r.NewCustomers)} new){Change(r.Messages, r.MessagesBefore)}");
        if (r.AfterHoursMessages is > 0) sb.AppendLine($"- Messages answered while you were closed: {N(r.AfterHoursMessages.Value)}");
        sb.AppendLine($"- Chats handed to your staff: {N(r.NeededStaff)}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            sb.AppendLine("See the full report (charts, and a download for Excel):");
            sb.AppendLine($"{publicBaseUrl.TrimEnd('/')}/Reports?period={ReportPeriod.Custom}&from={r.Period.From:yyyy-MM-dd}&to={r.Period.To:yyyy-MM-dd}");
            sb.AppendLine();
        }
        sb.AppendLine("You get this email on the 1st of every month. To stop it, untick \"Monthly report email\" under Business info.");
        sb.AppendLine();
        sb.AppendLine("MadeeAutomation by MadeeVision");
        return sb.ToString();
    }
}
