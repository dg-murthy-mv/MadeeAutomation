using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Common;

/// <summary>Sends an outbound message via WhatsApp/LINE - used by every vertical pack.</summary>
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
        var channel = request.Parameters.GetValueOrDefault("channel", "whatsapp");
        var to = request.Parameters.GetValueOrDefault("to", "");
        var message = request.Parameters.GetValueOrDefault("message", "");

        if (string.IsNullOrWhiteSpace(to))
        {
            return new ActionResult { Success = false, ErrorMessage = "Missing 'to' address for SendMessageAction." };
        }

        await _messaging.SendTextMessageAsync(channel, to, message, cancellationToken);
        return new ActionResult { Success = true, ResultData = $"Message sent to {to} via {channel}" };
    }
}

/// <summary>Notifies MadeeAutomation / client ops staff internally (e.g. via an internal Slack/email/WhatsApp group).</summary>
public class NotifyInternalAction : IAction
{
    public ActionType Type => ActionType.NotifyInternal;

    private readonly ILogger<NotifyInternalAction> _logger;
    private readonly IMessagingConnector _messaging;

    public NotifyInternalAction(ILogger<NotifyInternalAction> logger, IMessagingConnector messaging)
    {
        _logger = logger;
        _messaging = messaging;
    }

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        var message = request.Parameters.GetValueOrDefault("message", "");
        var internalChannel = request.Parameters.GetValueOrDefault("internalAddress", "internal-ops-group");

        _logger.LogInformation("Internal notification: {Message}", message);
        await _messaging.SendTextMessageAsync("whatsapp", internalChannel, message, cancellationToken);

        return new ActionResult { Success = true, ResultData = "Internal notification sent." };
    }
}

/// <summary>Writes handler output to a database table - used for extracted invoice data, reconciliation, etc.
/// This is a placeholder; wire it up to your actual EF Core DbContext in Infrastructure if you need
/// generic dynamic writes, or prefer a dedicated typed handler + repository per data shape.</summary>
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
        _logger.LogInformation("WriteToDatabaseAction -> table {Table}, payload {Payload}",
            table, request.Parameters.GetValueOrDefault("dataJson", ""));

        // TODO: replace with a real EF Core write once you decide on typed tables per vertical
        // (e.g. ExtractedInvoices, ReconciledPayments) in MadeeAutomation.Infrastructure.
        return Task.FromResult(new ActionResult { Success = true, ResultData = $"Recorded to {table}" });
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
        {
            return new ActionResult { Success = false, ErrorMessage = "Missing 'url' for CallExternalApiAction." };
        }

        try
        {
            var client = _httpClientFactory.CreateClient("MadeeAutomationExternal");
            var response = await client.PostAsync(url,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"), cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ActionResult { Success = response.IsSuccessStatusCode, ResultData = responseBody };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CallExternalApiAction failed for {Url}", url);
            return new ActionResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
