using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>
/// Shared logic for the three customer-message handlers. Always replies on the channel the
/// message came from (using the LINE reply token when there is one), and only creates a
/// POSFirst order when the message really is an order with at least one item.
/// </summary>
public abstract class CustomerMessageHandlerBase : IHandler
{
    public abstract string HandlerKey { get; }
    protected abstract AssistantMode Mode { get; }

    private readonly MessageAssistant _assistant;
    private readonly IPosFirstConnector _posFirst;

    protected CustomerMessageHandlerBase(MessageAssistant assistant, IPosFirstConnector posFirst)
    {
        _assistant = assistant;
        _posFirst = posFirst;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var (text, from, channel) = ReadMessage(context);

        if (channel is null)
            return HandlerResult.Fail("This handler answers customer messages - it needs a LINE/WhatsApp/Messenger message, not a scheduled or generic trigger.");
        if (string.IsNullOrWhiteSpace(text))
            return HandlerResult.Fail("Empty inbound message - nothing to do.");
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(context.ReplyToken))
            return HandlerResult.Fail("No customer id on the message - cannot reply.");

        var analysis = await _assistant.AnalyzeAsync(context.Client, text, Mode, cancellationToken);
        if (analysis is null)
            return HandlerResult.Fail("The AI reply was not valid JSON (tried twice).");

        ActionRequest Reply(string message) => ActionRequest.For(ActionType.SendMessage, new Dictionary<string, string>
        {
            ["channel"] = channel,
            ["to"] = from ?? "",
            ["message"] = message,
            ["replyToken"] = context.ReplyToken ?? ""
        });

