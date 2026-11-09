using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;

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
    public string? RequestedDeliveryTime { get; set; }
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

    public MessageAssistant(IClaudeService claude)
    {
        _claude = claude;
    }

    public async Task<MessageAnalysis?> AnalyzeAsync(ClientConfig? client, string customerMessage, AssistantMode mode,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(client, mode);
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
            analysis.RequestedDeliveryTime = order.GetString("requestedDeliveryTime");
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

    public static string BuildSystemPrompt(ClientConfig? client, AssistantMode mode)
    {
        var businessName = string.IsNullOrWhiteSpace(client?.ClientName) ? "the business" : client!.ClientName;
        var sb = new StringBuilder();

        sb.AppendLine($"You are the customer chat assistant for {businessName}. Customers write to you on LINE, WhatsApp or Messenger.");
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
        sb.AppendLine("- For orders, use the MENU names when an item clearly matches. Never invent prices or totals.");
        sb.AppendLine($"- When intent is \"order\" and there is at least one item, the reply must confirm the items and include the exact text {OrderRefPlaceholder} where the order reference goes.");
        sb.AppendLine("- A greeting like \"hello\" is NOT an order.");
        sb.AppendLine();
        sb.AppendLine("BUSINESS INFORMATION:");
        sb.AppendLine(string.IsNullOrWhiteSpace(client?.KnowledgeBaseText) ? "(none provided)" : client!.KnowledgeBaseText.Trim());
        sb.AppendLine();
        sb.AppendLine("MENU:");
        sb.AppendLine(string.IsNullOrWhiteSpace(client?.MenuText) ? "(none provided)" : client!.MenuText.Trim());
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object only, in exactly this shape:");
        sb.AppendLine("{ \"intent\": \"order\" | \"question\" | \"greeting\" | \"other\", \"language\": string, \"reply\": string, " +
                      "\"canAnswer\": boolean, \"needsHuman\": boolean, " +
                      "\"order\": { \"items\": [ { \"name\": string, \"quantity\": number } ], \"requestedDeliveryTime\": string|null, \"customerNote\": string|null } | null }");
        return sb.ToString();
    }
}
