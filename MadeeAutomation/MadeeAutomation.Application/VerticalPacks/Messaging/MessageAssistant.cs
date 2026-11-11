using System.Globalization;
using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

public enum AssistantMode
{
    Full,        // orders + questions + hand-over
    OrdersOnly,
    FaqOnly
}

public class OrderLine
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

/// <summary>What Claude decided about one customer message.</summary>
public class MessageAnalysis
{
    public string Intent { get; set; } = "other";   // order | question | greeting | other
    public string? Language { get; set; }
    public string Reply { get; set; } = string.Empty;
    public bool CanAnswer { get; set; }
    public bool NeedsHuman { get; set; }
    public List<OrderLine> Items { get; set; } = new();

    /// <summary>The customer's own words, e.g. "tomorrow 9am".</summary>
    public string? RequestedDeliveryText { get; set; }

    /// <summary>Resolved wall-clock time in the CLIENT's time zone (Kind = Unspecified), or null.</summary>
    public DateTime? RequestedDeliveryLocal { get; set; }

    public string? CustomerNote { get; set; }
}

/// <summary>
/// One Claude call per customer message: works out what the customer wants, extracts the order
/// against the client's menu, answers ONLY from the client's knowledge base, and writes the reply
/// in the customer's own language. Shared by the customer-message, order-parser and faq-responder handlers.
/// </summary>
public class MessageAssistant
{
    public const string OrderRefPlaceholder = "{ORDER_REF}";

    private readonly IClaudeService _claude;
    private readonly TimeProvider _clock;

    public MessageAssistant(IClaudeService claude, TimeProvider clock)
    {
        _claude = claude;
        _clock = clock;
    }

    public async Task<MessageAnalysis?> AnalyzeAsync(ClientConfig? client, BusinessProfile profile, string customerMessage, AssistantMode mode,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(client, mode, _clock.GetUtcNow().UtcDateTime, profile);
        var raw = await _claude.CompleteJsonAsync(systemPrompt, customerMessage, cancellationToken);

        var parsed = JsonReply.TryParseObject(raw);
        if (parsed is null)
        {
            // One retry - a second answer is almost always clean JSON.
            raw = await _claude.CompleteJsonAsync(systemPrompt, customerMessage, cancellationToken);
            parsed = JsonReply.TryParseObject(raw);
            if (parsed is null) return null;
        }

        var root = parsed.Value;
        var analysis = new MessageAnalysis
        {
            Intent = (root.GetString("intent") ?? "other").Trim().ToLowerInvariant(),
            Language = root.GetString("language"),
            Reply = (root.GetString("reply") ?? string.Empty).Trim(),
            CanAnswer = root.GetBool("canAnswer"),
            NeedsHuman = root.GetBool("needsHuman")
        };

        if (root.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object)
        {
            analysis.RequestedDeliveryText = order.GetString("requestedDeliveryText");
            analysis.RequestedDeliveryLocal = ParseLocal(order.GetString("requestedDeliveryTime"));
            analysis.CustomerNote = order.GetString("customerNote");

            if (order.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.GetString("name")?.Trim();
                    var qty = item.GetInt("quantity") ?? 0;
                    // Drop anything that can't be a real order line.
                    if (string.IsNullOrWhiteSpace(name) || qty <= 0 || qty > 1000) continue;
                    analysis.Items.Add(new OrderLine { Name = name, Quantity = qty });
                }
            }
        }

