using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The orders the bot took for a client: see them, accept, complete or cancel.
/// Used by staff (any client) and by a client's portal user (their own orders only).
/// </summary>
public class OrdersController : ClientScopedController
{
    private static readonly int[] PageSizes = { 10, 20, 50 };

    private readonly IOrderRepository _orders;
    private readonly IClientRepository _clients;
    private readonly IClientSettingsService _settings;
    private readonly IApiService _api;

    public OrdersController(IOrderRepository orders, IClientRepository clients, IClientSettingsService settings, IApiService api)
    {
        _orders = orders;
        _clients = clients;
        _settings = settings;
        _api = api;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? show = null, int page = 1, string? q = null, string? day = null, int ps = 20)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        if (client.Vertical == VerticalPack.BeautyWellness) return RedirectToAction("Index", "Bookings", new { clientConfigId = client.Id });

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

        if (!PageSizes.Contains(ps)) ps = 20;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];
        var today = DateOnly.FromDateTime(MadeeAutomation.Domain.Regional.LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        DateOnly? forDay = DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;

        var orders = await _orders.GetPagedAsync(client.Id, statuses, page, ps, q, forDay);

        return View(new OrdersPageModel
        {
            Client = client,
            Orders = orders,
            Search = q,
            Day = forDay,
            Today = today,
            Pager = new PagerModel
            {
                Page = orders.Page, PageSize = ps, TotalCount = orders.TotalCount, Controller = "Orders", Noun = "order",
                Route = new()
                {
                    ["clientConfigId"] = client.Id.ToString(), ["show"] = show, ["q"] = q,
                    ["day"] = forDay?.ToString("yyyy-MM-dd"), ["ps"] = ps == 20 ? null : ps.ToString()
                }
            },
            Show = show,
            NewCount = await _orders.CountAsync(client.Id, OrderStatus.New),
            HasPos = hasPos
        });
    }

    /// <summary>The order could not be sent to the client's POS when it was taken: try again.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendToPos(Guid clientConfigId, Guid id, string? returnUrl = null)
    {
        clientConfigId = Scope(clientConfigId);
        var order = await _orders.GetAsync(clientConfigId, id);   // null when it belongs to another client
        if (order is null) return NotFound();

        var error = await _api.SendOrderToPosAsync(clientConfigId, id);
        if (error is null) TempData["Success"] = $"Order {order.Reference} is now in the POS.";
        else TempData["Error"] = $"Order {order.Reference} could still not be sent to the POS: {error}";
        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid clientConfigId, Guid id, OrderStatus status, string? show = null, string? reason = null,
        string? returnUrl = null)
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
            await _orders.SetStatusAsync(clientConfigId, id, status, SignedInAs, reason);
            var done = status switch
            {
                OrderStatus.Accepted => $"Order {order.Reference} accepted.",
                OrderStatus.Completed => $"Order {order.Reference} completed.",
                _ => $"Order {order.Reference} cancelled."
            };

            if (status == OrderStatus.Completed)
            {
                TempData["Success"] = done;
            }
            else
            {
                // Accept / Cancel: the customer is told on the channel they ordered on (WebApi sends it).
                var notice = await _api.NotifyCustomerAboutOrderAsync(clientConfigId, id);
                var channel = Channels.DisplayName(order.Channel);
                if (notice.Outcome == "sent")
                    TempData["Success"] = $"{done} The customer was told on {channel}.";
                else if (notice.Outcome == "off")
                    TempData["Success"] = $"{done} Customer messages are switched off, so please tell the customer yourself.";
                else
                {
                    TempData["Success"] = done;
                    TempData["Error"] = $"The customer could NOT be told automatically ({notice.Detail}) - please message them in the chat.";
                }
            }
        }

        return BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId, show }));
    }
}
