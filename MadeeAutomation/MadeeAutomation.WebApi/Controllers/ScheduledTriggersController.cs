using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Runs a client's scheduled workflows (e.g. LowStockAlertHandler) when called.
/// A real scheduler that reads the cron in TriggerConfigJson, and protection on this endpoint,
/// come in Phase 2/3 - until then call it from a scheduled task you trust.
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

            var outcome = await _workflowEngine.RunAsync(context, cancellationToken);
            results.Add(new { workflow.Id, workflow.Name, status = outcome.Status.ToString(), outcome.ExecutionLogId, outcome.Message });
        }

        return Ok(results);
    }
}
