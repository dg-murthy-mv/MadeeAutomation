using MadeeAutomation.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace MadeeAutomation.Admin.Web.Controllers;

public class ExecutionLogsController : Controller
{
    private readonly IExecutionLogRepository _executionLogRepository;
    private readonly ICurrentClientService _currentClientService;

    public ExecutionLogsController(IExecutionLogRepository executionLogRepository, ICurrentClientService currentClientService)
    {
        _executionLogRepository = executionLogRepository;
        _currentClientService = currentClientService;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? search, string? status, int page = 1, int pageSize = 10)
    {
        var allowedSizes = new[] { 5, 10, 25, 50 };
        if (!allowedSizes.Contains(pageSize)) pageSize = 10;

        var result = await _executionLogRepository.GetPagedAsync(clientConfigId, page, pageSize, status, search);
        var client = await _currentClientService.GetClientAsync(clientConfigId);

        ViewBag.ClientConfigId = clientConfigId;
        ViewBag.ClientName = client?.ClientName;
        // Client's own IANA timezone (e.g. "Asia/Bangkok") - falls back to UTC if the client
        // record is missing or the stored TimeZoneId is invalid/unrecognized.
        ViewBag.ClientTimeZoneId = client?.TimeZoneId ?? "UTC";
        ViewBag.Search = search;
        ViewBag.Status = status;
        ViewBag.PageSize = pageSize;
        return View(result);
    }
}
