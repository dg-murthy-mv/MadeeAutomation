using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.VerticalPacks.FnB;

/// <summary>
/// F&amp;B / Retail vertical pack - LINE variant.
/// Identical logic to WhatsAppOrderParserHandler, just targets the LINE channel for the
/// outbound confirmation. Kept as a separate handler (rather than a shared generic one)
/// so each channel's workflow can be configured/enabled independently per client.
/// HandlerKey: "line-order-parser"
/// </summary>
public class LineOrderParserHandler : IHandler
{
    public string HandlerKey => "line-order-parser";

    private readonly IClaudeService _claude;
    private readonly IPosFirstConnector _posFirst;

    public LineOrderParserHandler(IClaudeService claude, IPosFirstConnector posFirst)
    {
        _claude = claude;
        _posFirst = posFirst;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(context.PayloadJson);
        var root = payload.RootElement;
        var messageText = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var fromUserId = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(messageText))
        {
            return HandlerResult.Fail("Empty inbound LINE message - nothing to parse.");
        }

        const string systemPrompt =
            "You extract structured bakery/retail orders from customer LINE messages. " +
            "Reply with strict JSON only, no prose, in this shape: " +
            "{ \"items\": [ { \"name\": string, \"quantity\": number } ], \"requestedDeliveryTime\": string|null, \"customerNote\": string|null }";

        var extractedJson = await _claude.CompleteJsonAsync(systemPrompt, messageText, cancellationToken);

        var orderPayload = new
        {
            source = "line",
            customerLineId = fromUserId,
            rawMessage = messageText,
            parsedOrder = extractedJson
        };

        var orderId = await _posFirst.CreateOrderAsync(context.ClientConfigId, orderPayload, cancellationToken);

        var confirmationAction = ActionRequest.For(ActionType.SendMessage, new Dictionary<string, string>
        {
            ["channel"] = "line",
            ["to"] = fromUserId,
            ["message"] = $"Thanks! We've received your order (ref #{orderId}). We'll confirm the total and delivery time shortly."
        });

        return HandlerResult.Ok($"Order {orderId} created from LINE message.", confirmationAction);
    }
}