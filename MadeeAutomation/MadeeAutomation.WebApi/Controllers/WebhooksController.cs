using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Single entry point for all inbound events: WhatsApp/LINE messages, POS webhooks,
/// lead-form submissions, file uploads. Each route resolves a specific ClientConfig +
/// WorkflowDefinition, wraps the payload into a TriggerContext, and hands off to the
/// WorkflowEngine. New client integrations rarely need a new controller action -
/// most reuse "generic" and are told which workflow to run by the client's WhatsApp
/// number / webhook path stored in WorkflowDefinition.TriggerConfigJson.
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly WorkflowEngine _workflowEngine;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(WorkflowEngine workflowEngine, IWorkflowRepository workflowRepository, ILogger<WebhooksController> logger)
    {
        _workflowEngine = workflowEngine;
        _workflowRepository = workflowRepository;
        _logger = logger;
    }

    /// <summary>
    /// Generic webhook entry point. clientCode + workflowId identify which automation to run;
    /// the raw JSON body becomes the TriggerContext payload.
    /// e.g. POST /api/webhooks/generic/{clientCode}/{workflowId}
    /// </summary>
    [HttpPost("generic/{clientCode}/{workflowId:guid}")]
    public async Task<IActionResult> Generic(string clientCode, Guid workflowId, [FromBody] object rawPayload, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return NotFound($"Workflow {workflowId} not found.");
        }

        var context = new TriggerContext
        {
            ClientConfigId = workflow.ClientConfigId,
            WorkflowDefinitionId = workflow.Id,
            TriggerType = TriggerType.Webhook,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(rawPayload)
        };

        var success = await _workflowEngine.RunAsync(context, cancellationToken);
        return success ? Ok(new { status = "processed" }) : StatusCode(500, new { status = "failed" });
    }

    /// <summary>
    /// WhatsApp Business API webhook. Meta requires a GET verification challenge and a POST
    /// for actual message delivery - both are handled here.
    /// </summary>
    [HttpGet("whatsapp")]
    public IActionResult VerifyWhatsAppWebhook(
        [FromQuery(Name = "hub.mode")] string mode,
        [FromQuery(Name = "hub.verify_token")] string verifyToken,
        [FromQuery(Name = "hub.challenge")] string challenge,
        [FromServices] IConfiguration configuration)
    {
        var expectedToken = configuration["Connectors:WhatsApp:VerifyToken"];
        if (mode == "subscribe" && verifyToken == expectedToken)
        {
            return Ok(challenge);
        }
        return Forbid();
    }

    [HttpPost("whatsapp/{clientCode}/{workflowId:guid}")]
    public async Task<IActionResult> WhatsApp(string clientCode, Guid workflowId, [FromBody] System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null) return NotFound();

        // Meta's payload: entry[0].changes[0].value.messages[0].{from, text.body}
        // Non-message webhooks (delivery/read receipts) won't have a "messages" array - ignore those.
        string? messageText = null;
        string? fromNumber = null;

        if (payload.TryGetProperty("entry", out var entries) && entries.GetArrayLength() > 0)
        {
            var change = entries[0].GetProperty("changes")[0].GetProperty("value");
            if (change.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0)
            {
                var msg = messages[0];
                fromNumber = msg.GetProperty("from").GetString();
                if (msg.TryGetProperty("text", out var textEl))
                {
                    messageText = textEl.GetProperty("body").GetString();
                }
            }
        }

        if (messageText is null || fromNumber is null)
        {
            return Ok(); // not a text message event (e.g. a status update) - nothing to process
        }

        var simplifiedPayload = System.Text.Json.JsonSerializer.Serialize(new { message = messageText, from = fromNumber });

        var context = new TriggerContext
        {
            ClientConfigId = workflow.ClientConfigId,
            WorkflowDefinitionId = workflow.Id,
            TriggerType = TriggerType.MessageReceived,
            PayloadJson = simplifiedPayload
        };

        var success = await _workflowEngine.RunAsync(context, cancellationToken);
        return success ? Ok() : StatusCode(500);
    }

    /// <summary>
    /// LINE Messaging API webhook. LINE's payload shape: events[0].message.text / events[0].source.userId.
    /// Unlike Meta, LINE has no GET verification challenge - only signature verification via the
    /// x-line-signature header, which is skipped here for now (same simplification as the WhatsApp
    /// route currently has - add signature checks before this touches real client data).
    /// </summary>
    [HttpPost("line/{clientCode}/{workflowId:guid}")]
    public async Task<IActionResult> Line(string clientCode, Guid workflowId, [FromBody] System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null) return NotFound();

        string? messageText = null;
        string? fromUserId = null;

        if (payload.TryGetProperty("events", out var events) && events.GetArrayLength() > 0)
        {
            var evt = events[0];
            if (evt.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "message"
                && evt.TryGetProperty("message", out var msgEl)
                && msgEl.TryGetProperty("type", out var msgTypeEl) && msgTypeEl.GetString() == "text")
            {
                messageText = msgEl.GetProperty("text").GetString();
                if (evt.TryGetProperty("source", out var sourceEl) && sourceEl.TryGetProperty("userId", out var userIdEl))
                {
                    fromUserId = userIdEl.GetString();
                }
            }
        }

        if (messageText is null || fromUserId is null)
        {
            return Ok(); // not a text message event - nothing to process
        }

        var simplifiedPayload = System.Text.Json.JsonSerializer.Serialize(new { message = messageText, from = fromUserId });

        var context = new TriggerContext
        {
            ClientConfigId = workflow.ClientConfigId,
            WorkflowDefinitionId = workflow.Id,
            TriggerType = TriggerType.MessageReceived,
            PayloadJson = simplifiedPayload
        };

        var success = await _workflowEngine.RunAsync(context, cancellationToken);
        return success ? Ok() : StatusCode(500);
    }
}
