using MadeeAutomation.Domain.Enums;
using System.Globalization;
using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The client portal: a client's own user sees their start page and edits their business information.
/// The client is always taken from the sign-in, never from the URL. Staff are sent to the client list
/// (they use the client's own page in Admin instead).
/// </summary>
[ClientPortal]
public class PortalController : Controller
{
    private readonly IClientRepository _clients;
    private readonly IBusinessProfileRepository _profile;
    private readonly IOrderRepository _orders;
    private readonly ILogger<PortalController> _logger;

    public PortalController(IClientRepository clients, IBusinessProfileRepository profile, IOrderRepository orders, ILogger<PortalController> logger)
    {
        _clients = clients;
        _profile = profile;
        _orders = orders;
        _logger = logger;
    }

    private async Task<ClientConfig?> OwnClientAsync() =>
        ClientAccess.ClientId(User) is { } id ? await _clients.GetWithWorkflowsAsync(id) : null;

    public async Task<IActionResult> Index()
    {
        if (!ClientAccess.IsClientUser(User)) return RedirectToAction("Index", "Clients");
        var client = await OwnClientAsync();
        if (client is null) return NotFound();

        // A spa / salon's day starts with its bookings.
        if (client.Vertical == VerticalPack.BeautyWellness) return RedirectToAction("Index", "Bookings");

        var utcNow = DateTime.UtcNow;
        var localNow = LocalTime.ToLocal(utcNow, client.TimeZoneId);
        var today = DateOnly.FromDateTime(localNow);
        var menu = await _profile.GetMenuAsync(client.Id);
        var hours = BusinessClock.GetStatus(await _profile.GetHoursAsync(client.Id),
            await _profile.GetClosedDatesAsync(client.Id, today.AddDays(-1)), client.TimeZoneId, utcNow);
        var latest = await _orders.GetPagedAsync(client.Id, new[] { OrderStatus.New }, 1, 5);

        return View(new PortalHomeModel
        {
            Client = client,
            Hours = hours,
            LocalNowText = localNow.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture),
            MenuItemCount = menu.Count,
            SoldOutCount = menu.Count(m => !m.IsAvailable),
            NewOrderCount = latest.TotalCount,
            LatestNewOrders = latest.Items
        });
    }

    public async Task<IActionResult> BusinessInfo()
    {
        if (!ClientAccess.IsClientUser(User)) return RedirectToAction("Index", "Clients");
        var client = await OwnClientAsync();
        if (client is null) return NotFound();

        return View(new BusinessInfoModel
        {
            ClientName = client.ClientName,
            KnowledgeBaseText = client.KnowledgeBaseText,
            StaffAlertEmail = client.StaffAlertEmail,
            NotifyCustomerOnOrderStatus = client.NotifyCustomerOnOrderStatus
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessInfo(BusinessInfoModel model)
    {
        if (!ClientAccess.IsClientUser(User)) return RedirectToAction("Index", "Clients");
        var client = await OwnClientAsync();
        if (client is null) return NotFound();

        model.ClientName = client.ClientName;
        if (!ModelState.IsValid) return View(model);

        client.KnowledgeBaseText = string.IsNullOrWhiteSpace(model.KnowledgeBaseText) ? null : model.KnowledgeBaseText.Trim();
        client.StaffAlertEmail = string.IsNullOrWhiteSpace(model.StaffAlertEmail) ? null : model.StaffAlertEmail.Trim();
        client.NotifyCustomerOnOrderStatus = model.NotifyCustomerOnOrderStatus;
        await _clients.UpdateAsync(client);

        _logger.LogInformation("{By} updated the business information of {ClientCode} in the portal", User.Identity?.Name, client.ClientCode);
        TempData["Success"] = "Saved. The bot uses the new information from the next message.";
        return RedirectToAction(nameof(BusinessInfo));
    }
}
