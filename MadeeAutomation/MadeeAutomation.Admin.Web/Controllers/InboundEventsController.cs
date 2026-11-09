using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// Every message a webhook received for a client - including ones that never became a run
/// (Skipped: client paused, no workflow for the channel; Failed: could not be processed).
/// </summary>
public class InboundEventsController : Controller
{
    private static readonly int[] AllowedPageSizes = { 10, 25, 50 };

    private readonly IInboundEventRepository _events;
    private readonly ICurrentClientService _clients;

    public InboundEventsController(IInboundEventRepository events, ICurrentClientService clients)
    {
        _events = events;
        _clients = clients;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? status, int page = 1, int pageSize = 25)
    {
        var client = await _clients.GetClientAsync(clientConfigId);
        if (client is null) return NotFound();
        if (!AllowedPageSizes.Contains(pageSize)) pageSize = 25;

        InboundEventStatus? statusFilter = Enum.TryParse<InboundEventStatus>(status, true, out var s) ? s : null;
        var result = await _events.GetPagedAsync(clientConfigId, page, pageSize, statusFilter);

        ViewBag.Client = client;
        ViewBag.Status = statusFilter?.ToString();
        ViewBag.PageSize = pageSize;
        return View(result);
    }
}
