using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Domain.Billing;

/// <summary>
/// The plan a client is on. Demo = MadeeVision's own demo and test clients: no limits, never ends.
/// Trial = 15 days with Business features. Starter / Business / Premium are the paid plans.
/// </summary>
public enum PlanTier
{
    Demo = 0,
    Trial = 1,
    Starter = 2,
    Business = 3,
    Premium = 4
}

public enum BillingCycle
{
    Monthly = 1,
    Yearly = 2
}

/// <summary>What a plan includes. Null limit = unlimited.</summary>
public sealed record PlanDefinition(
    PlanTier Tier,
    string Name,
    int? MaxChannels,
    int? MaxStaff,
    int? MonthlyChats,
    bool SecondReminder,
    bool Waitlist,
    bool Feedback,
    bool PrioritySupport,
    string Summary)
{
    public bool IsPaid => Tier is PlanTier.Starter or PlanTier.Business or PlanTier.Premium;

    public static string Limit(int? value) => value is { } v ? v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "Unlimited";
}

/// <summary>
/// The plans, their limits and list prices. Chats are warn-only (emails at 80% and 100%); staff and
/// channels are hard limits in Admin; the extra features (2-hour reminder, waitlist, ratings) only run
/// on plans that include them.
/// </summary>
public static class PlanCatalog
{
    public const int TrialDays = 15;
    public const int GraceDays = 3;

    public static readonly PlanDefinition Demo = new(PlanTier.Demo, "Demo", null, null, null, true, true, true, true,
        "MadeeVision demo or internal client - no limits, never ends.");

    public static readonly PlanDefinition Trial = new(PlanTier.Trial, "Free trial", 2, 10, 300, true, true, true, false,
        $"{TrialDays} days with Business features (Messenger + WhatsApp, up to 10 staff, 300 chats).");

    public static readonly PlanDefinition Starter = new(PlanTier.Starter, "Starter", 1, 3, 300, false, false, false, false,
        "One channel, up to 3 staff, 300 customer chats a month.");

    public static readonly PlanDefinition Business = new(PlanTier.Business, "Business", 2, 10, 1000, true, true, true, false,
        "Messenger + WhatsApp, up to 10 staff, 1,000 chats a month, 2-hour reminder, waitlist, ratings.");

    public static readonly PlanDefinition Premium = new(PlanTier.Premium, "Premium", null, null, 3000, true, true, true, true,
        "All channels, unlimited staff, 3,000 chats a month, priority support.");

    public static IReadOnlyList<PlanDefinition> All { get; } = new[] { Demo, Trial, Starter, Business, Premium };

    /// <summary>The plans a client can pay for, cheapest first.</summary>
    public static IReadOnlyList<PlanDefinition> Paid { get; } = new[] { Starter, Business, Premium };

    public static PlanDefinition For(PlanTier tier) => All.FirstOrDefault(p => p.Tier == tier) ?? Demo;

    // List prices per month, by currency. Yearly = 10 months (2 months free). Markets without a list
    // price yet get the price agreed with the client (ClientConfig.PlanPrice).
    private static readonly Dictionary<string, (decimal Starter, decimal Business, decimal Premium)> MonthlyPrices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PHP"] = (1490m, 2990m, 4990m)
        };

    public const int YearlyMonthsCharged = 10;

    public static decimal? ListPrice(PlanTier tier, string? currency, BillingCycle cycle)
    {
        if (currency is null || !MonthlyPrices.TryGetValue(currency, out var p)) return null;
        decimal? monthly = tier switch
        {
            PlanTier.Starter => p.Starter,
            PlanTier.Business => p.Business,
            PlanTier.Premium => p.Premium,
            _ => null
        };
        return monthly is { } m && cycle == BillingCycle.Yearly ? m * YearlyMonthsCharged : monthly;
    }

    /// <summary>The price this client pays per billing cycle: the agreed price, else the list price.</summary>
    public static decimal? PriceFor(ClientConfig client) =>
        client.PlanPrice ?? ListPrice(client.Plan, client.Currency, client.BillingCycle ?? BillingCycle.Monthly);

    /// <summary>The same price as a monthly figure (yearly / 12) - for adding up monthly revenue.</summary>
    public static decimal? MonthlyValue(ClientConfig client) =>
        PriceFor(client) is { } price
            ? client.BillingCycle == BillingCycle.Yearly ? Math.Round(price / 12m, 2) : price
            : null;

    public static DateTime TrialEnd(DateTime startUtc) => startUtc.AddDays(TrialDays);

    /// <summary>A payment for one cycle extends from the current end (if still ahead) or from now.</summary>
    public static DateTime ExtendEnd(DateTime? currentEndUtc, DateTime utcNow, BillingCycle cycle, int cycles = 1)
    {
        var from = currentEndUtc is { } end && end > utcNow ? end : utcNow;
        return cycle == BillingCycle.Yearly ? from.AddYears(cycles) : from.AddMonths(cycles);
    }
}

