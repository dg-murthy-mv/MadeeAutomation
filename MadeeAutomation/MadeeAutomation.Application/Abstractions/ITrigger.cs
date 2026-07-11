using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>
/// Represents an inbound event that can start a workflow: a webhook call, a scheduled tick,
/// an inbound WhatsApp/LINE message, or a manual trigger from the Admin dashboard.
/// Triggers are thin - they just describe "something happened" and carry the raw payload.
/// The actual business logic always lives in an IHandler, never in the trigger itself.
/// </summary>
public interface ITrigger
{
    TriggerType Type { get; }

    /// <summary>
    /// Unique key used to route a workflow's TriggerConfigJson to this trigger implementation,
    /// e.g. "webhook", "scheduled", "whatsapp-message".
    /// </summary>
    string Key { get; }
}

/// <summary>
/// The payload captured when a trigger fires, before it's handed to a handler.
/// </summary>
public class TriggerContext
{
    public Guid ClientConfigId { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public TriggerType TriggerType { get; set; }

    /// <summary>Raw inbound data - webhook JSON body, message text, file path, etc.</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
