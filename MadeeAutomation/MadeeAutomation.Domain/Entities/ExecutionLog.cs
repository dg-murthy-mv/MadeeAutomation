using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One row per workflow execution attempt. This is the backbone that lets you
/// (a) prove ROI to clients, (b) debug failures, (c) avoid being a "launch and leave" agency.
/// </summary>
public class ExecutionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowDefinitionId { get; set; }
    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;

    public Guid ClientConfigId { get; set; }

    /// <summary>The webhook event that started this run, when it came from a webhook.</summary>
    public Guid? InboundEventId { get; set; }

    public string? Channel { get; set; }       // line / whatsapp / messenger, null for non-message runs
    public string? CustomerId { get; set; }    // LINE userId / phone number / Messenger PSID

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    public string? TriggerPayloadJson { get; set; }   // normalised inbound payload
    public string? ResultSummary { get; set; }         // short human-readable outcome
    public string? ErrorMessage { get; set; }

    /// <summary>JSON array of every action the handler asked for and what happened to it.</summary>
    public string? ActionsJson { get; set; }

    public int RetryCount { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public long? DurationMs { get; set; }
}
