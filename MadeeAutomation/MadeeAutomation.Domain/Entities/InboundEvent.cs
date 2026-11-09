using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One inbound webhook event (one customer message), stored the moment it arrives so the
/// webhook can answer the platform immediately. A background worker then runs the workflow.
///
/// The unique index on (ClientConfigId, Channel, ExternalEventId) is what stops duplicates:
/// when LINE/Meta re-deliver the same event, the second insert is ignored.
/// </summary>
public class InboundEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientConfigId { get; set; }

    /// <summary>Set when the event arrived on a per-workflow (legacy) URL; null = route by channel.</summary>
    public Guid? WorkflowDefinitionId { get; set; }

    public string Channel { get; set; } = string.Empty;

    /// <summary>LINE webhookEventId, WhatsApp message id, Messenger message mid.</summary>
    public string ExternalEventId { get; set; } = string.Empty;

    public string? CustomerId { get; set; }
    public string? MessageText { get; set; }

    /// <summary>LINE reply token - replying with it is free; push messages count against the plan quota.</summary>
    public string? ReplyToken { get; set; }

    public InboundEventStatus Status { get; set; } = InboundEventStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public Guid? ExecutionLogId { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}
