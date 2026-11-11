using System.Globalization;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>Everything the customer-message handlers need, in one constructor argument.</summary>
public class CustomerMessageServices
{
    public CustomerMessageServices(MessageAssistant assistant, OrderDesk orderDesk, BusinessProfileLoader profiles,
        IConversationRepository conversations, TimeProvider clock)
    {
        Assistant = assistant;
        OrderDesk = orderDesk;
        Profiles = profiles;
        Conversations = conversations;
        Clock = clock;
    }

    public MessageAssistant Assistant { get; }
    public OrderDesk OrderDesk { get; }
    public BusinessProfileLoader Profiles { get; }
    public IConversationRepository Conversations { get; }
    public TimeProvider Clock { get; }
}

/// <summary>
/// Shared logic for the three customer-message handlers. Always replies on the channel the
/// message came from (using the LINE reply token when there is one), and only creates an
/// order when the message really is an order with at least one item.
///
/// Two ways a customer can order:
///   - TYPE it ("2 iced latte for 9am"): the AI reads the message (this file).
///   - TAP it: categories -> items -> cart -> "When do you want it?" -> order. Taps are answered
///     by our code without the AI, so they are instant (CustomerMessageHandlerBase.MenuChat.cs).
/// Both end in the same PlaceAsync, so sold-out checks, daily limits, totals, the saved order
/// and the staff message are identical.
/// </summary>
public abstract partial class CustomerMessageHandlerBase : IHandler
{
    public abstract string HandlerKey { get; }
    protected abstract AssistantMode Mode { get; }

    private readonly MessageAssistant _assistant;
    private readonly OrderDesk _orderDesk;
    private readonly BusinessProfileLoader _profiles;
    private readonly IConversationRepository _conversations;
    private readonly TimeProvider _clock;

    protected CustomerMessageHandlerBase(CustomerMessageServices services)
    {
        _assistant = services.Assistant;
        _orderDesk = services.OrderDesk;
        _profiles = services.Profiles;
        _conversations = services.Conversations;
        _clock = services.Clock;
    }

    /// <summary>One inbound message being handled.</summary>
    private sealed class Turn
    {
        public TriggerContext Context = null!;
        public ClientConfig? Client;
        public BusinessProfile Profile = null!;
        public string Channel = "";
        public string? From;
        public string Text = "";
        public DateTime UtcNow;
        public DateTime LocalNow;

        /// <summary>The menu-buttons chat is on for this message (structured menu, LINE, switched on).</summary>
        public bool Buttons;
        public Conversation Conversation = null!;
        public List<CartLine> Cart = new();
        public ChatTexts T = ChatTexts.English;
        public string? Currency => Client?.Currency?.ToUpperInvariant();
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var (text, from, channel) = ReadMessage(context);
        var kind = context.MessageKind ?? InboundKinds.Text;

        if (channel is null)
            return HandlerResult.Fail("This handler answers customer messages - it needs a LINE/WhatsApp/Messenger message, not a scheduled or generic trigger.");
        if (string.IsNullOrWhiteSpace(text))
            return HandlerResult.Fail("Empty inbound message - nothing to do.");
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(context.ReplyToken))
            return HandlerResult.Fail("No customer id on the message - cannot reply.");

        var client = context.Client;
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var turn = new Turn
        {
            Context = context,
            Client = client,
            Profile = await _profiles.LoadAsync(client, cancellationToken),
            Channel = channel,
            From = from,
            Text = text,
            UtcNow = utcNow,
            LocalNow = LocalTime.ToLocal(utcNow, client?.TimeZoneId)
        };

        // The menu-buttons chat: needs a structured menu and a customer to remember; LINE only for now.
        turn.Buttons = Mode != AssistantMode.FaqOnly && channel == Channels.Line && client is { MenuButtonsEnabled: true }
                       && turn.Profile.MenuIsStructured && !string.IsNullOrWhiteSpace(from);
        turn.Profile.ShowsMenuButtons = turn.Buttons;

        turn.Conversation = (turn.Buttons ? await _conversations.GetAsync(client!.Id, channel, from!, cancellationToken) : null)
                            ?? new Conversation { ClientConfigId = context.ClientConfigId, Channel = channel, CustomerId = from ?? "" };
        turn.Cart = turn.Conversation.ReadCart(utcNow);
        turn.T = ChatTexts.For(turn.Conversation.Language, client?.Country);

