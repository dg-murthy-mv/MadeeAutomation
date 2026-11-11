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
    private readonly MadeeAutomation.Application.VerticalPacks.Messaging.OrderCustomerNotifier _orderNotifier;
    private readonly MadeeAutomation.Application.VerticalPacks.Messaging.OrderDesk _orderDesk;
    private readonly ICurrentClientService _clients;

    public InternalController(WorkflowEngine engine, IWorkflowRepository workflows,
        MadeeAutomation.Application.VerticalPacks.Messaging.OrderCustomerNotifier orderNotifier,
        MadeeAutomation.Application.VerticalPacks.Messaging.OrderDesk orderDesk, ICurrentClientService clients)
    {
        _engine = engine;
        _workflows = workflows;
        _orderNotifier = orderNotifier;
        _orderDesk = orderDesk;
        _clients = clients;
    }

    /// <summary>
    /// "Send to POS again" for an order whose first send to the client's POS failed.
    /// Answers { sent: true } or { sent: false, error }.
    /// </summary>
    [HttpPost("clients/{clientConfigId:guid}/orders/{orderId:guid}/send-to-pos")]
    public async Task<IActionResult> SendToPos(Guid clientConfigId, Guid orderId, CancellationToken cancellationToken)
    {
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        var error = await _orderDesk.ResendToPosAsync(clientConfigId, orderId, client, cancellationToken);
        return Ok(new { sent = error is null, error });
    }

    /// <summary>
    /// Called by Admin.Web right after staff accept or cancel an order: tells the customer on the channel
    /// they ordered on, in their own language. Answers { outcome: sent | failed | off | skipped, detail }.
    /// </summary>
    [HttpPost("clients/{clientConfigId:guid}/orders/{orderId:guid}/notify-customer")]
    public async Task<IActionResult> NotifyCustomer(Guid clientConfigId, Guid orderId, CancellationToken cancellationToken)
    {
        var result = await _orderNotifier.NotifyAsync(clientConfigId, orderId, cancellationToken);
        return Ok(new { outcome = result.Outcome, detail = result.Detail });
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
