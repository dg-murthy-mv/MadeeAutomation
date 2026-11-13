using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// What a beauty / wellness client sells by time: services with duration, price, clean-up time,
/// home service, and who can do each one. Used by staff (any client) and the client's portal user.
/// </summary>
public class ServicesController : ClientScopedController
{
    private readonly IBookingRepository _bookings;
    private readonly IClientRepository _clients;
    private readonly IBusinessProfileRepository _profile;

    public ServicesController(IBookingRepository bookings, IClientRepository clients, IBusinessProfileRepository profile)
    {
        _bookings = bookings;
        _clients = clients;
        _profile = profile;
    }

    public async Task<IActionResult> Index(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(await PageAsync(client, new ServiceFormModel { ClientConfigId = client.Id }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add([Bind(Prefix = "NewService")] ServiceFormModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(model.ClientConfigId));
        if (client is null) return NotFound();
        model.ClientConfigId = client.Id;
        model.Name = (model.Name ?? "").Trim();

        if (ModelState.IsValid && await _bookings.ServiceNameExistsAsync(client.Id, model.Name))
            ModelState.AddModelError("NewService.Name", "There is already a service with that name.");
        if (!ModelState.IsValid) return View("Index", await PageAsync(client, model));

        var existing = await _bookings.GetServicesAsync(client.Id);
        var service = new ServiceItem
        {
            ClientConfigId = client.Id,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(s => s.SortOrder) + 1
        };
        Apply(model, service);
        await _bookings.AddServiceAsync(service, model.StaffIds);

        TempData["Success"] = model.StaffIds.Count == 0
            ? $"\"{service.Name}\" added. Tick who can do it (Edit), or customers can't book it yet."
            : $"\"{service.Name}\" added.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    public async Task<IActionResult> Edit(Guid id, string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        var service = await _bookings.GetServiceAsync(id);
        if (service is null || !CanAccess(service.ClientConfigId)) return NotFound();
        var client = await _clients.GetWithWorkflowsAsync(service.ClientConfigId);
        if (client is null) return NotFound();

        var staff = await _bookings.GetStaffAsync(client.Id);
        return View(new ServiceEditModel
        {
            Client = client,
            Staff = staff,
            Form = new ServiceFormModel
            {
                Id = service.Id, ClientConfigId = service.ClientConfigId, Name = service.Name, Category = service.Category,
                Description = service.Description, DurationMinutes = service.DurationMinutes, BufferMinutes = service.BufferMinutes,
                Price = service.Price, HomeServiceAllowed = service.HomeServiceAllowed, IsActive = service.IsActive, SortOrder = service.SortOrder,
                StaffIds = staff.Where(m => m.Services.Any(x => x.ServiceItemId == service.Id)).Select(m => m.Id).ToList()
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, [Bind(Prefix = "Form")] ServiceFormModel model, string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        var service = await _bookings.GetServiceAsync(id);
        if (service is null || !CanAccess(service.ClientConfigId)) return NotFound();
        var client = await _clients.GetWithWorkflowsAsync(service.ClientConfigId);
        if (client is null) return NotFound();

        model.Id = service.Id;
        model.ClientConfigId = service.ClientConfigId;
        model.Name = (model.Name ?? "").Trim();
        if (ModelState.IsValid && await _bookings.ServiceNameExistsAsync(client.Id, model.Name, service.Id))
            ModelState.AddModelError("Form.Name", "There is already a service with that name.");
        if (!ModelState.IsValid)
            return View(new ServiceEditModel { Client = client, Form = model, Staff = await _bookings.GetStaffAsync(client.Id) });

        Apply(model, service);
        service.IsActive = model.IsActive;
        service.SortOrder = model.SortOrder;
        await _bookings.UpdateServiceAsync(service, model.StaffIds);

        TempData["Success"] = $"\"{service.Name}\" saved.";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = client.Id }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(Guid id, string? returnUrl = null)
    {
        var service = await _bookings.GetServiceAsync(id);
        if (service is null || !CanAccess(service.ClientConfigId)) return NotFound();
        var staff = await _bookings.GetStaffAsync(service.ClientConfigId);
        service.IsActive = !service.IsActive;
        await _bookings.UpdateServiceAsync(service, staff.Where(m => m.Services.Any(x => x.ServiceItemId == service.Id)).Select(m => m.Id));
        TempData["Success"] = service.IsActive ? $"\"{service.Name}\" is offered again." : $"\"{service.Name}\" is hidden from customers.";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = service.ClientConfigId }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, string? returnUrl = null)
    {
        var service = await _bookings.GetServiceAsync(id);
        if (service is null || !CanAccess(service.ClientConfigId)) return NotFound();
        await _bookings.DeleteServiceAsync(id);
        TempData["Success"] = $"\"{service.Name}\" deleted. Bookings already made keep its name and price.";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId = service.ClientConfigId }));
    }

    private async Task<ServicesPageModel> PageAsync(ClientConfig client, ServiceFormModel newService) => new()
    {
        Client = client,
        Services = await _bookings.GetServicesAsync(client.Id),
        Staff = await _bookings.GetStaffAsync(client.Id),
        NewService = newService,
        HoursConfigured = (await _profile.GetHoursAsync(client.Id)).Count > 0
    };

    private static void Apply(ServiceFormModel m, ServiceItem s)
    {
        s.Name = m.Name!.Trim();
        s.Category = string.IsNullOrWhiteSpace(m.Category) ? null : m.Category.Trim();
        s.Description = string.IsNullOrWhiteSpace(m.Description) ? null : m.Description.Trim();
        s.DurationMinutes = m.DurationMinutes;
        s.BufferMinutes = m.BufferMinutes;
        s.Price = m.Price;
        s.HomeServiceAllowed = m.HomeServiceAllowed;
    }
}
