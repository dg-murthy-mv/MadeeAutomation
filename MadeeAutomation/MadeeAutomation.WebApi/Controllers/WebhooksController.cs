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
    // WhatsApp / Messenger (Meta)
    // =====================================================================

    /// <summary>
    /// Meta's "Verify and save" check: Meta calls the URL with hub.verify_token and expects hub.challenge back.
    /// The token is the client's own (Edit client -> Meta app) or, if blank, WebApi's Connectors:Meta:VerifyToken
    /// (older setting names Connectors:WhatsApp:VerifyToken / Connectors:Messenger:VerifyToken still work).
    /// </summary>
    [HttpGet("whatsapp")]
    [HttpGet("whatsapp/{clientCode}")]
    [HttpGet("whatsapp/{clientCode}/{workflowId:guid}")]
    public Task<IActionResult> VerifyWhatsAppWebhook(string? clientCode,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        CancellationToken cancellationToken) =>
        VerifyMetaAsync(Channels.WhatsApp, clientCode, mode, verifyToken, challenge, cancellationToken);

    [HttpGet("messenger")]
    [HttpGet("messenger/{clientCode}")]
    [HttpGet("messenger/{clientCode}/{workflowId:guid}")]
    public Task<IActionResult> VerifyMessengerWebhook(string? clientCode,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        CancellationToken cancellationToken) =>
        VerifyMetaAsync(Channels.Messenger, clientCode, mode, verifyToken, challenge, cancellationToken);

    private async Task<IActionResult> VerifyMetaAsync(string channel, string? clientCode, string? mode, string? verifyToken,
        string? challenge, CancellationToken cancellationToken)
    {
        var expected = new List<string?>();
        if (!string.IsNullOrWhiteSpace(clientCode) && await _clients.GetByCodeAsync(clientCode, cancellationToken) is { } client)
            expected.Add((await _settings.GetAsync(client.Id, cancellationToken)).MetaVerifyToken);
        expected.Add(_configuration["Connectors:Meta:VerifyToken"]);
        expected.Add(_configuration[channel == Channels.WhatsApp ? "Connectors:WhatsApp:VerifyToken" : "Connectors:Messenger:VerifyToken"]);

        var tokens = expected.Where(t => !string.IsNullOrWhiteSpace(t) && !t!.StartsWith("SET_", StringComparison.OrdinalIgnoreCase))
            .Select(t => t!.Trim()).ToList();
        if (mode == "subscribe" && !string.IsNullOrEmpty(challenge) && !string.IsNullOrWhiteSpace(verifyToken)
            && tokens.Any(t => FixedEquals(t, verifyToken.Trim())))
            return new ContentResult { Content = challenge, ContentType = "text/plain", StatusCode = 200 };

        _logger.LogWarning("{Channel} webhook verification for '{ClientCode}' refused - the verify token does not match " +
                           "(set it on the client's Edit page under Meta app, or Connectors:Meta:VerifyToken in WebApi).",
            Channels.DisplayName(channel), clientCode ?? "(no client)");
        // Plain 403 - Forbid() needs an authentication scheme and used to crash with a 500.
        return new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

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

        // Meta signs every call with the App Secret (X-Hub-Signature-256). Same rule as LINE: without a
        // secret we cannot tell Meta from a fake caller, so the call is refused (Webhooks:RequireMetaSignature).
        var settings = await _settings.GetAsync(client.Id, cancellationToken);
        var appSecret = settings.HasMetaAppSecret ? settings.MetaAppSecret : _configuration["Connectors:Meta:AppSecret"];
        if (!string.IsNullOrWhiteSpace(appSecret) && !appSecret.StartsWith("SET_", StringComparison.OrdinalIgnoreCase))
        {
            if (!MetaSignature.IsValid(appSecret, body, Request.Headers["X-Hub-Signature-256"].ToString()))
            {
                _logger.LogWarning("{Channel} webhook for {ClientCode}: invalid X-Hub-Signature-256 - rejected",
                    Channels.DisplayName(channel), client.ClientCode);
                return Unauthorized(new { error = "Invalid signature." });
            }
        }
        else if (_configuration.GetValue("Webhooks:RequireMetaSignature", true))
        {
            _logger.LogWarning("{Channel} webhook for {ClientCode} rejected: no Meta App Secret saved. " +
                               "Add it in Admin -> client -> Edit -> Meta app (or Connectors:Meta:AppSecret in WebApi).",
                Channels.DisplayName(channel), client.ClientCode);
            return Unauthorized(new { error = "Meta App Secret is not configured for this client." });
        }

        var raw = Encoding.UTF8.GetString(body);
        var parsed = channel == Channels.WhatsApp
            ? ChannelPayloadParser.ParseWhatsApp(raw, settings.WhatsAppPhoneNumberId)
            : ChannelPayloadParser.ParseMessenger(raw);
        if (parsed.Error is not null) return BadRequest(new { error = parsed.Error });

        // Say plainly why a WhatsApp message got no answer when it was meant for another number
        // (a wrong "WhatsApp phone number ID" in Admin, or Meta's dashboard sample, which uses a made-up number).
        foreach (var other in parsed.OtherPhoneNumberIds)
            _logger.LogWarning("WhatsApp message for phone number ID {Other} ignored - client {ClientCode} is set up with {Mine}. " +
                               "If this is the client's number, fix \"WhatsApp phone number ID\" in Admin. (123456123 = Meta's test sample.)",
                other, client.ClientCode, settings.WhatsAppPhoneNumberId ?? "(none)");

        var result = await _ingestor.IngestAsync(client, channel, workflowId, parsed, cancellationToken);
        if (result.Stored == 0 && parsed.StatusUpdates > 0 && parsed.Messages.Count == 0)
            _logger.LogInformation("WhatsApp for {ClientCode}: {Count} sent/delivered/read notice(s) - nothing to answer.", client.ClientCode, parsed.StatusUpdates);
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
