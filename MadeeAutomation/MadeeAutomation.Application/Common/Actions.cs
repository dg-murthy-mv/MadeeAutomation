using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Common;

/// <summary>
/// Sends an outbound customer message on the channel the handler names (normally the channel
/// the customer wrote on). Uses the CLIENT's own credentials, never a shared account.
/// Parameters: channel, to, message, replyToken (optional, LINE), rich (optional: buttons / cards as JSON).
/// </summary>
public class SendMessageAction : IAction
{
    public ActionType Type => ActionType.SendMessage;

    private readonly IMessagingConnector _messaging;

    public SendMessageAction(IMessagingConnector messaging)
    {
        _messaging = messaging;
    }

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        var channel = request.Parameters.GetValueOrDefault("channel", "");
        var to = request.Parameters.GetValueOrDefault("to", "");
        var message = request.Parameters.GetValueOrDefault("message", "");
        var replyToken = request.Parameters.GetValueOrDefault("replyToken");

        if (Channels.Normalize(channel) is null)
            return ActionResult.Fail($"Unknown or missing channel '{channel}' for SendMessage.");
        if (string.IsNullOrWhiteSpace(to) && string.IsNullOrWhiteSpace(replyToken))
            return ActionResult.Fail("Missing 'to' address for SendMessage.");
        if (string.IsNullOrWhiteSpace(message))
            return ActionResult.Fail("Message text is empty.");
        if (!Guid.TryParse(request.Parameters.GetValueOrDefault("clientConfigId", ""), out var clientConfigId))
            return ActionResult.Fail("Missing or invalid clientConfigId for SendMessage.");

        var result = await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = clientConfigId,
            Channel = Channels.Normalize(channel)!,
            To = to,
            Text = message,
            ReplyToken = string.IsNullOrWhiteSpace(replyToken) ? null : replyToken,
            Rich = ReadRich(request.Parameters.GetValueOrDefault("rich"))
        }, cancellationToken);

        return result.Success
            ? ActionResult.Ok($"Sent via {channel} ({result.Mode})")
            : ActionResult.Fail($"{channel} {result.Mode} failed: {result.Error}");
    }

    public const string RichParameter = "rich";

    public static string WriteRich(RichContent rich) => System.Text.Json.JsonSerializer.Serialize(rich);

    private static RichContent? ReadRich(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var rich = System.Text.Json.JsonSerializer.Deserialize<RichContent>(json);
            return rich is null || rich.IsEmpty ? null : rich;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;   // a broken extra never stops the text itself from going out
        }
    }
}

/// <summary>
/// Alerts a person: the client's staff LINE user / staff email, and MadeeAutomation's internal
/// WhatsApp - whichever are configured (see IStaffAlertService). Parameters: message.
/// </summary>
public class NotifyInternalAction : IAction
{
    public ActionType Type => ActionType.NotifyInternal;

    private readonly ILogger<NotifyInternalAction> _logger;
    private readonly IStaffAlertService _alerts;

    public NotifyInternalAction(ILogger<NotifyInternalAction> logger, IStaffAlertService alerts)
    {
        _logger = logger;
        _alerts = alerts;
    }

    /// <summary>Parameter "kind" = this value: a "New order" message for the client's own staff (not an alert).</summary>
    public const string OrderKind = "order";

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        var message = request.Parameters.GetValueOrDefault("message", "");
        Guid? clientConfigId = Guid.TryParse(request.Parameters.GetValueOrDefault("clientConfigId", ""), out var id) ? id : null;

        if (request.Parameters.GetValueOrDefault("kind") == OrderKind && clientConfigId is { } orderClientId)
        {
            _logger.LogInformation("New order message for staff: {Message}", message);
            var sent = await _alerts.SendNewOrderAsync(orderClientId, request.Parameters.GetValueOrDefault("subject", "New order"), message, cancellationToken);
            return sent.Success
                ? ActionResult.Ok($"Staff told about the order ({sent.Mode}).")
                : ActionResult.Fail($"Order message to staff failed: {sent.Error}");
        }

        _logger.LogInformation("Staff alert: {Message}", message);

        var result = await _alerts.SendAsync(clientConfigId, message, cancellationToken);
        return result.Success
            ? ActionResult.Ok($"Staff alerted ({result.Mode}).")
            : ActionResult.Fail($"Staff alert failed: {result.Error}");
    }
}

/// <summary>Writes handler output to a database table - used for extracted invoice data, reconciliation, etc.
/// Still a placeholder (Phase 5: typed tables per vertical such as ExtractedInvoices).</summary>
public class WriteToDatabaseAction : IAction
{
    public ActionType Type => ActionType.WriteToDatabase;

    private readonly ILogger<WriteToDatabaseAction> _logger;

    public WriteToDatabaseAction(ILogger<WriteToDatabaseAction> logger)
    {
        _logger = logger;
    }

    public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        var table = request.Parameters.GetValueOrDefault("table", "Unknown");
        _logger.LogInformation("WriteToDatabaseAction (not stored yet) -> table {Table}, payload {Payload}",
            table, request.Parameters.GetValueOrDefault("dataJson", ""));

        return Task.FromResult(ActionResult.Ok($"Logged only - {table} table not built yet"));
    }
}

/// <summary>Generic outbound HTTP call to an external system not covered by a dedicated connector.</summary>
public class CallExternalApiAction : IAction
{
    public ActionType Type => ActionType.CallExternalApi;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallExternalApiAction> _logger;

    public CallExternalApiAction(IHttpClientFactory httpClientFactory, ILogger<CallExternalApiAction> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        var url = request.Parameters.GetValueOrDefault("url", "");
        var body = request.Parameters.GetValueOrDefault("body", "");

        if (string.IsNullOrWhiteSpace(url))
            return ActionResult.Fail("Missing 'url' for CallExternalApiAction.");

        try
        {
            var client = _httpClientFactory.CreateClient("MadeeAutomationExternal");
            var response = await client.PostAsync(url,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"), cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode
                ? ActionResult.Ok(responseBody)
                : ActionResult.Fail($"HTTP {(int)response.StatusCode}: {responseBody}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "CallExternalApiAction failed for {Url}", url);
            return ActionResult.Fail(ex.Message);
        }
    }
}