        // ---- A tapped button, or a new friend: answered by our code, no AI ----
        if (kind != InboundKinds.Text)
        {
            return turn.Buttons
                ? await HandleTapAsync(turn, kind, cancellationToken)
                : HandlerResult.Ok(kind == InboundKinds.Follow
                    ? "New friend - no welcome sent (menu buttons are not available for this client)."
                    : "Button tap ignored - menu buttons are not available for this client.");
        }

        // ---- We asked "When do you want it?" and the customer typed the answer ----
        if (turn.Buttons && turn.Conversation.State == Conversation.AwaitingTime && turn.Cart.Count > 0)
        {
            var (local, asap) = await _assistant.ResolveTimeAsync(client, text, cancellationToken);
            if (local is not null || asap)
                return await PlaceCartAsync(turn, local, asap ? null : text, cancellationToken);
            // Not about the time (a question, something else): carry on as a normal message.
        }

        var analysis = await _assistant.AnalyzeAsync(client, turn.Profile, text, Mode, cancellationToken);
        if (analysis is null)
            return HandlerResult.Fail("The AI reply was not valid JSON (tried twice).");

        // Remember the language they write in: the button texts follow it.
        if (turn.Buttons && !string.IsNullOrWhiteSpace(analysis.Language) &&
            !string.Equals(turn.Conversation.Language, analysis.Language.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            turn.Conversation.Language = Cut(analysis.Language.Trim(), 40);
            turn.T = ChatTexts.For(turn.Conversation.Language, client?.Country);
            await SaveConversationAsync(turn, cancellationToken);
        }

        ActionRequest NotifyStaff(string reason) => ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = $"[{client?.ClientName ?? "client"}] {Channels.DisplayName(channel)} customer {from} needs a person ({reason}): \"{text}\""
        }, required: false);

        var isOrder = analysis.Intent == "order" && analysis.Items.Count > 0;

        // ---- Orders (typed) ----
        if (isOrder && Mode != AssistantMode.FaqOnly)
        {
            // Prices come from the client's own menu - never from the AI.
            var wanted = analysis.Items.Select(i => new Wanted(MenuCatalog.Match(turn.Profile.Menu, i.Name), i.Name, i.Quantity)).ToList();

            return await PlaceAsync(turn, wanted, analysis.RequestedDeliveryLocal, analysis.RequestedDeliveryText, analysis.CustomerNote,
                analysis.Language, text,
                (orderId, _) =>
                {
                    var reply = string.IsNullOrWhiteSpace(analysis.Reply)
                        ? $"Thanks! We've received your order (ref #{MessageAssistant.OrderRefPlaceholder})."
                        : analysis.Reply;
                    return reply.Contains(MessageAssistant.OrderRefPlaceholder)
                        ? reply.Replace(MessageAssistant.OrderRefPlaceholder, orderId)
                        : $"{reply} (ref #{orderId})";
                },
                cancellationToken);
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
            return HandlerResult.Ok("Escalated to staff.", Reply(turn, reply), NotifyStaff(reason));
        }

        if (string.IsNullOrWhiteSpace(analysis.Reply))
            return HandlerResult.Fail($"The AI returned no reply text (intent: {analysis.Intent}).");

        // ---- Greeting, or "what do you sell?": the AI's one line + the menu buttons ----
        if (turn.Buttons && analysis.Intent is "greeting" or "menu")
            return HandlerResult.Ok($"Replied ({analysis.Intent}) with the menu buttons.", Reply(turn, analysis.Reply, HomeButtons(turn, 1)));

        // ---- Answered question ----
        if (analysis.Intent == "question" && analysis.CanAnswer)
            return HandlerResult.Ok("Answered from knowledge base.", Reply(turn, analysis.Reply, MenuHint(turn)));

        // ---- Orders-only workflow got something that isn't an order ----
        if (Mode == AssistantMode.OrdersOnly)
            return HandlerResult.Ok("Not an order - asked customer to send their order.", Reply(turn, analysis.Reply, MenuHint(turn)));

        // ---- Small talk / order intent with no usable items ----
        return HandlerResult.Ok($"Replied ({analysis.Intent}).", Reply(turn, analysis.Reply, MenuHint(turn)));
    }

    // ------------------------------------------------------------------------------------------
    // Placing an order - shared by typed orders and the cart
    // ------------------------------------------------------------------------------------------

    /// <summary>One thing the customer wants: the menu item it matched (null = not on the menu), the name they used, how many.</summary>
    private sealed record Wanted(MenuLine? Match, string Name, int Quantity);

    /// <summary>
    /// Checks sold-out and daily limits, works out the total from OUR prices, places the order
    /// (POS when connected, always saved with us), replies to the customer and tells the staff.
    /// replyFor(orderReference, finalLines) writes the first part of the customer's reply.
    /// </summary>
    private async Task<HandlerResult> PlaceAsync(Turn turn, List<Wanted> wanted, DateTime? deliveryLocalTime, string? deliveryText, string? customerNote,
        string? language, string customerMessage, Func<string, IReadOnlyList<(string Name, int Quantity)>, string> replyFor,
        CancellationToken cancellationToken, RichContent? buttons = null, Func<Task>? onPlaced = null)
    {
        var context = turn.Context;
        var client = turn.Client;
        var profile = turn.Profile;
        var currency = turn.Currency;
        var timeZoneId = client?.TimeZoneId;

        // Safety net: sold-out items are never ordered, even if the AI tried.
        var soldOut = wanted.Where(m => m.Match is { IsAvailable: false }).Select(m => m.Match!.Name).Distinct().ToList();
        var matched = wanted.Where(m => m.Match is not { IsAvailable: false }).ToList();

        DateTimeOffset? deliveryLocal = deliveryLocalTime is { } local ? LocalTime.FromLocal(local, timeZoneId) : null;

        // The day the order is FOR: the delivery date when the customer gave one, otherwise today.
        var orderDate = deliveryLocalTime is { } requested ? DateOnly.FromDateTime(requested) : profile.LocalToday;
        var dayLabel = orderDate == profile.LocalToday ? "today" : orderDate.ToString("ddd d MMM", CultureInfo.InvariantCulture);

        // Safety net: daily limits. An item is left out when the customer wants more than what is left for that day.
        var overLimit = new List<string>();
        if (matched.Any(m => m.Match is { DailyLimit: not null, Id: not null }))
        {
            IReadOnlyDictionary<Guid, int> ordered = orderDate == profile.LocalToday
                ? profile.OrderedToday
                : await _orderDesk.OrderedOnAsync(context.ClientConfigId, orderDate, cancellationToken);

            foreach (var group in matched.Where(m => m.Match is { DailyLimit: not null, Id: not null }).GroupBy(m => m.Match!.Id!.Value).ToList())
            {
                var item = group.First().Match!;
                var left = BusinessProfile.Left(item, ordered) ?? int.MaxValue;
                if (group.Sum(g => g.Quantity) <= left) continue;

                overLimit.Add(left == 0 ? $"{item.Name} is fully booked for {dayLabel}" : $"only {left} {item.Name} left for {dayLabel}");
                matched = matched.Where(m => m.Match?.Id != group.Key).ToList();
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
            return HandlerResult.Ok($"Not ordered - {why}.", Reply(turn, $"Sorry, {string.Join("; ", reasons)}.", buttons ?? MenuHint(turn)));
        }

        var lines = matched.Select(m =>
        {
            var unitPrice = m.Match?.Price;
            return new
            {
                name = m.Match?.Name ?? m.Name,
                quantity = m.Quantity,
                unitPrice,
                lineTotal = unitPrice * m.Quantity,
                onMenu = m.Match is not null,
                menuItemId = m.Match?.Id
            };
        }).ToList();

        // Tax (GST / VAT) is worked out by our code from the client's settings; orderTotal is what the customer pays.
        decimal? itemsTotal = lines.All(l => l.lineTotal.HasValue) ? lines.Sum(l => l.lineTotal!.Value) : null;
        var money = itemsTotal is { } sum ? Tax.Calculate(sum, client?.TaxName, client?.TaxRatePercent, client?.PricesIncludeTax ?? true) : null;
        decimal? orderTotal = money?.Total;

        var orderPayload = new
        {
            source = turn.Channel,
            customerId = turn.From,
            rawMessage = customerMessage,
            currency,
            items = lines.Select(l => new { l.name, l.quantity, l.unitPrice, l.lineTotal, l.onMenu }).ToList(),
            orderTotal,
            subtotal = money is { HasTax: true } ? money.Subtotal : (decimal?)null,
            taxName = money is { HasTax: true } ? money.TaxName : null,
            taxRatePercent = money is { HasTax: true } ? money.RatePercent : null,
            taxAmount = money is { HasTax: true } ? money.TaxAmount : (decimal?)null,
            pricesIncludeTax = money is { HasTax: true } ? money.PricesIncludeTax : (bool?)null,
            requestedDeliveryLocal = deliveryLocal?.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            requestedDeliveryUtc = deliveryLocal?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            requestedDeliveryText = deliveryText,
            timeZone = timeZoneId,
            customerNote
        };

        // Saved in our own Orders list for every client; also sent to the POS when one is connected.
        // An Admin test run sends and saves nothing.
        var order = new Order
        {
            ClientConfigId = context.ClientConfigId,
            Channel = turn.Channel,
            CustomerId = Cut(turn.From, 200),
            CustomerMessage = customerMessage,
            Currency = currency is { Length: 3 } ? currency : null,
            Total = orderTotal,
            Subtotal = money is { HasTax: true } ? money.Subtotal : null,
            TaxAmount = money is { HasTax: true } ? money.TaxAmount : null,
            TaxName = money is { HasTax: true } ? money.TaxName : null,
            TaxRatePercent = money is { HasTax: true } ? money.RatePercent : null,
            PricesIncludeTax = client?.PricesIncludeTax ?? true,
            RequestedForUtc = deliveryLocal?.UtcDateTime,
            RequestedForText = Cut(deliveryText, 200),
            OrderDate = orderDate,
            CustomerNote = Cut(customerNote, 1000),
            Language = Cut(language?.Trim(), 40),
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
        if (onPlaced is not null) await onPlaced();

        var reply = replyFor(orderId, lines.Select(l => (l.name, l.quantity)).ToList());
        if (soldOut.Count > 0)
            reply += $"\n(Not included - not available today: {string.Join(", ", soldOut)})";
        foreach (var note in overLimit)
            reply += $"\n(Not included - {note})";
        if (money is not null)
            reply += "\n" + string.Join("\n", Tax.Lines(money, currency));

        // The client's staff are told about every order - this is how a shop with no POS gets its orders.
        var tellStaff = ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["kind"] = NotifyInternalAction.OrderKind,
            ["subject"] = $"[{client?.ClientName ?? "MadeeAutomation"}] New order {orderId}",
            ["message"] = OrderDesk.StaffMessage(order, client)
        }, required: false);

        var totalText = orderTotal is null ? "" : $", total {Money.Format(orderTotal.Value, currency)}";
        // Customer's answer first, then the staff message.
        return HandlerResult.Ok($"Order {orderId} created ({lines.Count} item(s){totalText}).", Reply(turn, reply, buttons ?? MenuHint(turn)), tellStaff);
    }

    // ------------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>A reply on the customer's own channel, optionally with buttons / cards.</summary>
    private static ActionRequest Reply(Turn turn, string message, RichContent? rich = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["channel"] = turn.Channel,
            ["to"] = turn.From ?? "",
            ["message"] = message,
            ["replyToken"] = turn.Context.ReplyToken ?? ""
        };
        if (rich is { IsEmpty: false }) parameters[SendMessageAction.RichParameter] = SendMessageAction.WriteRich(rich);
        return ActionRequest.For(ActionType.SendMessage, parameters);
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
    public CustomerMessageHandler(CustomerMessageServices services) : base(services) { }
    public override string HandlerKey => "customer-message";
    protected override AssistantMode Mode => AssistantMode.Full;
}

/// <summary>Orders only (replaces whatsapp-/line-/messenger-order-parser). HandlerKey "order-parser".</summary>
public class OrderParserHandler : CustomerMessageHandlerBase
{
    public OrderParserHandler(CustomerMessageServices services) : base(services) { }
    public override string HandlerKey => "order-parser";
    protected override AssistantMode Mode => AssistantMode.OrdersOnly;
}

/// <summary>FAQ only, from the client's knowledge base. HandlerKey "faq-responder".</summary>
public class FaqResponderHandler : CustomerMessageHandlerBase
{
    public FaqResponderHandler(CustomerMessageServices services) : base(services) { }
    public override string HandlerKey => "faq-responder";
    protected override AssistantMode Mode => AssistantMode.FaqOnly;
}
