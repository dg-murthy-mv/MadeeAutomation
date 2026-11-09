using MadeeAutomation.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

public class ExecutionLogsController : Controller
{
    private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

    private readonly IExecutionLogRepository _executionLogRepository;
    private readonly ICurrentClientService _currentClientService;

    public ExecutionLogsController(IExecutionLogRepository executionLogRepository, ICurrentClientService currentClientService)
    {
        _executionLogRepository = executionLogRepository;
        _currentClientService = currentClientService;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? search, string? status, int page = 1, int pageSize = 10)
    {
        if (!AllowedPageSizes.Contains(pageSize)) pageSize = 10;

        var client = await _currentClientService.GetClientAsync(clientConfigId);
        if (client is null) return NotFound();

        var result = await _executionLogRepository.GetPagedAsync(clientConfigId, page, pageSize, status, search);

        ViewBag.ClientConfigId = clientConfigId;
        ViewBag.ClientName = client.ClientName;
        ViewBag.ClientTimeZoneId = client.TimeZoneId;
        ViewBag.Search = search;
        ViewBag.Status = status;
        ViewBag.PageSize = pageSize;
        return View(result);
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var log = await _executionLogRepository.GetByIdAsync(id);
        if (log is null) return NotFound();

        var client = await _currentClientService.GetClientAsync(log.ClientConfigId);
        ViewBag.ClientName = client?.ClientName;
        ViewBag.ClientTimeZoneId = client?.TimeZoneId ?? "UTC";
        return View(log);
    }
}
