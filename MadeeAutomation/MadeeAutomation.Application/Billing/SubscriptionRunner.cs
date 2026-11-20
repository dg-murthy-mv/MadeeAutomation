using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Billing;

/// <summary>
/// Plan emails, each sent once (claimed in SubscriptionNotices):
/// 7 days and 1 day before the trial / paid period ends, when it ends (3-day grace), when the assistant
/// pauses, and when the month's customer chats reach 80% and 100% of the plan (warn only - nothing stops).
/// The owner (StaffAlertEmail) gets them, and MadeeVision (Billing:NotifyEmail) gets a copy.
/// </summary>
public class SubscriptionRunner
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly SubscriptionService _service;
    private readonly IEmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionRunner> _logger;

    public SubscriptionRunner(ISubscriptionRepository subscriptions, SubscriptionService service, IEmailSender email,
        IConfiguration configuration, ILogger<SubscriptionRunner> logger)
    {
        _subscriptions = subscriptions;
        _service = service;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed record Notice(string Key, string Subject, string Body);

    /// <summary>The notice due for this client right now (at most one about the period, one about chats).</summary>
    public static IEnumerable<Notice> DueNotices(PlanUsage usage, string? portalUrl)
    {
        var client = usage.Client;
        var status = usage.Status;
        var plan = status.Plan;
        var link = string.IsNullOrWhiteSpace(portalUrl) ? "" : $"\n\nSee your plan: {portalUrl.TrimEnd('/')}/Plan";
        var contact = "\n\nTo renew or upgrade, reply to this email or message MadeeVision on WhatsApp / facebook.com/madeevision.";

        if (status.EndsAtUtc is { } end)
        {
            var endKey = end.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            var endText = LocalDate(end, client);
            var pauseText = status.PausesAtUtc is { } p ? LocalDate(p, client) : endText;
            var what = status.State == SubscriptionState.Trial || client.Plan == PlanTier.Trial ? "free trial" : $"{plan.Name} plan";

            switch (status.State)
            {
                case SubscriptionState.Trial or SubscriptionState.Active when status.DaysLeft is <= 1:
                    yield return new Notice($"ends1:{endKey}", $"{client.ClientName}: last day of your {what} ({endText})",
                        $"Hello,\n\nYour MadeeAutomation {what} for {client.ClientName} ends on {endText}.\n" +
                        $"After that we keep everything running for {PlanCatalog.GraceDays} more days; then the booking assistant pauses " +
                        "and customers only get a short \"we'll reply soon\" message." + contact + link);
                    break;
                case SubscriptionState.Trial or SubscriptionState.Active when status.DaysLeft is <= 7:
                    yield return new Notice($"ends7:{endKey}", $"{client.ClientName}: your {what} ends on {endText}",
                        $"Hello,\n\nYour MadeeAutomation {what} for {client.ClientName} ends on {endText} ({status.DaysLeft} days left).\n" +
                        PlanLines(client) + contact + link);
                    break;
                case SubscriptionState.Grace:
                    yield return new Notice($"ended:{endKey}", $"{client.ClientName}: your {what} has ended - assistant pauses on {pauseText}",
                        $"Hello,\n\nYour MadeeAutomation {what} for {client.ClientName} ended on {endText}.\n" +
                        $"Everything keeps working until {pauseText}. If it is not renewed by then, the booking assistant pauses " +
                        "and customers only get a short \"we'll reply soon\" message." + contact + link);
                    break;
                case SubscriptionState.Paused:
                    yield return new Notice($"paused:{endKey}", $"{client.ClientName}: booking assistant paused",
                        $"Hello,\n\nThe MadeeAutomation {what} for {client.ClientName} was not renewed, so the booking assistant is paused.\n" +
                        "Customers who message you now get one short \"we'll reply soon\" message a day - please answer them yourself.\n" +
                        "Your services, staff and bookings are all kept. Renew and the assistant switches back on straight away." + contact + link);
                    break;
            }
        }

        if (usage.ChatLimit is { } limit && limit > 0 && status.State != SubscriptionState.Paused)
        {
            var monthKey = SubscriptionService.CurrentMonth(client, DateTime.UtcNow).Key;
            if (usage.Chats >= limit)
                yield return new Notice($"chats100:{monthKey}", $"{client.ClientName}: {usage.Chats:N0} customer chats this month (plan: {limit:N0})",
                    $"Hello,\n\n{client.ClientName} has had {usage.Chats:N0} customer chats in {usage.MonthLabel} - more than the {limit:N0} " +
                    $"in the {plan.Name} plan. Great news for your business!\n" +
                    "Nothing stops: the assistant keeps answering. We'll contact you about a plan that fits." + contact + link);
            else if (usage.Chats * 100 >= limit * 80)
                yield return new Notice($"chats80:{monthKey}", $"{client.ClientName}: 80% of this month's customer chats used",
                    $"Hello,\n\n{client.ClientName} has had {usage.Chats:N0} of the {limit:N0} customer chats in the {plan.Name} plan for {usage.MonthLabel}.\n" +
                    "Nothing stops if you go over - the assistant keeps answering - but a bigger plan may fit better." + contact + link);
        }
    }

    private static string PlanLines(ClientConfig client)
    {
        var sb = new StringBuilder("\nPlans (per month):\n");
        foreach (var p in PlanCatalog.Paid)
        {
            var price = PlanCatalog.ListPrice(p.Tier, client.Currency, BillingCycle.Monthly);
            sb.Append($"- {p.Name}{(price is { } m ? " " + Money.Format(m, client.Currency) : "")}: {p.Summary}\n");
        }
        sb.Append($"Pay yearly and get {12 - PlanCatalog.YearlyMonthsCharged} months free.");
        return sb.ToString();
    }

    private static string LocalDate(DateTime utc, ClientConfig client) =>
        LocalTime.ToLocal(utc, client.TimeZoneId).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    /// <returns>How many notices were emailed.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_email.IsConfigured) return 0;
        var copyTo = _configuration["Billing:NotifyEmail"];
        var portalUrl = _configuration["AdminWeb:PublicBaseUrl"];
        var sent = 0;

        foreach (var client in await _subscriptions.GetClientsAsync(cancellationToken))
        {
            if (client.Plan == PlanTier.Demo || client.Status is not (ClientStatus.Trial or ClientStatus.Active)) continue;
            try
            {
                var usage = await _service.UsageAsync(client, cancellationToken);
                foreach (var notice in DueNotices(usage, portalUrl))
                {
                    // Reaching 100% straight away also uses up the 80% notice.
                    if (notice.Key.StartsWith("chats100:"))
                        await _subscriptions.TryClaimNoticeAsync(client.Id, notice.Key.Replace("chats100:", "chats80:"), cancellationToken);
                    if (!await _subscriptions.TryClaimNoticeAsync(client.Id, notice.Key, cancellationToken)) continue;

                    var anyDelivered = false;
                    foreach (var to in new[] { client.StaffAlertEmail, copyTo }.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var subject = to == copyTo && to != client.StaffAlertEmail ? "[MadeeVision copy] " + notice.Subject : notice.Subject;
                        var result = await _email.SendAsync(to!, subject, notice.Body, cancellationToken);
                        if (result.Success) anyDelivered = true;
                        else _logger.LogWarning("Plan notice {Key} for {Client} could not be emailed to {To}: {Error}", notice.Key, client.ClientCode, to, result.Error);
                    }

                    if (anyDelivered) sent++;
                    else
                    {
                        // Nobody got it (no address, or the email service failed): try again next run.
                        await _subscriptions.ReleaseNoticeAsync(client.Id, notice.Key, CancellationToken.None);
                        if (string.IsNullOrWhiteSpace(client.StaffAlertEmail) && string.IsNullOrWhiteSpace(copyTo)) break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Plan notices for {Client} failed - will try again", client.ClientCode);
            }
        }
        return sent;
    }
}
