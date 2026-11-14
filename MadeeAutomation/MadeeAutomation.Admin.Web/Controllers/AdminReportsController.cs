using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Reports;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The MadeeVision report: every client side by side - messages, customers, orders, bookings, failed runs,
/// AI calls and AI cost - plus totals by country and type, and which clients have gone quiet.
/// MadeeVision staff only: there is no [ClientPortal] here, so a client's portal user gets 403.
/// Days are in the signed-in admin's own time zone (My account).
/// </summary>
public class AdminReportsController : Controller
{
    private readonly AdminReportBuilder _builder;
    private readonly TimeProvider _clock;

    public AdminReportsController(AdminReportBuilder builder, TimeProvider clock)
    {
        _builder = builder;
        _clock = clock;
    }

    private string TimeZoneId => User.FindFirst(AdminAuthService.TimeZoneClaim)?.Value is { Length: > 0 } tz ? tz : "Asia/Bangkok";

    public async Task<IActionResult> Index(string? period = null, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var (resolved, error, today) = Resolve(period, from, to);
        var report = await _builder.BuildAsync(resolved, _clock.GetUtcNow().UtcDateTime, cancellationToken);
        return View(new AdminReportPageModel
        {
            Report = report,
            TimeZoneId = TimeZoneId,
            Picker = new PeriodPickerModel
            {
                Period = resolved,
                Controller = "AdminReports",
                Error = error,
                Today = today,
                CsvLabel = "Download clients (CSV)",
                CsvUrl = Url.Action(nameof(Csv), new
                {
                    period = resolved.Key,
                    from = resolved.Key == ReportPeriod.Custom ? resolved.From.ToString("yyyy-MM-dd") : null,
                    to = resolved.Key == ReportPeriod.Custom ? resolved.To.ToString("yyyy-MM-dd") : null
                })
            }
        });
    }

    public async Task<IActionResult> Csv(string? period = null, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var (resolved, _, _) = Resolve(period, from, to);
        var report = await _builder.BuildAsync(resolved, _clock.GetUtcNow().UtcDateTime, cancellationToken);
        return File(ReportCsv.Clients(report), "text/csv; charset=utf-8", $"madeeautomation-clients-{resolved.FileLabel}.csv");
    }

    private (ReportPeriod Period, string? Error, DateOnly Today) Resolve(string? period, DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(_clock.GetUtcNow().UtcDateTime, TimeZoneId));
        var resolved = ReportPeriod.Resolve(period, from, to, today, TimeZoneId, out var error);
        return (resolved, error, today);
    }
}
