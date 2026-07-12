using MadeeAutomation.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

public class ExecutionLogsController : Controller
{
    private readonly IExecutionLogRepository _executionLogRepository;

    public ExecutionLogsController(IExecutionLogRepository executionLogRepository)
    {
        _executionLogRepository = executionLogRepository;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, string? search, string? status, int page = 1)
    {
        const int pageSize = 15;
        var result = await _executionLogRepository.GetPagedAsync(clientConfigId, page, pageSize, status, search);

        ViewBag.ClientConfigId = clientConfigId;
        ViewBag.Search = search;
        ViewBag.Status = status;
        return View(result);
    }
}
