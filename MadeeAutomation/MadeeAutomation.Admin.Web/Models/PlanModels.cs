using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Application.Billing;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Admin.Web.Models;

/// <summary>One client on the Billing page.</summary>
public sealed record BillingRow(ClientConfig Client, PlanUsage Usage, decimal? MonthlyValue)
{
    public bool NeedsAttention =>
        Usage.Status.IsPaused || Usage.Status.NeedsRenewalSoon || Usage.ChatPercent is >= 80;
}

public class BillingPageModel
{
    public string Show { get; set; } = "all";
    public List<BillingRow> Rows { get; set; } = new();
    public int TotalClients { get; set; }

    /// <summary>Monthly revenue from paid plans, per currency (yearly plans count as 1/12).</summary>
    public Dictionary<string, decimal> MonthlyRevenue { get; set; } = new();

    /// <summary>Payments recorded this calendar month, per currency.</summary>
    public Dictionary<string, decimal> PaidThisMonth { get; set; } = new();

    public int Trials { get; set; }
    public int Paying { get; set; }
    public int Attention { get; set; }
    public int Paused { get; set; }
}

/// <summary>Admin -> Billing -> one client: plan, usage, change plan, record a payment, payment history.</summary>
public class ClientPlanPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public PlanUsage Usage { get; set; } = null!;
    public List<SubscriptionPayment> Payments { get; set; } = new();
    public PlanChangeForm Change { get; set; } = new();
    public PaymentForm Payment { get; set; } = new();
}

public class PlanChangeForm
{
    public Guid ClientConfigId { get; set; }

    [Display(Name = "Plan")]
    public PlanTier Plan { get; set; } = PlanTier.Trial;

    [Display(Name = "Billing")]
    public BillingCycle Cycle { get; set; } = BillingCycle.Monthly;

    /// <summary>The last day of the trial or paid period (client's local date). Empty = no end (Demo).</summary>
    [Display(Name = "Ends on (last day)")]
    public DateOnly? EndsOn { get; set; }

    [Display(Name = "Agreed price per billing period (empty = list price)")]
    [Range(0, 100_000_000)]
    public decimal? Price { get; set; }

    [Display(Name = "Note (only MadeeVision sees it)")]
    [StringLength(500)]
    public string? Note { get; set; }
}

public class PaymentForm
{
    public static readonly string[] Methods = { "Bank transfer", "GCash", "Maya", "Cash", "Other" };

    public Guid ClientConfigId { get; set; }

    [Display(Name = "Plan paid for")]
    public PlanTier Plan { get; set; } = PlanTier.Business;

    [Display(Name = "Billing")]
    public BillingCycle Cycle { get; set; } = BillingCycle.Monthly;

    /// <summary>How many months (or years, for yearly billing) this payment covers.</summary>
    [Display(Name = "Periods covered")]
    [Range(1, 24, ErrorMessage = "Between 1 and 24.")]
    public int Cycles { get; set; } = 1;

    [Display(Name = "Amount received")]
    [Range(0.01, 100_000_000, ErrorMessage = "Enter the amount received.")]
    public decimal Amount { get; set; }

    [Display(Name = "Paid on")]
    public DateOnly PaidOn { get; set; }

    [Display(Name = "Method")]
    [Required]
    public string Method { get; set; } = "GCash";

    [Display(Name = "Reference no.")]
    [StringLength(100)]
    public string? Reference { get; set; }

    [Display(Name = "Note")]
    [StringLength(500)]
    public string? Note { get; set; }
}

/// <summary>The client's own "Plan" page in the portal (MadeeVision sees the same page).</summary>
public class MyPlanPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public PlanUsage Usage { get; set; } = null!;
    public decimal? Price { get; set; }
}