        return analysis;
    }

    /// <summary>
    /// The customer was asked "When do you want it?" and typed an answer ("3pm", "tomorrow 9", "now").
    /// Returns the time in the client's local time, or Asap, or neither when the message is not about time.
    /// One small AI call; any failure just means "not understood".
    /// </summary>
    public async Task<(DateTime? Local, bool Asap)> ResolveTimeAsync(ClientConfig? client, string customerMessage, CancellationToken cancellationToken = default)
    {
        var timeZoneId = string.IsNullOrWhiteSpace(client?.TimeZoneId) ? "UTC" : client!.TimeZoneId;
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var localNow = LocalTime.ToLocal(utcNow, timeZoneId);

        var system =
            "A shop asked its customer: \"When do you want your order?\" The customer's answer is the user message.\n" +
            $"Now is {localNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}, local time {localNow.ToString("HH:mm", CultureInfo.InvariantCulture)}. " +
            $"Local date: {localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.\n" +
            "Respond with ONE JSON object only: { \"time\": \"yyyy-MM-ddTHH:mm\" | null, \"asap\": boolean }\n" +
            "- time: the moment they want it, worked out from now (\"3pm\" -> today 15:00, \"tomorrow 9\" -> tomorrow 09:00). null when they gave no time.\n" +
            "- asap: true when they want it now / as soon as possible.\n" +
            "- If the message is not an answer about time at all (a question, a new order, anything else): time null and asap false.";

        try
        {
            var parsed = JsonReply.TryParseObject(await _claude.CompleteJsonAsync(system, customerMessage, cancellationToken));
            if (parsed is null) return (null, false);
            var local = ParseLocal(parsed.Value.GetString("time"));
            return (local, local is null && parsed.Value.GetBool("asap"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, false);
        }
    }

    private static readonly string[] LocalFormats = { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };

    /// <summary>Reads the AI's "yyyy-MM-ddTHH:mm" (client local time). Anything else -> null.</summary>
    public static DateTime? ParseLocal(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTime.TryParseExact(value.Trim(), LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            : null;

    public static string BuildSystemPrompt(ClientConfig? client, AssistantMode mode, DateTime utcNow, BusinessProfile? profile = null)
    {
        profile ??= new BusinessProfile { Menu = MenuCatalog.Parse(client?.MenuText) };
        var businessName = string.IsNullOrWhiteSpace(client?.ClientName) ? "the business" : client!.ClientName;
        var timeZoneId = string.IsNullOrWhiteSpace(client?.TimeZoneId) ? "UTC" : client!.TimeZoneId;
        var localNow = LocalTime.ToLocal(utcNow, timeZoneId);
        var currency = string.IsNullOrWhiteSpace(client?.Currency) ? null : client!.Currency.ToUpperInvariant();
        var sb = new StringBuilder();

        sb.AppendLine($"You are the customer chat assistant for {businessName}. Customers write to you on LINE, WhatsApp or Messenger.");
        sb.AppendLine();
        sb.AppendLine("BUSINESS CLOCK (use it for \"today\", \"tomorrow\", \"tonight\", \"are you open now?\"):");
        sb.AppendLine($"Today is {localNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}, local time {localNow.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
                      $"(time zone {timeZoneId}, {LocalTime.OffsetLabel(timeZoneId, utcNow)}). Local date: {localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        if (currency is not null)
            sb.AppendLine($"All prices are in {currency}.");
        if (Tax.IsConfigured(client?.TaxName, client?.TaxRatePercent))
        {
            var taxLabel = Tax.Label(client!.TaxName!, client.TaxRatePercent!.Value);
            sb.AppendLine(client.PricesIncludeTax
                ? $"MENU prices already include {taxLabel}."
                : $"MENU prices do NOT include {taxLabel}: the system adds it to the order total. If you quote a price, say that {taxLabel} is added.");
        }
        sb.AppendLine();
        sb.AppendLine(mode switch
        {
            AssistantMode.OrdersOnly =>
                "YOUR JOB: take orders only. If the message is not an order, reply politely asking the customer to send their order (items and quantities).",
            AssistantMode.FaqOnly =>
                "YOUR JOB: answer questions only. You cannot take orders - if the customer tries to order, set needsHuman to true and say a staff member will help them.",
            _ =>
                "YOUR JOB: take orders, answer questions, and hand anything else to a staff member."
        });
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Reply in the SAME language the customer wrote in (Thai -> Thai, English -> English, Tagalog -> Tagalog). Keep replies short and friendly.");
        sb.AppendLine("- Answer questions ONLY from BUSINESS INFORMATION below. Never invent opening hours, prices, delivery areas or anything else.");
        sb.AppendLine("- If a question is not covered by BUSINESS INFORMATION, set canAnswer to false and needsHuman to true, and reply that a staff member will get back to them soon.");
        sb.AppendLine("- Set needsHuman to true for complaints, refunds, or anything you cannot handle.");
        sb.AppendLine("- For orders, use the MENU names exactly when an item clearly matches. You may tell a customer the price of an item exactly as written in the MENU, but never calculate a total - the system adds the correct total.");
        if (profile.ShowsMenuButtons)
            sb.AppendLine("- If the customer asks to see the menu, what you sell, or prices in general, use intent \"menu\" and reply with ONE short friendly line only. " +
                          "Do NOT list the items: the system shows the menu as buttons right under your reply. Do the same for a greeting.");
        sb.AppendLine("- Some MENU items have a daily limit. Never take an order for more than what is left for today; tell the customer how many are left instead.");
        sb.AppendLine("- requestedDeliveryTime: when the customer gives a delivery/pick-up time, resolve it against the BUSINESS CLOCK to local \"yyyy-MM-ddTHH:mm\" (e.g. \"tomorrow 9am\" -> next day 09:00). Use null if no time was given. Put the customer's own words in requestedDeliveryText.");
        sb.AppendLine($"- When intent is \"order\" and there is at least one item, the reply must confirm the items and include the exact text {OrderRefPlaceholder} where the order reference goes.");
        sb.AppendLine("- A greeting like \"hello\" is NOT an order.");
        sb.AppendLine();
        if (profile.Hours.HoursConfigured)
        {
            sb.AppendLine("OPENING HOURS (exact - use these to answer any question about hours or whether we are open):");
            sb.AppendLine(profile.Hours.WeekText);
            if (profile.Hours.ClosedDatesText is not null)
                sb.AppendLine("Closed on: " + profile.Hours.ClosedDatesText);
            sb.AppendLine("RIGHT NOW: " + profile.Hours.StatusText);
            sb.AppendLine("If the customer orders while we are closed, still take the order but tell them when we open again.");
            sb.AppendLine();
        }

        sb.AppendLine("BUSINESS INFORMATION:");
        sb.AppendLine(string.IsNullOrWhiteSpace(client?.KnowledgeBaseText) ? "(none provided)" : client!.KnowledgeBaseText.Trim());
        sb.AppendLine();
        sb.AppendLine("MENU:");
        var available = profile.Menu.Where(m => m.IsAvailable).ToList();
        var soldOut = profile.Menu.Where(m => !m.IsAvailable).ToList();
        if (available.Count == 0 && soldOut.Count == 0)
        {
            sb.AppendLine("(none provided)");
        }
        else
        {
            foreach (var group in available.GroupBy(m => m.Category ?? ""))
            {
                if (group.Key.Length > 0) sb.AppendLine($"[{group.Key}]");
                foreach (var m in group)
                {
                    var aliases = m.Aliases is { Count: > 0 } ? $" (customers may also say: {string.Join(", ", m.Aliases)})" : "";
                    var price = m.Price is { } p ? ": " + Money.Format(p, currency) : "";
                    var limit = profile.LeftToday(m) switch
                    {
                        null => "",
                        0 => $" [daily limit {m.DailyLimit} - NONE left for today; can still be ordered for another day]",
                        var left => $" [daily limit {m.DailyLimit} - {left} left for today]"
                    };
                    sb.AppendLine($"- {m.Name}{price}{aliases}{limit}");
                }
            }
            if (soldOut.Count > 0)
                sb.AppendLine("SOLD OUT today (do NOT take orders for these - tell the customer they are not available): " +
                              string.Join(", ", soldOut.Select(m => m.Name)));
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object only, in exactly this shape:");
        var intents = profile.ShowsMenuButtons
            ? "\"order\" | \"question\" | \"greeting\" | \"menu\" | \"other\""
            : "\"order\" | \"question\" | \"greeting\" | \"other\"";
        sb.AppendLine("{ \"intent\": " + intents + ", \"language\": string, \"reply\": string, " +
                      "\"canAnswer\": boolean, \"needsHuman\": boolean, " +
                      "\"order\": { \"items\": [ { \"name\": string, \"quantity\": number } ], \"requestedDeliveryTime\": \"yyyy-MM-ddTHH:mm\"|null, \"requestedDeliveryText\": string|null, \"customerNote\": string|null } | null }");
        return sb.ToString();
    }
}
