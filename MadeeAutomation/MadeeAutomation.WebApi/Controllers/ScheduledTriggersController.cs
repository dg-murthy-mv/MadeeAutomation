using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.WebApi.Security;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Runs a client's scheduled workflows (e.g. LowStockAlertHandler) when called.
/// Protected: callers must send header X-Internal-Key (InternalApi:Key). A real scheduler that reads
/// the cron in TriggerConfigJson comes in Phase 3 - until then call it from a Windows scheduled task:
///   curl -X POST -H "X-Internal-Key: ..." https://localhost:58715/api/scheduled-triggers/run/{clientConfigId}
/// </summary>
[ApiController]
[RequireInternalKey]
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
