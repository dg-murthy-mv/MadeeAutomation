using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

using MadeeAutomation.Domain.Billing;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The therapists, nail technicians and stylists customers book with: working days, home service,
/// and which services each one does. Used by staff (any client) and the client's portal user.
/// </summary>
public class StaffController : ClientScopedController
{
    private readonly IBookingRepository _bookings;
    private readonly IClientRepository _clients;

    public StaffController(IBookingRepository bookings, IClientRepository clients)
    {
        _bookings = bookings;
        _clients = clients;
    }

    public async Task<IActionResult> Index(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(new StaffPageModel
        {
            Client = client,
            Staff = await _bookings.GetStaffAsync(client.Id),
            Services = await _bookings.GetServicesAsync(client.Id)
        });
    }

    public async Task<IActionResult> Create(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        var services = await _bookings.GetServicesAsync(client.Id);
        return View("Edit", new StaffEditModel
        {
            Client = client,
            Services = services,
            // A new person usually does everything that's active - untick what they don't.
            Form = new StaffFormModel
            {
                ClientConfigId = client.Id,
                WorkingDays = Enumerable.Range(0, 7).ToList(),
                ServiceIds = services.Where(s => s.IsActive).Select(s => s.Id).ToList()
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] StaffFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(model.ClientConfigId));
        if (client is null) return NotFound();
        model.ClientConfigId = client.Id;
        CheckWorkingDays(model);
        var sessions = ReadSessions(model);
        var existing = await _bookings.GetStaffAsync(client.Id);
        if (PlanRules.StaffLimitError(client, existing.Count(x => x.IsActive) + 1) is { } limit)
            ModelState.AddModelError("", limit);
        if (!ModelState.IsValid)
            return View("Edit", new StaffEditModel { Client = client, Form = model, Services = await _bookings.GetServicesAsync(client.Id) });

        var staff = new StaffMember { ClientConfigId = client.Id, SortOrder = existing.Count == 0 ? 0 : existing.Max(s => s.SortOrder) + 1 };
        Apply(model, staff);
        staff.Sessions = sessions;
        staff.IsActive = true;
        await _bookings.AddStaffAsync(staff, model.ServiceIds);

        TempData["Success"] = $"{staff.Name} added.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    public async Task<IActionResult> Edit(Guid id)
    {
        var staff = await _bookings.GetStaffMemberAsync(id);
        if (staff is null || !CanAccess(staff.ClientConfigId)) return NotFound();
        var client = await _clients.GetWithWorkflowsAsync(staff.ClientConfigId);
        if (client is null) return NotFound();

        return View(new StaffEditModel
        {
            Client = client,
            Services = await _bookings.GetServicesAsync(client.Id),
            Form = new StaffFormModel
            {
                Id = staff.Id, ClientConfigId = staff.ClientConfigId, Name = staff.Name, Title = staff.Title, Gender = staff.Gender,
                WorkingDays = Enumerable.Range(0, 7).Where(d => !staff.IsOffOn((DayOfWeek)d)).ToList(),
                DoesHomeService = staff.DoesHomeService, IsActive = staff.IsActive, SortOrder = staff.SortOrder,
                ServiceIds = staff.Services.Select(x => x.ServiceItemId).ToList(),
                ConsultationFee = staff.ConsultationFee, Languages = staff.Languages, Bio = staff.Bio,
                SessionStarts = SessionBoxes(staff, start: true), SessionEnds = SessionBoxes(staff, start: false)
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, [Bind(Prefix = "Form")] StaffFormModel model)
    {
        var staff = await _bookings.GetStaffMemberAsync(id);
        if (staff is null || !CanAccess(staff.ClientConfigId)) return NotFound();
        var client = await _clients.GetWithWorkflowsAsync(staff.ClientConfigId);
        if (client is null) return NotFound();
        model.Id = staff.Id;
        model.ClientConfigId = staff.ClientConfigId;
        CheckWorkingDays(model);
        var sessions = ReadSessions(model);
        if (model.IsActive && !staff.IsActive)
        {
            var working = (await _bookings.GetStaffAsync(client.Id, activeOnly: true)).Count;
            if (PlanRules.StaffLimitError(client, working + 1) is { } limit) ModelState.AddModelError("", limit);
        }
        if (!ModelState.IsValid)
            return View(new StaffEditModel { Client = client, Form = model, Services = await _bookings.GetServicesAsync(client.Id) });

        Apply(model, staff);
        staff.Sessions = sessions;
        staff.IsActive = model.IsActive;
        staff.SortOrder = model.SortOrder;
        await _bookings.UpdateStaffAsync(staff, model.ServiceIds);

        TempData["Success"] = $"{staff.Name} saved." + (staff.IsActive ? "" : " Not working: customers can't book them; bookings already made stay.");
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var staff = await _bookings.GetStaffMemberAsync(id);
        if (staff is null || !CanAccess(staff.ClientConfigId)) return NotFound();
        await _bookings.DeleteStaffAsync(id);
        TempData["Success"] = $"{staff.Name} deleted. Bookings already made keep their name.";
        return RedirectToAction(nameof(Index), new { clientConfigId = staff.ClientConfigId });
    }

    /// <summary>The 14 time boxes (2 per day) filled from the saved sessions.</summary>
    private static List<string?> SessionBoxes(StaffMember staff, bool start)
    {
        var boxes = Enumerable.Repeat<string?>(null, 14).ToList();
        foreach (var day in staff.Sessions.GroupBy(x => (int)x.DayOfWeek))
        {
            var n = 0;
            foreach (var x in day.OrderBy(x => x.StartTime).Take(2))
                boxes[day.Key * 2 + n++] = (start ? x.StartTime : x.EndTime).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        return boxes;
    }

    /// <summary>Reads the 14 session boxes. Adds a form error for half-filled or unreadable ones.</summary>
    private List<StaffSession> ReadSessions(StaffFormModel m)
    {
        var result = new List<StaffSession>();
        for (var i = 0; i < 14; i++)
        {
            var a = m.SessionStarts.ElementAtOrDefault(i)?.Trim();
            var b = m.SessionEnds.ElementAtOrDefault(i)?.Trim();
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) continue;
            var day = BusinessClock.Short((DayOfWeek)(i / 2));
            if (!TimeOnly.TryParseExact(a, new[] { "HH:mm", "H:mm" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from) ||
                !TimeOnly.TryParseExact(b, new[] { "HH:mm", "H:mm" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var to))
            {
                ModelState.AddModelError("Form.SessionStarts", $"{day}: give both a start and an end time.");
                continue;
            }
            if (from == to)
            {
                ModelState.AddModelError("Form.SessionStarts", $"{day}: the start and end time are the same.");
                continue;
            }
            result.Add(new StaffSession { DayOfWeek = (DayOfWeek)(i / 2), StartTime = from, EndTime = to });
        }
        return result;
    }

    private void CheckWorkingDays(StaffFormModel m)
    {
        m.WorkingDays = m.WorkingDays.Where(d => d is >= 0 and <= 6).Distinct().ToList();
        if (m.WorkingDays.Count == 0)
            ModelState.AddModelError("Form.WorkingDays", "Tick at least one day they work - otherwise customers can never book them.");
    }

    private static void Apply(StaffFormModel m, StaffMember s)
    {
        s.Name = m.Name!.Trim();
        s.Title = string.IsNullOrWhiteSpace(m.Title) ? null : m.Title.Trim();
        s.Gender = m.Gender is "F" or "M" ? m.Gender : null;
        s.DaysOffMask = StaffMember.Mask(Enumerable.Range(0, 7).Where(d => !m.WorkingDays.Contains(d)).Select(d => (DayOfWeek)d));
        s.DoesHomeService = m.DoesHomeService;
        s.ConsultationFee = m.ConsultationFee;
        s.Languages = string.IsNullOrWhiteSpace(m.Languages) ? null : m.Languages.Trim();
        s.Bio = string.IsNullOrWhiteSpace(m.Bio) ? null : m.Bio.Trim();
    }
}
