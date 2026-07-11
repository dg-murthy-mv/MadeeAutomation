using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.FnB;

/// <summary>
/// F&amp;B / Retail vertical pack.
/// Trigger: inbound WhatsApp message (order text, e.g. "2 sourdough loaves and 1 dozen croissants, deliver tomorrow 9am").
/// Handler: asks Claude to extract structured order data, creates the order in POSFirst,
/// then queues a confirmation message back to the customer.
///
/// HandlerKey "whatsapp-order-parser" is what you set on WorkflowDefinition.HandlerKey
/// when configuring this automation for a specific client in the Admin dashboard.
/// </summary>
public class WhatsAppOrderParserHandler : IHandler
{
    public string HandlerKey => "whatsapp-order-parser";

    private readonly IClaudeService _claude;
    private readonly IPosFirstConnector _posFirst;

    public WhatsAppOrderParserHandler(IClaudeService claude, IPosFirstConnector posFirst)
    {
        _claude = claude;
        _posFirst = posFirst;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(context.PayloadJson);
        var root = payload.RootElement;
        var messageText = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var fromNumber = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(messageText))
        {
            return HandlerResult.Fail("Empty inbound message - nothing to parse.");
        }

        const string systemPrompt =
            "You extract structured bakery/retail orders from customer WhatsApp messages. " +
            "Reply with strict JSON only, no prose, in this shape: " +
            "{ \"items\": [ { \"name\": string, \"quantity\": number } ], \"requestedDeliveryTime\": string|null, \"customerNote\": string|null }";

        var extractedJson = await _claude.CompleteJsonAsync(systemPrompt, messageText, cancellationToken);

        var orderPayload = new
        {
            source = "whatsapp",
            customerPhone = fromNumber,
            rawMessage = messageText,
            parsedOrder = extractedJson
        };

        var orderId = await _posFirst.CreateOrderAsync(context.ClientConfigId, orderPayload, cancellationToken);

        var confirmationAction = ActionRequest.For(ActionType.SendMessage, new Dictionary<string, string>
        {
            ["channel"] = "whatsapp",
            ["to"] = fromNumber,
            ["message"] = $"Thanks! We've received your order (ref #{orderId}). We'll confirm the total and delivery time shortly."
        });

        return HandlerResult.Ok($"Order {orderId} created from WhatsApp message.", confirmationAction);
    }
}
