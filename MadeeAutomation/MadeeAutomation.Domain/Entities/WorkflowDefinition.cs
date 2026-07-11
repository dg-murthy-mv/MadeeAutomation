using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// A configured automation for a specific client: one Trigger -> one or more Actions,
/// executed by a Handler that contains the business logic (e.g. WhatsAppOrderParserHandler).
/// This is the "vertical pack instance" once it's wired up for a real client.
/// </summary>
public class WorkflowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientConfigId { get; set; }
    public ClientConfig ClientConfig { get; set; } = null!;

    public string Name { get; set; } = string.Empty;           // e.g. "WhatsApp Order Intake"
    public string HandlerKey { get; set; } = string.Empty;     // resolves to an IHandler via DI, e.g. "whatsapp-order-parser"
    public VerticalPack Vertical { get; set; }

    public TriggerType TriggerType { get; set; }
    public string TriggerConfigJson { get; set; } = "{}";      // e.g. cron expression, webhook path, message channel

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
}
