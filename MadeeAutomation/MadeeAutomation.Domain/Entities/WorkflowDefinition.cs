using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// A configured automation for a specific client: one Trigger -> one or more Actions,
/// executed by a Handler that contains the business logic (e.g. CustomerMessageHandler).
/// </summary>
public class WorkflowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientConfigId { get; set; }
    public ClientConfig ClientConfig { get; set; } = null!;

    public string Name { get; set; } = string.Empty;           // e.g. "LINE customer messages"
    public string HandlerKey { get; set; } = string.Empty;     // resolves to an IHandler, e.g. "customer-message"
    public VerticalPack Vertical { get; set; }

    public TriggerType TriggerType { get; set; }

    /// <summary>
    /// For MessageReceived workflows: which channel this workflow answers ("line", "whatsapp",
    /// "messenger"), or null for every channel. A client can have only ONE enabled message
    /// workflow per channel - the per-client webhook URL routes each message to it.
    /// </summary>
    public string? Channel { get; set; }

    public string TriggerConfigJson { get; set; } = "{}";      // e.g. cron expression for Scheduled

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
}
