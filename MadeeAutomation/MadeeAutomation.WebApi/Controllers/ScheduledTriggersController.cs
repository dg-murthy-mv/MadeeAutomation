using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Endpoint that a scheduler (e.g. a simple hosted background service, Azure Function Timer,
/// or even a cron job hitting this URL) calls to fire scheduled workflows such as
/// LowStockAlertHandler. Keeping this as an HTTP-triggerable endpoint (rather than only
/// an in-process timer) makes it easy to run outside of the WebApi process later if needed.
/// </summary>
[ApiController]
[Route("api/scheduled-triggers")]
public class ScheduledTriggersController : ControllerBase
{
    private readonly WorkflowEngine _workflowEngine;
    private readonly IWorkflowRepository _workflowRepository;

    public ScheduledTriggersController(WorkflowEngine workflowEngine, IWorkflowRepository workflowRepository)
    {
        _workflowEngine = workflowEngine;
        _workflowRepository = workflowRepository;
    }

    [HttpPost("run/{clientConfigId:guid}")]
    public async Task<IActionResult> RunScheduledForClient(Guid clientConfigId, CancellationToken cancellationToken)
    {
        var workflows = await _workflowRepository.GetActiveWorkflowsAsync(clientConfigId, cancellationToken);
        var scheduled = workflows.Where(w => w.TriggerType == TriggerType.Scheduled).ToList();

        var results = new List<object>();
        foreach (var workflow in scheduled)
        {
            var context = new TriggerContext
            {
                ClientConfigId = workflow.ClientConfigId,
                WorkflowDefinitionId = workflow.Id,
                TriggerType = TriggerType.Scheduled,
                PayloadJson = "{}"
            };

            var success = await _workflowEngine.RunAsync(context, cancellationToken);
            results.Add(new { workflow.Id, workflow.Name, success });
        }

        return Ok(results);
    }
}
