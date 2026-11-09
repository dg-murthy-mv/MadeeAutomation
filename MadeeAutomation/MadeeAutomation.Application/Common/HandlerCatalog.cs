using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Common;

/// <summary>Describes one handler for the Admin "New workflow" form.</summary>
public record HandlerInfo(
    string Key,
    string Name,
    VerticalPack Vertical,
    TriggerType DefaultTrigger,
    string Description,
    bool IsMessageHandler);

/// <summary>
/// The list of handlers the Admin dashboard offers. Kept as plain data (not resolved from DI)
/// so Admin.Web never has to construct handlers or hold Claude/connector secrets.
/// When you add a new IHandler, add one line here too.
/// </summary>
public static class HandlerCatalog
{
    public static readonly IReadOnlyList<HandlerInfo> All = new List<HandlerInfo>
    {
        new("customer-message", "Customer messages (orders + FAQ + hand-over)", VerticalPack.FnBRetail, TriggerType.MessageReceived,
            "Recommended for LINE / WhatsApp / Messenger. Takes orders from the menu, answers questions from the knowledge base, hands anything else to staff. Replies in the customer's language.", true),
        new("order-parser", "Orders only", VerticalPack.FnBRetail, TriggerType.MessageReceived,
            "Creates a POSFirst order when the message is an order; otherwise asks the customer to send their order.", true),
        new("faq-responder", "FAQ only", VerticalPack.CustomerSupport, TriggerType.MessageReceived,
            "Answers from the knowledge base; hands anything it cannot answer to staff.", true),
        new("low-stock-alert", "Low-stock alert", VerticalPack.FnBRetail, TriggerType.Scheduled,
            "Checks POSFirst for low-stock items and notifies staff.", false),
        new("lead-scoring", "Lead scoring", VerticalPack.CrmSales, TriggerType.Webhook,
            "Scores a web-form lead with AI and saves it to Merkai CRM.", false),
        new("invoice-extraction", "Invoice extraction", VerticalPack.FinanceChit, TriggerType.Webhook,
            "Extracts invoice fields from text (file upload not built yet).", false),
    };

    public static HandlerInfo? Find(string? key) =>
        All.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
}
