using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Application.Inbound;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Entry point for all inbound events.
///
/// Customer messages (LINE / WhatsApp / Messenger) are NOT processed inside the request any more:
/// the controller checks the signature, stores every message as an InboundEvent (duplicates are
/// ignored) and answers 200 straight away. The InboundEventWorker then runs the workflow and replies.
/// That stops the platforms timing out and re-sending, which used to create duplicate orders.
///
/// URLs
///   POST /api/webhooks/line/{clientCode}                       one URL per LINE Official Account (recommended)
///   POST /api/webhooks/line/{clientCode}/{workflowId}          old per-workflow URL - still works
///   GET+POST /api/webhooks/whatsapp/{clientCode}[/{workflowId}]
///   GET+POST /api/webhooks/messenger/{clientCode}[/{workflowId}]
///   POST /api/webhooks/generic/{clientCode}/{workflowId}       runs synchronously (forms, POS events)
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly WorkflowEngine _workflowEngine;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly ICurrentClientService _clients;
    private readonly IClientSettingsService _settings;
    private readonly InboundEventIngestor _ingestor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(WorkflowEngine workflowEngine, IWorkflowRepository workflowRepository,
        ICurrentClientService clients, IClientSettingsService settings, InboundEventIngestor ingestor,
        IConfiguration configuration, ILogger<WebhooksController> logger)
    {
        _workflowEngine = workflowEngine;
        _workflowRepository = workflowRepository;
        _clients = clients;
        _settings = settings;
        _ingestor = ingestor;
        _configuration = configuration;
        _logger = logger;
    }

    // =====================================================================
    // LINE
    // =====================================================================

    [HttpPost("line/{clientCode}")]
    public Task<IActionResult> Line(string clientCode, CancellationToken cancellationToken) =>
        ReceiveLineAsync(clientCode, null, cancellationToken);

    [HttpPost("line/{clientCode}/{workflowId:guid}")]
    public Task<IActionResult> LineForWorkflow(string clientCode, Guid workflowId, CancellationToken cancellationToken) =>
        ReceiveLineAsync(clientCode, workflowId, cancellationToken);

    private async Task<IActionResult> ReceiveLineAsync(string clientCode, Guid? workflowId, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);

        var (client, notFound) = await ResolveClientAsync(clientCode, workflowId, cancellationToken);
        if (client is null) return notFound!;

        // LINE signs every call with the channel secret. Without the secret we cannot tell a real
        // LINE call from a fake one, so by default the call is refused (Webhooks:RequireLineSignature).
        var settings = await _settings.GetAsync(client.Id, cancellationToken);
        var signature = Request.Headers["X-Line-Signature"].ToString();

        if (settings.HasLineSecret)
        {
            if (!LineSignature.IsValid(settings.LineChannelSecret, body, signature))
            {
                _logger.LogWarning("LINE webhook for {ClientCode}: invalid X-Line-Signature - rejected", client.ClientCode);
                return Unauthorized(new { error = "Invalid signature." });
            }
        }
        else if (_configuration.GetValue("Webhooks:RequireLineSignature", true))
        {
            _logger.LogWarning("LINE webhook for {ClientCode} rejected: no LINE channel secret saved for this client. " +
                               "Add it in Admin -> client -> Edit -> LINE.", client.ClientCode);
            return Unauthorized(new { error = "LINE channel secret is not configured for this client." });
        }

        var parsed = ChannelPayloadParser.ParseLine(Encoding.UTF8.GetString(body));
        if (parsed.Error is not null) return BadRequest(new { error = parsed.Error });

        var result = await _ingestor.IngestAsync(client, Channels.Line, workflowId, parsed, cancellationToken);
        return Ok(new { received = result.Stored, duplicates = result.Duplicates, ignored = result.Ignored });
    }

    // =====================================================================
    // WhatsApp / Messenger (Meta) - signature check (X-Hub-Signature-256) comes in Phase 4
    // =====================================================================

    /// <summary>
    /// Meta's "Verify and save" check. Meta calls the SAME URL you register, so this answers on
    /// every WhatsApp / Messenger route shape.
    /// </summary>
    [HttpGet("whatsapp")]
    [HttpGet("whatsapp/{clientCode}")]
    [HttpGet("whatsapp/{clientCode}/{workflowId:guid}")]
    public IActionResult VerifyWhatsAppWebhook(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge) =>
        VerifyMeta(mode, verifyToken, challenge, _configuration["Connectors:WhatsApp:VerifyToken"]);

    [HttpGet("messenger")]
    [HttpGet("messenger/{clientCode}")]
    [HttpGet("messenger/{clientCode}/{workflowId:guid}")]
    public IActionResult VerifyMessengerWebhook(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge) =>
        VerifyMeta(mode, verifyToken, challenge, _configuration["Connectors:Messenger:VerifyToken"]);

    private static IActionResult VerifyMeta(string? mode, string? verifyToken, string? challenge, string? expectedToken)
    {
        var configured = !string.IsNullOrWhiteSpace(expectedToken) && !expectedToken.StartsWith("SET_", StringComparison.OrdinalIgnoreCase);
        if (configured && mode == "subscribe" && verifyToken == expectedToken && !string.IsNullOrEmpty(challenge))
            return new ContentResult { Content = challenge, ContentType = "text/plain", StatusCode = 200 };

        // Plain 403 - Forbid() needs an authentication scheme and used to crash with a 500.
        return new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    [HttpPost("whatsapp/{clientCode}")]
    public Task<IActionResult> WhatsApp(string clientCode, CancellationToken cancellationToken) =>
        ReceiveMetaAsync(Channels.WhatsApp, clientCode, null, cancellationToken);

    [HttpPost("whatsapp/{clientCode}/{workflowId:guid}")]
    public Task<IActionResult> WhatsAppForWorkflow(string clientCode, Guid workflowId, CancellationToken cancellationToken) =>
        ReceiveMetaAsync(Channels.WhatsApp, clientCode, workflowId, cancellationToken);

    [HttpPost("messenger/{clientCode}")]
    public Task<IActionResult> Messenger(string clientCode, CancellationToken cancellationToken) =>
        ReceiveMetaAsync(Channels.Messenger, clientCode, null, cancellationToken);

    [HttpPost("messenger/{clientCode}/{workflowId:guid}")]
    public Task<IActionResult> MessengerForWorkflow(string clientCode, Guid workflowId, CancellationToken cancellationToken) =>
        ReceiveMetaAsync(Channels.Messenger, clientCode, workflowId, cancellationToken);

    private async Task<IActionResult> ReceiveMetaAsync(string channel, string clientCode, Guid? workflowId, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);

        var (client, notFound) = await ResolveClientAsync(clientCode, workflowId, cancellationToken);
        if (client is null) return notFound!;

        var parsed = ChannelPayloadParser.Parse(channel, Encoding.UTF8.GetString(body));
        if (parsed.Error is not null) return BadRequest(new { error = parsed.Error });

        var result = await _ingestor.IngestAsync(client, channel, workflowId, parsed, cancellationToken);
        return Ok(new { received = result.Stored, duplicates = result.Duplicates, ignored = result.Ignored });
    }

    // =====================================================================
    // Generic (synchronous) - lead forms, POS events, etc.
    // =====================================================================

    [HttpPost("generic/{clientCode}/{workflowId:guid}")]
    public async Task<IActionResult> Generic(string clientCode, Guid workflowId, [FromBody] JsonElement rawPayload, CancellationToken cancellationToken)
    {
        var (client, notFound) = await ResolveClientAsync(clientCode, workflowId, cancellationToken);
        if (client is null) return notFound!;

        var context = new TriggerContext
        {
            ClientConfigId = client.Id,
            WorkflowDefinitionId = workflowId,
            TriggerType = TriggerType.Webhook,
            PayloadJson = rawPayload.GetRawText()
        };

        var outcome = await _workflowEngine.RunAsync(context, cancellationToken);
        if (outcome.ExecutionLogId is null) return NotFound(new { status = "not-run", reason = outcome.Message });

        return outcome.Succeeded || outcome.Status == ExecutionStatus.Skipped
            ? Ok(new { status = outcome.Status.ToString(), executionLogId = outcome.ExecutionLogId })
            : StatusCode(500, new { status = outcome.Status.ToString(), executionLogId = outcome.ExecutionLogId, error = outcome.Message });
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private async Task<byte[]> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>
    /// Finds the client by code. When the URL also names a workflow, that workflow must exist and
    /// belong to the same client - a mismatched code/workflow pair is treated as not found.
    /// </summary>
    private async Task<(ClientConfig? client, IActionResult? notFound)> ResolveClientAsync(
        string clientCode, Guid? workflowId, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByCodeAsync(clientCode, cancellationToken);
        if (client is null)
        {
            _logger.LogWarning("Webhook for unknown client code '{ClientCode}'", clientCode);
            return (null, NotFound(new { error = $"Unknown client '{clientCode}'." }));
        }

        if (workflowId is not null)
        {
            var workflow = await _workflowRepository.GetByIdAsync(workflowId.Value, cancellationToken);
            if (workflow is null || workflow.ClientConfigId != client.Id)
            {
                _logger.LogWarning("Webhook for {ClientCode}: workflow {WorkflowId} not found for this client", clientCode, workflowId);
                return (null, NotFound(new { error = "Workflow not found for this client." }));
            }
        }

        return (client, null);
    }
}
