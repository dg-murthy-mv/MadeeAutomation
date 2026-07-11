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

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    public string? TriggerPayloadJson { get; set; }   // raw inbound payload (webhook body, message text, etc.)
    public string? ResultSummary { get; set; }         // short human-readable outcome
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public long? DurationMs { get; set; }
}
