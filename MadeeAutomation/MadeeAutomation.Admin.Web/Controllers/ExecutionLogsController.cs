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

    public async Task<IActionResult> Index(Guid clientConfigId)
    {
        var logs = await _executionLogRepository.GetRecentAsync(clientConfigId);
        ViewBag.ClientConfigId = clientConfigId;
        return View(logs);
    }
}
