using MadeeAutomation.Domain.Billing;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// A payment a client made for their plan (bank transfer, GCash...), recorded by MadeeVision in Admin.
/// Recording it extends the client's PlanEndsAtUtc by the months it covers.
/// </summary>
public class SubscriptionPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    /// <summary>The day the money arrived (the client's local date).</summary>
    public DateOnly PaidOn { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>"Bank transfer", "GCash", "Maya", "Cash", "Other".</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Bank or GCash reference number.</summary>
    public string? Reference { get; set; }

    public PlanTier Plan { get; set; }
    public BillingCycle Cycle { get; set; }

    /// <summary>How many months or years (per Cycle) this payment covers.</summary>
    public int Cycles { get; set; } = 1;

    public DateTime PeriodFromUtc { get; set; }
    public DateTime PeriodToUtc { get; set; }

    public string? Note { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A plan email or message that was sent once ("ends7:20261205", "chats80:2026-11", "paused-reply:U123:2026-12-08").
/// The unique (client, key) pair makes sure two WebApi copies never send the same notice twice.
/// </summary>
public class SubscriptionNotice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }
    public string NoticeKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
