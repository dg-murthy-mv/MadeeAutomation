using System.Globalization;
using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>A client's weekly opening hours (in the client's time zone) and closed dates.</summary>
public class HoursController : Controller
{
    private static readonly DayOfWeek[] MondayFirst =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    private readonly IBusinessProfileRepository _profile;
    private readonly IClientRepository _clients;

    public HoursController(IBusinessProfileRepository profile, IClientRepository clients)
    {
        _profile = profile;
        _clients = clients;
    }

    public async Task<IActionResult> Index(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (client is null) return NotFound();

        var hours = await _profile.GetHoursAsync(client.Id);
        var days = MondayFirst.Select(d =>
        {
            var periods = hours.Where(h => h.DayOfWeek == d).OrderBy(h => h.OpenTime).ToList();
            return new DayHoursModel
            {
                Day = d,
                Closed = periods.Count == 0 && hours.Count > 0,
                Open1 = periods.ElementAtOrDefault(0)?.OpenTime.ToString("HH:mm"),
                Close1 = periods.ElementAtOrDefault(0)?.CloseTime.ToString("HH:mm"),
                Open2 = periods.ElementAtOrDefault(1)?.OpenTime.ToString("HH:mm"),
                Close2 = periods.ElementAtOrDefault(1)?.CloseTime.ToString("HH:mm")
            };
        }).ToList();

        return View(await PageAsync(client, days));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid clientConfigId, List<DayHoursModel> days)
    {
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (client is null) return NotFound();

        var rows = new List<OpeningHours>();
        var allBlank = days.All(d => !d.Closed && string.IsNullOrWhiteSpace(d.Open1) && string.IsNullOrWhiteSpace(d.Close1)
                                     && string.IsNullOrWhiteSpace(d.Open2) && string.IsNullOrWhiteSpace(d.Close2));
        if (allBlank) days = new List<DayHoursModel>();
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var label = d.Day.ToString();
            if (d.Closed) continue;

            var hasFirst = !string.IsNullOrWhiteSpace(d.Open1) || !string.IsNullOrWhiteSpace(d.Close1);
            var hasSecond = !string.IsNullOrWhiteSpace(d.Open2) || !string.IsNullOrWhiteSpace(d.Close2);
            if (!hasFirst && !hasSecond)
            {
                ModelState.AddModelError($"days[{i}]", $"{label}: enter opening and closing times, or tick Closed.");
                continue;
            }

            var first = Period(d.Day, d.Open1, d.Close1, $"days[{i}]", label);
            if (first is not null) rows.Add(first);

            if (hasSecond)
            {
                var second = Period(d.Day, d.Open2, d.Close2, $"days[{i}]", label + " (2nd period)");
                if (second is not null && first is not null)
                {
                    if (first.CloseTime <= first.OpenTime)
                        ModelState.AddModelError($"days[{i}]", $"{label}: the first period runs past midnight, so there can't be a second one.");
                    else if (second.OpenTime < first.CloseTime)
                        ModelState.AddModelError($"days[{i}]", $"{label}: the second period must start after the first one closes.");
                    else rows.Add(second);
                }
            }
        }

        if (!ModelState.IsValid) return View("Index", await PageAsync(client, days));

        await _profile.ReplaceHoursAsync(client.Id, rows);
        TempData["Success"] = rows.Count == 0
            ? "Opening hours cleared - the bot will answer hours questions from the knowledge base (or hand them to staff)."
            : "Opening hours saved.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddClosedDate(Guid clientConfigId, string? date, string? reason)
    {
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (client is null) return NotFound();

        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            TempData["Error"] = "Pick a date.";
        else if (day < today)
            TempData["Error"] = "That date is in the past.";
        else if (!await _profile.AddClosedDateAsync(new ClosedDate
                 {
                     ClientConfigId = client.Id,
                     Date = day,
                     Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)]
                 }))
            TempData["Error"] = $"{day:yyyy-MM-dd} is already a closed date.";
        else
            TempData["Success"] = $"Closed on {day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)} added.";

        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteClosedDate(Guid clientConfigId, Guid id)
    {
        await _profile.DeleteClosedDateAsync(id);
        TempData["Success"] = "Closed date removed.";
        return RedirectToAction(nameof(Index), new { clientConfigId });
    }

    private OpeningHours? Period(DayOfWeek day, string? open, string? close, string key, string label)
    {
        if (!TimeOnly.TryParseExact(open, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var o) ||
            !TimeOnly.TryParseExact(close, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c))
        {
            ModelState.AddModelError(key, $"{label}: enter both an opening and a closing time.");
            return null;
        }
        if (o == c)
        {
            ModelState.AddModelError(key, $"{label}: opening and closing time can't be the same.");
            return null;
        }
        return new OpeningHours { DayOfWeek = day, OpenTime = o, CloseTime = c };
    }

    private async Task<HoursPageModel> PageAsync(ClientConfig client, List<DayHoursModel> days)
    {
        var utcNow = DateTime.UtcNow;
        var localToday = DateOnly.FromDateTime(LocalTime.ToLocal(utcNow, client.TimeZoneId));
        var hours = await _profile.GetHoursAsync(client.Id);
        var closed = await _profile.GetClosedDatesAsync(client.Id, localToday.AddDays(-1));
        var status = BusinessClock.GetStatus(hours, closed, client.TimeZoneId, utcNow);

        return new HoursPageModel
        {
            Client = client,
            Days = days,
            ClosedDates = closed.Where(c => c.Date >= localToday).ToList(),
            StatusText = status.StatusText,
            HoursConfigured = status.HoursConfigured,
            LocalNowText = LocalTime.ToLocal(utcNow, client.TimeZoneId).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)
        };
    }
}