public enum SubscriptionState
{
    /// <summary>Demo plan: no limits, never ends.</summary>
    Unlimited,
    Trial,
    Active,
    /// <summary>The period ended; everything still works for GraceDays while the owner renews.</summary>
    Grace,
    /// <summary>Grace is over: the assistant only sends a short "we'll reply soon" message.</summary>
    Paused
}

/// <summary>Where a client's subscription stands at a moment.</summary>
public sealed record SubscriptionStatus(PlanDefinition Plan, SubscriptionState State, DateTime? EndsAtUtc, int? DaysLeft, DateTime? PausesAtUtc)
{
    public bool IsPaused => State == SubscriptionState.Paused;
    public bool NeedsRenewalSoon => State is SubscriptionState.Grace || DaysLeft is <= 7;

    public static SubscriptionStatus Of(ClientConfig client, DateTime utcNow)
    {
        var plan = PlanCatalog.For(client.Plan);
        if (client.Plan == PlanTier.Demo) return new(plan, SubscriptionState.Unlimited, null, null, null);

        var running = client.Plan == PlanTier.Trial ? SubscriptionState.Trial : SubscriptionState.Active;
        if (client.PlanEndsAtUtc is not { } end) return new(plan, running, null, null, null);

        var pausesAt = end.AddDays(PlanCatalog.GraceDays);
        if (utcNow < end)
            return new(plan, running, end, (int)Math.Ceiling((end - utcNow).TotalDays), pausesAt);
        if (utcNow < pausesAt)
            return new(plan, SubscriptionState.Grace, end, 0, pausesAt);
        return new(plan, SubscriptionState.Paused, end, 0, pausesAt);
    }

    /// <summary>Plain words for badges and banners.</summary>
    public string Label => State switch
    {
        SubscriptionState.Unlimited => "Demo (no limits)",
        SubscriptionState.Trial => DaysLeft is { } d ? $"Free trial - {d} day{(d == 1 ? "" : "s")} left" : "Free trial",
        SubscriptionState.Active => DaysLeft is { } d ? $"{Plan.Name} - {d} day{(d == 1 ? "" : "s")} left" : Plan.Name,
        SubscriptionState.Grace => $"{Plan.Name} - ended, renew to keep it running",
        _ => $"{Plan.Name} - paused (not renewed)"
    };
}

/// <summary>Hard limits checked in Admin before saving.</summary>
public static class PlanRules
{
    public static string? StaffLimitError(ClientConfig client, int activeStaffAfterSave)
    {
        var plan = PlanCatalog.For(client.Plan);
        return plan.MaxStaff is { } max && activeStaffAfterSave > max
            ? $"The {plan.Name} plan allows up to {max} working staff. Upgrade the plan, or mark someone as not working first."
            : null;
    }

    public static string? ChannelLimitError(ClientConfig client, int channelsAfterSave)
    {
        var plan = PlanCatalog.For(client.Plan);
        return plan.MaxChannels is { } max && channelsAfterSave > max
            ? $"The {plan.Name} plan allows {max} messaging channel{(max == 1 ? "" : "s")} (LINE, WhatsApp or Messenger). Upgrade the plan, or disable another channel's workflow first."
            : null;
    }
}
