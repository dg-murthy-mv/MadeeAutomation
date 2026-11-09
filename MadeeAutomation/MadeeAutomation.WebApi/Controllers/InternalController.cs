using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.WebApi.Security;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

public class WorkflowTestRequest
{
    public string Message { get; set; } = string.Empty;
    public string? Channel { get; set; }
}

/// <summary>Endpoints for Admin.Web only (header X-Internal-Key).</summary>
[ApiController]
[Route("api/internal")]
[RequireInternalKey]
public class InternalController : ControllerBase
{
    private readonly WorkflowEngine _engine;
    private readonly IWorkflowRepository _workflows;

    public InternalController(WorkflowEngine engine, IWorkflowRepository workflows)
    {
        _engine = engine;
        _workflows = workflows;
    }

    /// <summary>
    /// Admin "Test" button: runs a message workflow with a sample customer message. The AI really runs,
    /// but nothing is sent and no POSFirst order is created. The run is logged with customer "admin-test".
    /// </summary>
    [HttpPost("workflows/{workflowId:guid}/test")]
    public async Task<IActionResult> TestWorkflow(Guid workflowId, [FromBody] WorkflowTestRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Type a customer message to test with." });

        var workflow = await _workflows.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null) return NotFound(new { error = "Workflow not found." });
        if (workflow.TriggerType != TriggerType.MessageReceived)
            return BadRequest(new { error = "Only customer-message workflows can be tested here." });

        var channel = Channels.Normalize(request.Channel) ?? workflow.Channel ?? Channels.Line;
        var message = request.Message.Trim();

        var outcome = await _engine.RunAsync(new TriggerContext
        {
            ClientConfigId = workflow.ClientConfigId,
            WorkflowDefinitionId = workflow.Id,
            TriggerType = TriggerType.MessageReceived,
            Channel = channel,
            CustomerId = WorkflowEngine.TestCustomerId,
            MessageText = message,
            IsTest = true,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { message, from = WorkflowEngine.TestCustomerId, channel, test = true })
        }, cancellationToken);

        return Ok(new
        {
            status = outcome.Status.ToString(),
            summary = outcome.Summary,
            error = outcome.Error,
            executionLogId = outcome.ExecutionLogId,
            actions = outcome.Actions.Select(a => new { type = a.Type, parameters = a.Parameters })
        });
    }
}
