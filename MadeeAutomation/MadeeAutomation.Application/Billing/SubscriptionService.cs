using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Billing;

/// <summary>How much of its plan a client is using this month.</summary>
public sealed record PlanUsage(
    ClientConfig Client,
    SubscriptionStatus Status,
    string MonthLabel,
    int Chats,
    int? ChatLimit,
    int Staff,
    int? StaffLimit,
    int Channels,
    int? ChannelLimit)
{
    public int? ChatPercent => ChatLimit is > 0 ? (int)Math.Round(Chats * 100m / ChatLimit.Value) : null;
    public bool OverChats => ChatLimit is { } limit && Chats > limit;
}

/// <summary>Reads plan usage: chats this month (client time), working staff, messaging channels.</summary>
public class SubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IBookingRepository _bookings;
    private readonly TimeProvider _clock;

    public SubscriptionService(ISubscriptionRepository subscriptions, IBookingRepository bookings, TimeProvider clock)
    {
        _subscriptions = subscriptions;
        _bookings = bookings;
        _clock = clock;
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Messaging channels in use: enabled "message received" workflows, one per channel.
    /// exceptWorkflowId / withChannel let Admin check a change before it is saved.
    /// </summary>
    public static int ChannelsInUse(ClientConfig client, Guid? exceptWorkflowId = null, string? withChannel = null)
    {
        var channels = client.Workflows
            .Where(w => w.Id != exceptWorkflowId && w.IsEnabled && w.TriggerType == TriggerType.MessageReceived)
            .Select(w => Channels.Normalize(w.Channel))
            .Where(c => c is not null)
            .ToHashSet();
        if (Channels.Normalize(withChannel) is { } extra) channels.Add(extra);
        return channels.Count;
    }

    /// <summary>The client's current month in its own time zone: first moment (UTC), label "November 2026", key "2026-11".</summary>
    public static (DateTime FromUtc, string Label, string Key, int OffsetMinutes) CurrentMonth(ClientConfig client, DateTime utcNow)
    {
        var local = LocalTime.ToLocalOffset(utcNow, client.TimeZoneId);
        var first = new DateTime(local.Year, local.Month, 1);
        var fromUtc = LocalTime.FromLocal(first, client.TimeZoneId).UtcDateTime;
        return (fromUtc, first.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            first.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), (int)local.Offset.TotalMinutes);
    }

    /// <param name="client">Loaded with its workflows (GetWithWorkflowsAsync).</param>
    public async Task<PlanUsage> UsageAsync(ClientConfig client, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var plan = PlanCatalog.For(client.Plan);
        var month = CurrentMonth(client, now);
        var chats = await _subscriptions.CountChatsAsync(client.Id, month.FromUtc, now.AddMinutes(1), month.OffsetMinutes, cancellationToken);
        var staff = client.Vertical.UsesBookings()
            ? (await _bookings.GetStaffAsync(client.Id, activeOnly: true, cancellationToken)).Count
            : 0;
        return new PlanUsage(client, SubscriptionStatus.Of(client, now), month.Label, chats, plan.MonthlyChats,
            staff, plan.MaxStaff, ChannelsInUse(client), plan.MaxChannels);
    }
}
