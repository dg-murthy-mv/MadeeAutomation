using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The orders the bot took for a client: see them, accept, complete or cancel.
/// Used by staff (any client) and by a client's portal user (their own orders only).
/// </summary>
public class OrdersController : ClientScopedController
{
    private const int PageSize = 20;

    private readonly IOrderRepository _orders;
    private readonly IClientRepository _clients;
    private readonly IClientSettingsService _settings;

    public OrdersController(IOrderRepository orders, IClientRepository clients, IClientSettingsService settings)
    {
        _orders = orders;
        _clients = clients;
        _settings = settings;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? show = null, int page = 1)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();

        show = show is "new" or "done" or "all" ? show : "open";
        IReadOnlyCollection<OrderStatus>? statuses = show switch
        {
            "new" => new[] { OrderStatus.New },
            "done" => new[] { OrderStatus.Completed, OrderStatus.Cancelled },
            "all" => null,
            _ => new[] { OrderStatus.New, OrderStatus.Accepted }
        };

        bool hasPos;
        try { hasPos = _settings.Read(client.IntegrationSettingsJson).HasPosFirst; }
        catch { hasPos = false; }   // settings can't be read (encryption key) - the list still works

        return View(new OrdersPageModel
        {
            Client = client,
            Orders = await _orders.GetPagedAsync(client.Id, statuses, page, PageSize),
            Show = show,
            NewCount = await _orders.CountAsync(client.Id, OrderStatus.New),
            HasPos = hasPos
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid clientConfigId, Guid id, OrderStatus status, string? show = null)
    {
        clientConfigId = Scope(clientConfigId);
        var order = await _orders.GetAsync(clientConfigId, id);   // null when it belongs to another client
        if (order is null) return NotFound();

        var allowed = (order.Status, status) switch
        {
            (OrderStatus.New, OrderStatus.Accepted) => true,
            (OrderStatus.New, OrderStatus.Cancelled) => true,
            (OrderStatus.Accepted, OrderStatus.Completed) => true,
            (OrderStatus.Accepted, OrderStatus.Cancelled) => true,
            _ => false
        };

        if (!allowed)
        {
            TempData["Error"] = $"Order {order.Reference} is {order.Status} - it can't be changed to {status}.";
        }
        else
        {
            await _orders.SetStatusAsync(clientConfigId, id, status, SignedInAs);
            TempData["Success"] = status switch
            {
                OrderStatus.Accepted => $"Order {order.Reference} accepted.",
                OrderStatus.Completed => $"Order {order.Reference} completed.",
                _ => $"Order {order.Reference} cancelled. Please tell the customer in the chat - the bot does not message them about this."
            };
        }

        return RedirectToAction(nameof(Index), new { clientConfigId, show });
    }
}
