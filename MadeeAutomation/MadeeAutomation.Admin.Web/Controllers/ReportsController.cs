using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Reports;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// One client's report: messages, orders or bookings, sales, busiest times, best sellers / services / staff,
/// in the client's own time zone. Opened by the client's portal user (their own business only) and by
/// MadeeVision staff (any client, plus the AI cost). CSV download and "email this report".
/// </summary>
public class ReportsController : ClientScopedController
{
    private readonly IClientRepository _clients;
    private readonly ReportBuilder _builder;
    private readonly IReportRepository _reports;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(IClientRepository clients, ReportBuilder builder, IReportRepository reports, IEmailSender email,
        TimeProvider clock, IConfiguration configuration, ILogger<ReportsController> logger)
    {
        _clients = clients;
        _builder = builder;
        _reports = reports;
        _email = email;
        _clock = clock;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? period = null, DateOnly? from = null, DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId), cancellationToken);
        if (client is null) return NotFound();

        var (resolved, error, today) = Resolve(client.TimeZoneId, period, from, to);
        var report = await _builder.BuildAsync(client, resolved, cancellationToken);
        var csv = Url.Action(nameof(Csv), new { clientConfigId = IsClientUser ? (Guid?)null : client.Id, period = resolved.Key,
            from = resolved.Key == ReportPeriod.Custom ? resolved.From.ToString("yyyy-MM-dd") : null,
            to = resolved.Key == ReportPeriod.Custom ? resolved.To.ToString("yyyy-MM-dd") : null });

        return View(new ClientReportPageModel
        {
            Report = report,
            Picker = new PeriodPickerModel
            {
                Period = resolved,
                Controller = "Reports",
                ClientConfigId = IsClientUser ? null : client.Id,
                Error = error,
                Today = today,
                CsvUrl = csv,
                CsvLabel = report.Kind switch
                {
                    ClientReportKind.Bookings => "Download bookings (CSV)",
                    ClientReportKind.Orders => "Download orders (CSV)",
                    _ => "Download (CSV)"
                }
            },
            ShowAiCost = !IsClientUser,
            AiByPurpose = IsClientUser ? new() : (await _reports.GetAiByPurposeAsync(client.Id, resolved.FromUtc, resolved.ToUtc, cancellationToken))
                .OrderByDescending(a => a.CostUsd).ToList(),
            EmailTo = string.IsNullOrWhiteSpace(client.StaffAlertEmail) ? null : client.StaffAlertEmail
        });
    }

    public async Task<IActionResult> Csv(Guid clientConfigId, string? period = null, DateOnly? from = null, DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId), cancellationToken);
        if (client is null) return NotFound();

        var (resolved, _, _) = Resolve(client.TimeZoneId, period, from, to);
        var report = await _builder.BuildAsync(client, resolved, cancellationToken);
        var (bytes, what) = report.Kind switch
        {
            ClientReportKind.Bookings => (ReportCsv.Bookings(report), "bookings"),
            ClientReportKind.Orders => (ReportCsv.Orders(report), "orders"),
            _ => (ReportCsv.Messages(report), "messages")
        };
        return File(bytes, "text/csv; charset=utf-8", $"{client.ClientCode}-{what}-{resolved.FileLabel}.csv");
    }

    /// <summary>Sends this report's summary (the same text as the monthly email) to the client's staff alert email.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Email(Guid clientConfigId, string? period = null, DateOnly? from = null, DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId), cancellationToken);
        if (client is null) return NotFound();

        var (resolved, _, _) = Resolve(client.TimeZoneId, period, from, to);
        var back = RedirectToAction(nameof(Index), new
        {
            clientConfigId = IsClientUser ? (Guid?)null : client.Id,
            period = resolved.Key,
            from = resolved.Key == ReportPeriod.Custom ? resolved.From.ToString("yyyy-MM-dd") : null,
            to = resolved.Key == ReportPeriod.Custom ? resolved.To.ToString("yyyy-MM-dd") : null
        });

        if (string.IsNullOrWhiteSpace(client.StaffAlertEmail))
        {
            TempData["Error"] = "There is no email to send it to. Add one under Business info (or the client's staff alert email).";
            return back;
        }

        var report = await _builder.BuildAsync(client, resolved, cancellationToken);
        var subject = $"{client.ClientName}: report for {resolved.Label}";
        var body = MonthlyReportRunner.Body(report, PublicBaseUrl());
        var result = await _email.SendAsync(client.StaffAlertEmail, subject, body, cancellationToken);
        if (result.Success)
        {
            _logger.LogInformation("{By} emailed the {Client} report for {Period} to {To}", SignedInAs, client.ClientCode, resolved.Label, client.StaffAlertEmail);
            TempData["Success"] = $"Report sent to {client.StaffAlertEmail}.";
        }
        else
        {
            TempData["Error"] = $"The email could not be sent: {result.Error}";
        }
        return back;
    }

    private string? PublicBaseUrl() =>
        _configuration["AdminWeb:PublicBaseUrl"] is { Length: > 0 } configured ? configured : $"{Request.Scheme}://{Request.Host}";

    private (ReportPeriod Period, string? Error, DateOnly Today) Resolve(string? timeZoneId, string? period, DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(_clock.GetUtcNow().UtcDateTime, timeZoneId));
        var resolved = ReportPeriod.Resolve(period, from, to, today, timeZoneId, out var error);
        return (resolved, error, today);
    }
}