        ActionRequest NotifyStaff(string reason) => ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = $"[{context.Client?.ClientName ?? "client"}] {Channels.DisplayName(channel)} customer {from} needs a person ({reason}): \"{text}\""
        }, required: false);

        var isOrder = analysis.Intent == "order" && analysis.Items.Count > 0;

        // ---- Orders ----
        if (isOrder && Mode != AssistantMode.FaqOnly)
        {
            var client = context.Client;
            var currency = client?.Currency?.ToUpperInvariant();
            var timeZoneId = client?.TimeZoneId;
            var menu = MenuCatalog.Parse(client?.MenuText);

            // Prices come from the client's own menu - never from the AI.
            var lines = analysis.Items.Select(i =>
            {
                var match = MenuCatalog.Match(menu, i.Name);
                var unitPrice = match?.Price;
                return new
                {
                    name = match?.Name ?? i.Name,
                    quantity = i.Quantity,
                    unitPrice,
                    lineTotal = unitPrice * i.Quantity,
                    onMenu = match is not null
                };
            }).ToList();

            decimal? orderTotal = lines.All(l => l.lineTotal.HasValue) ? lines.Sum(l => l.lineTotal!.Value) : null;

            DateTimeOffset? deliveryLocal = analysis.RequestedDeliveryLocal is { } local
                ? LocalTime.FromLocal(local, timeZoneId)
                : null;

            var orderPayload = new
            {
                source = channel,
                customerId = from,
                rawMessage = text,
                currency,
                items = lines,
                orderTotal,
                requestedDeliveryLocal = deliveryLocal?.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                requestedDeliveryUtc = deliveryLocal?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                requestedDeliveryText = analysis.RequestedDeliveryText,
                timeZone = timeZoneId,
                customerNote = analysis.CustomerNote
            };

            // Admin test runs never create a real order.
            var orderId = context.IsTest
                ? "TEST-ORDER"
                : await _posFirst.CreateOrderAsync(context.ClientConfigId, orderPayload, cancellationToken);

            var reply = string.IsNullOrWhiteSpace(analysis.Reply)
                ? $"Thanks! We've received your order (ref #{MessageAssistant.OrderRefPlaceholder})."
                : analysis.Reply;
            reply = reply.Contains(MessageAssistant.OrderRefPlaceholder)
                ? reply.Replace(MessageAssistant.OrderRefPlaceholder, orderId)
                : $"{reply} (ref #{orderId})";
            if (orderTotal is not null)
                reply += $"\nTotal: {Money.Format(orderTotal.Value, currency)}";

            var totalText = orderTotal is null ? "" : $", total {Money.Format(orderTotal.Value, currency)}";
            return HandlerResult.Ok($"Order {orderId} created ({analysis.Items.Count} item(s){totalText}).", Reply(reply));
        }

        // ---- Hand-over to staff ----
        var cannotAnswerQuestion = analysis.Intent == "question" && !analysis.CanAnswer;
        if (analysis.NeedsHuman || (cannotAnswerQuestion && Mode != AssistantMode.OrdersOnly))
        {
            var reply = string.IsNullOrWhiteSpace(analysis.Reply)
                ? "Thanks for your message! A member of our team will get back to you shortly."
                : analysis.Reply;
            var reason = cannotAnswerQuestion ? "question not covered by the knowledge base" : "needs a person";
            // Customer's answer first, then the staff alert.
            return HandlerResult.Ok("Escalated to staff.", Reply(reply), NotifyStaff(reason));
        }

        if (string.IsNullOrWhiteSpace(analysis.Reply))
            return HandlerResult.Fail($"The AI returned no reply text (intent: {analysis.Intent}).");

        // ---- Answered question ----
        if (analysis.Intent == "question" && analysis.CanAnswer)
            return HandlerResult.Ok("Answered from knowledge base.", Reply(analysis.Reply));

        // ---- Orders-only workflow got something that isn't an order ----
        if (Mode == AssistantMode.OrdersOnly)
            return HandlerResult.Ok("Not an order - asked customer to send their order.", Reply(analysis.Reply));

        // ---- Greeting / small talk / order intent with no usable items ----
        return HandlerResult.Ok($"Replied ({analysis.Intent}).", Reply(analysis.Reply));
    }

    /// <summary>Typed context first; falls back to the { message, from, channel } payload.</summary>
    private static (string? text, string? from, string? channel) ReadMessage(TriggerContext context)
    {
        string? text = context.MessageText, from = context.CustomerId, channel = Channels.Normalize(context.Channel);

        if (text is null || from is null || channel is null)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(context.PayloadJson) ? "{}" : context.PayloadJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    text ??= root.GetString("message");
                    from ??= root.GetString("from");
                    channel ??= Channels.Normalize(root.GetString("channel"));
                }
            }
            catch (JsonException) { /* not JSON - leave as is */ }
        }

        return (text, from, channel);
    }
}

/// <summary>Recommended handler for LINE / WhatsApp / Messenger: orders + FAQ + hand-over. HandlerKey "customer-message".</summary>
public class CustomerMessageHandler : CustomerMessageHandlerBase
{
    public CustomerMessageHandler(MessageAssistant assistant, IPosFirstConnector posFirst) : base(assistant, posFirst) { }
    public override string HandlerKey => "customer-message";
    protected override AssistantMode Mode => AssistantMode.Full;
}

/// <summary>Orders only (replaces whatsapp-/line-/messenger-order-parser). HandlerKey "order-parser".</summary>
public class OrderParserHandler : CustomerMessageHandlerBase
{
    public OrderParserHandler(MessageAssistant assistant, IPosFirstConnector posFirst) : base(assistant, posFirst) { }
    public override string HandlerKey => "order-parser";
    protected override AssistantMode Mode => AssistantMode.OrdersOnly;
}

/// <summary>FAQ only, from the client's knowledge base. HandlerKey "faq-responder".</summary>
public class FaqResponderHandler : CustomerMessageHandlerBase
{
    public FaqResponderHandler(MessageAssistant assistant, IPosFirstConnector posFirst) : base(assistant, posFirst) { }
    public override string HandlerKey => "faq-responder";
    protected override AssistantMode Mode => AssistantMode.FaqOnly;
}
