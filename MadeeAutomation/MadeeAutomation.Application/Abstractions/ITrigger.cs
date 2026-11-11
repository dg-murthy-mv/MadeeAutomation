using MadeeAutomation.Domain.Entities;
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
    string Key { get; }
}

/// <summary>
/// Everything a handler needs to know about the event that started it.
/// For customer messages the typed properties (Channel, CustomerId, MessageText, ReplyToken)
/// are filled in, so handlers never have to dig through channel-specific JSON - and they
/// always reply on the SAME channel the message came from.
/// </summary>
public class TriggerContext
{
    public Guid ClientConfigId { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public TriggerType TriggerType { get; set; }

    /// <summary>Normalised inbound data - webhook JSON body, message details, etc.</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    // ---- Customer-message details (null for scheduled / generic webhook runs) ----
    public Guid? InboundEventId { get; set; }
    public string? Channel { get; set; }
    public string? CustomerId { get; set; }

    /// <summary>Customer's display name when the channel sent it with the message (WhatsApp).</summary>
    public string? CustomerName { get; set; }
    public string? MessageText { get; set; }
    public string? ReplyToken { get; set; }

    /// <summary>"text" (typed), "tap" (a button - MessageText holds its data) or "follow" (added as friend). Null = text.</summary>
    public string? MessageKind { get; set; }

    /// <summary>The client this run belongs to. Filled in by the WorkflowEngine before the handler runs.</summary>
    public ClientConfig? Client { get; set; }

    /// <summary>
    /// Admin "Test" run: the AI really runs, but nothing is sent, no POSFirst order is created and
    /// no staff alert goes out. The engine records what WOULD have happened.
    /// </summary>
    public bool IsTest { get; set; }
}
