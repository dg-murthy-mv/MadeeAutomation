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

    /// <summary>
    /// Scheduled workflows: when to run, in the client's own time zone - see WorkflowSchedule
    /// ({ "days": [1,2,3,4,5], "times": ["07:00"] } or { "cron": "0 7 * * *" }).
    /// </summary>
    public string TriggerConfigJson { get; set; } = "{}";

    // ---- Scheduled workflows: kept up to date by the scheduler in WebApi ----

    /// <summary>The next time the scheduler will run it (UTC). Null = not worked out yet, or no valid schedule.</summary>
    public DateTime? NextRunAtUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }

    /// <summary>Plain English, e.g. "Succeeded", "Failed - retry 1 of 3 at 07:02", "Missed - WebApi was not running".</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>0 normally; 1, 2, 3 while retrying a failed run.</summary>
    public int RetryAttempt { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<ExecutionLog> ExecutionLogs { get; set; } = new List<ExecutionLog>();
}
