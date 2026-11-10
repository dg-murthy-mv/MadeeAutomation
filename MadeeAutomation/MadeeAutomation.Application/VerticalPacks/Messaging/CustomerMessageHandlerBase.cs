using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
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
    private readonly OrderDesk _orderDesk;
    private readonly BusinessProfileLoader _profiles;

    protected CustomerMessageHandlerBase(MessageAssistant assistant, OrderDesk orderDesk, BusinessProfileLoader profiles)
    {
        _assistant = assistant;
        _orderDesk = orderDesk;
        _profiles = profiles;
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

        var profile = await _profiles.LoadAsync(context.Client, cancellationToken);
        var analysis = await _assistant.AnalyzeAsync(context.Client, profile, text, Mode, cancellationToken);
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
            var menu = profile.Menu;

            // Prices come from the client's own menu - never from the AI.
            var matched = analysis.Items.Select(i => (ordered: i, match: MenuCatalog.Match(menu, i.Name))).ToList();

            // Safety net: sold-out items are never ordered, even if the AI tried.
            var soldOut = matched.Where(m => m.match is { IsAvailable: false }).Select(m => m.match!.Name).Distinct().ToList();
            matched = matched.Where(m => m.match is not { IsAvailable: false }).ToList();

            DateTimeOffset? deliveryLocal = analysis.RequestedDeliveryLocal is { } local
                ? LocalTime.FromLocal(local, timeZoneId)
                : null;

            // The day the order is FOR: the delivery date when the customer gave one, otherwise today.
            var orderDate = analysis.RequestedDeliveryLocal is { } requested ? DateOnly.FromDateTime(requested) : profile.LocalToday;
            var dayLabel = orderDate == profile.LocalToday ? "today" : orderDate.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

            // Safety net: daily limits. An item is left out when the customer wants more than what is left for that day.
            var overLimit = new List<string>();
            if (matched.Any(m => m.match is { DailyLimit: not null, Id: not null }))
            {
                IReadOnlyDictionary<Guid, int> ordered = orderDate == profile.LocalToday
                    ? profile.OrderedToday
                    : await _orderDesk.OrderedOnAsync(context.ClientConfigId, orderDate, cancellationToken);

                foreach (var group in matched.Where(m => m.match is { DailyLimit: not null, Id: not null }).GroupBy(m => m.match!.Id!.Value).ToList())
                {
                    var item = group.First().match!;
                    var left = BusinessProfile.Left(item, ordered) ?? int.MaxValue;
                    if (group.Sum(g => g.ordered.Quantity) <= left) continue;

                    overLimit.Add(left == 0 ? $"{item.Name} is fully booked for {dayLabel}" : $"only {left} {item.Name} left for {dayLabel}");
                    matched = matched.Where(m => m.match?.Id != group.Key).ToList();
                }
            }

            if (matched.Count == 0)
            {
                var reasons = new List<string>();
                if (soldOut.Count > 0) reasons.Add($"{string.Join(", ", soldOut)} {(soldOut.Count == 1 ? "is" : "are")} not available today");
                reasons.AddRange(overLimit);
                var why = soldOut.Count > 0 && overLimit.Count == 0 ? $"sold out ({string.Join(", ", soldOut)})"
                    : soldOut.Count == 0 ? $"daily limit ({string.Join("; ", overLimit)})"
                    : $"sold out / daily limit ({string.Join("; ", reasons)})";
                return HandlerResult.Ok($"Not ordered - {why}.", Reply($"Sorry, {string.Join("; ", reasons)}."));
            }

            var lines = matched.Select(m =>
            {
                var unitPrice = m.match?.Price;
                return new
                {
                    name = m.match?.Name ?? m.ordered.Name,
                    quantity = m.ordered.Quantity,
                    unitPrice,
                    lineTotal = unitPrice * m.ordered.Quantity,
                    onMenu = m.match is not null,
                    menuItemId = m.match?.Id
                };
            }).ToList();

            decimal? orderTotal = lines.All(l => l.lineTotal.HasValue) ? lines.Sum(l => l.lineTotal!.Value) : null;

            var orderPayload = new
            {
                source = channel,
                customerId = from,
                rawMessage = text,
                currency,
                items = lines.Select(l => new { l.name, l.quantity, l.unitPrice, l.lineTotal, l.onMenu }).ToList(),
                orderTotal,
                requestedDeliveryLocal = deliveryLocal?.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                requestedDeliveryUtc = deliveryLocal?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                requestedDeliveryText = analysis.RequestedDeliveryText,
                timeZone = timeZoneId,
                customerNote = analysis.CustomerNote
            };

            // Saved in our own Orders list for every client; also sent to the POS when one is connected.
            // An Admin test run sends and saves nothing.
            var order = new Order
            {
                ClientConfigId = context.ClientConfigId,
                Channel = channel,
                CustomerId = Cut(from, 200),
                CustomerMessage = text,
                Currency = currency is { Length: 3 } ? currency : null,
                Total = orderTotal,
                RequestedForUtc = deliveryLocal?.UtcDateTime,
                RequestedForText = Cut(analysis.RequestedDeliveryText, 200),
                OrderDate = orderDate,
                CustomerNote = Cut(analysis.CustomerNote, 1000),
                Language = Cut(analysis.Language?.Trim(), 40),
                Items = lines.Select(l => new OrderItem
                {
                    MenuItemId = l.menuItemId,
                    Name = Cut(l.name, 200)!,
                    Quantity = l.quantity,
                    UnitPrice = l.unitPrice,
                    LineTotal = l.lineTotal
                }).ToList()
            };
            await _orderDesk.PlaceAsync(order, orderPayload, context.IsTest, cancellationToken);
            var orderId = order.Reference;

            var reply = string.IsNullOrWhiteSpace(analysis.Reply)
                ? $"Thanks! We've received your order (ref #{MessageAssistant.OrderRefPlaceholder})."
                : analysis.Reply;
            reply = reply.Contains(MessageAssistant.OrderRefPlaceholder)
                ? reply.Replace(MessageAssistant.OrderRefPlaceholder, orderId)
                : $"{reply} (ref #{orderId})";
            if (soldOut.Count > 0)
                reply += $"\n(Not included - not available today: {string.Join(", ", soldOut)})";
            foreach (var note in overLimit)
                reply += $"\n(Not included - {note})";
            if (orderTotal is not null)
                reply += $"\nTotal: {Money.Format(orderTotal.Value, currency)}";

            // The client's staff are told about every order - this is how a shop with no POS gets its orders.
            var tellStaff = ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
            {
                ["kind"] = NotifyInternalAction.OrderKind,
                ["subject"] = $"[{client?.ClientName ?? "MadeeAutomation"}] New order {orderId}",
                ["message"] = OrderDesk.StaffMessage(order, client)
            }, required: false);

            var totalText = orderTotal is null ? "" : $", total {Money.Format(orderTotal.Value, currency)}";
            // Customer's answer first, then the staff message.
            return HandlerResult.Ok($"Order {orderId} created ({lines.Count} item(s){totalText}).", Reply(reply), tellStaff);
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

    private static string? Cut(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

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
    public CustomerMessageHandler(MessageAssistant assistant, OrderDesk orderDesk, BusinessProfileLoader profiles) : base(assistant, orderDesk, profiles) { }
    public override string HandlerKey => "customer-message";
    protected override AssistantMode Mode => AssistantMode.Full;
}

/// <summary>Orders only (replaces whatsapp-/line-/messenger-order-parser). HandlerKey "order-parser".</summary>
public class OrderParserHandler : CustomerMessageHandlerBase
{
    public OrderParserHandler(MessageAssistant assistant, OrderDesk orderDesk, BusinessProfileLoader profiles) : base(assistant, orderDesk, profiles) { }
    public override string HandlerKey => "order-parser";
    protected override AssistantMode Mode => AssistantMode.OrdersOnly;
}

/// <summary>FAQ only, from the client's knowledge base. HandlerKey "faq-responder".</summary>
public class FaqResponderHandler : CustomerMessageHandlerBase
{
    public FaqResponderHandler(MessageAssistant assistant, OrderDesk orderDesk, BusinessProfileLoader profiles) : base(assistant, orderDesk, profiles) { }
    public override string HandlerKey => "faq-responder";
    protected override AssistantMode Mode => AssistantMode.FaqOnly;
}
