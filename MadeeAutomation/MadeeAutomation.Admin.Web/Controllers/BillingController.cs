using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Billing;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// MadeeVision's billing: every client's plan, usage and renewal date; change a plan; record a payment.
/// MadeeVision staff only: there is no [ClientPortal] here, so a client's portal user gets 403.
/// </summary>
public class BillingController : Controller
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly SubscriptionService _service;
    private readonly IClientRepository _clients;

    public BillingController(ISubscriptionRepository subscriptions, SubscriptionService service, IClientRepository clients)
    {
        _subscriptions = subscriptions;
        _service = service;
        _clients = clients;
    }

    public async Task<IActionResult> Index(string? show = null, CancellationToken cancellationToken = default)
    {
        show = show is "attention" or "trial" or "paid" or "paused" or "demo" ? show : "all";
        var rows = new List<BillingRow>();
        foreach (var client in await _subscriptions.GetClientsAsync(cancellationToken))
        {
            var usage = await _service.UsageAsync(client, cancellationToken);
            rows.Add(new BillingRow(client, usage, PlanCatalog.For(client.Plan).IsPaid ? PlanCatalog.MonthlyValue(client) : null));
        }

        var today = DateOnly.FromDateTime(_service.UtcNow);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var payments = await _subscriptions.GetPaymentsAsync(firstOfMonth.AddDays(-1), today.AddDays(1), cancellationToken);

        var model = new BillingPageModel
        {
            Show = show,
            TotalClients = rows.Count,
            Trials = rows.Count(r => r.Client.Plan == PlanTier.Trial),
            Paying = rows.Count(r => PlanCatalog.For(r.Client.Plan).IsPaid && !r.Usage.Status.IsPaused),
            Attention = rows.Count(r => r.NeedsAttention),
            Paused = rows.Count(r => r.Usage.Status.IsPaused),
            MonthlyRevenue = rows.Where(r => r.MonthlyValue is not null && !r.Usage.Status.IsPaused)
                .GroupBy(r => r.Client.Currency).ToDictionary(g => g.Key, g => g.Sum(r => r.MonthlyValue!.Value)),
            PaidThisMonth = payments.Where(p => p.PaidOn >= firstOfMonth)
                .GroupBy(p => p.Currency).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount)),
            Rows = show switch
            {
                "attention" => rows.Where(r => r.NeedsAttention).ToList(),
                "trial" => rows.Where(r => r.Client.Plan == PlanTier.Trial).ToList(),
                "paid" => rows.Where(r => PlanCatalog.For(r.Client.Plan).IsPaid).ToList(),
                "paused" => rows.Where(r => r.Usage.Status.IsPaused).ToList(),
                "demo" => rows.Where(r => r.Client.Plan == PlanTier.Demo).ToList(),
                _ => rows
            }
        };
        // Soonest renewal first; clients without an end date (Demo) last.
        model.Rows = model.Rows.OrderBy(r => r.Usage.Status.EndsAtUtc ?? DateTime.MaxValue).ThenBy(r => r.Client.ClientName).ToList();
        return View(model);
    }

    public async Task<IActionResult> Client(Guid id, CancellationToken cancellationToken = default)
    {
        var model = await PageAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePlan([Bind(Prefix = "Change")] PlanChangeForm form, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(form.ClientConfigId);
        if (client is null) return NotFound();

        if (form.Plan != PlanTier.Demo && form.EndsOn is null)
            ModelState.AddModelError("Change.EndsOn", "Give the last day of the trial or paid period.");
        if (!ModelState.IsValid)
        {
            var page = (await PageAsync(client.Id, cancellationToken))!;
            page.Change = form;
            return View(nameof(Client), page);
        }

        var endUtc = form.Plan == PlanTier.Demo || form.EndsOn is not { } last ? (DateTime?)null : EndOfDayUtc(last, client.TimeZoneId);
        BillingCycle? cycle = PlanCatalog.For(form.Plan).IsPaid ? form.Cycle : null;
        var price = PlanCatalog.For(form.Plan).IsPaid ? form.Price : null;
        await _subscriptions.SetPlanAsync(client.Id, form.Plan, cycle, endUtc, price, Clean(form.Note), cancellationToken);

        var warning = LimitWarning(client, form.Plan);
        TempData["Success"] = $"{client.ClientName} is now on {PlanCatalog.For(form.Plan).Name}" +
                              (endUtc is { } e ? $" until {LocalDay(e, client)}." : ".") + (warning is null ? "" : " " + warning);
        return RedirectToAction(nameof(Client), new { id = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPayment([Bind(Prefix = "Payment")] PaymentForm form, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(form.ClientConfigId);
        if (client is null) return NotFound();

        if (!PlanCatalog.For(form.Plan).IsPaid) ModelState.AddModelError("Payment.Plan", "Choose Starter, Business or Premium.");
        if (!PaymentForm.Methods.Contains(form.Method)) ModelState.AddModelError("Payment.Method", "Choose how they paid.");
        var localToday = DateOnly.FromDateTime(LocalTime.ToLocal(_service.UtcNow, client.TimeZoneId));
        if (form.PaidOn == default) form.PaidOn = localToday;
        if (form.PaidOn > localToday) ModelState.AddModelError("Payment.PaidOn", "The payment date can't be in the future.");
        if (!ModelState.IsValid)
        {
            var page = (await PageAsync(client.Id, cancellationToken))!;
            page.Payment = form;
            return View(nameof(Client), page);
        }

        var now = _service.UtcNow;
        // A trial ends when they pay: the paid period starts now. Otherwise it continues from the current end.
        var from = client.Plan == PlanTier.Trial || client.PlanEndsAtUtc is not { } end || end < now ? now : end;
        var to = PlanCatalog.ExtendEnd(from, from, form.Cycle, form.Cycles);
        to = EndOfDayUtc(DateOnly.FromDateTime(LocalTime.ToLocal(to, client.TimeZoneId)), client.TimeZoneId);

        // Keep a price that differs from the list price as the agreed price, so renewals use it.
        var perPeriod = Math.Round(form.Amount / form.Cycles, 2);
        var list = PlanCatalog.ListPrice(form.Plan, client.Currency, form.Cycle);
        decimal? agreed = list is { } l && l == perPeriod ? null : perPeriod;

        await _subscriptions.AddPaymentAsync(new SubscriptionPayment
        {
            ClientConfigId = client.Id,
            PaidOn = form.PaidOn,
            Amount = form.Amount,
            Currency = client.Currency,
            Method = form.Method,
            Reference = Clean(form.Reference),
            Plan = form.Plan,
            Cycle = form.Cycle,
            Cycles = form.Cycles,
            PeriodFromUtc = from,
            PeriodToUtc = to,
            Note = Clean(form.Note),
            RecordedBy = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? User.Identity?.Name ?? "unknown",
            CreatedAtUtc = now
        }, agreed, cancellationToken);

        var warning = LimitWarning(client, form.Plan);
        TempData["Success"] = $"Payment of {Money.Format(form.Amount, client.Currency)} recorded. {client.ClientName} is on " +
                              $"{PlanCatalog.For(form.Plan).Name} until {LocalDay(to, client)}." + (warning is null ? "" : " " + warning);
        return RedirectToAction(nameof(Client), new { id = client.Id });
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<ClientPlanPageModel?> PageAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await _clients.GetWithWorkflowsAsync(id);
        if (client is null) return null;
        var localToday = DateOnly.FromDateTime(LocalTime.ToLocal(_service.UtcNow, client.TimeZoneId));
        var cycle = client.BillingCycle ?? BillingCycle.Monthly;
        var payPlan = PlanCatalog.For(client.Plan).IsPaid ? client.Plan : PlanTier.Business;
        return new ClientPlanPageModel
        {
            Client = client,
            Usage = await _service.UsageAsync(client, cancellationToken),
            Payments = await _subscriptions.GetPaymentsAsync(client.Id, cancellationToken),
            Change = new PlanChangeForm
            {
                ClientConfigId = client.Id,
                Plan = client.Plan,
                Cycle = cycle,
                EndsOn = client.PlanEndsAtUtc is { } e ? DateOnly.FromDateTime(LocalTime.ToLocal(e, client.TimeZoneId)) : null,
                Price = client.PlanPrice,
                Note = client.PlanNote
            },
            Payment = new PaymentForm
            {
                ClientConfigId = client.Id,
                Plan = payPlan,
                Cycle = cycle,
                PaidOn = localToday,
                Amount = (client.Plan == payPlan ? client.PlanPrice : null) ?? PlanCatalog.ListPrice(payPlan, client.Currency, cycle) ?? 0m
            }
        };
    }

    /// <summary>Plans with lower limits don't switch anything off by themselves - say what is over.</summary>
    private static string? LimitWarning(ClientConfig client, PlanTier tier)
    {
        var plan = PlanCatalog.For(tier);
        var channels = SubscriptionService.ChannelsInUse(client);
        return plan.MaxChannels is { } max && channels > max
            ? $"Note: {channels} channels are switched on but {plan.Name} allows {max} - disable a message workflow."
            : null;
    }

    /// <summary>The last moment of a local day, in UTC.</summary>
    private static DateTime EndOfDayUtc(DateOnly day, string timeZoneId) =>
        LocalTime.FromLocal(day.ToDateTime(new TimeOnly(23, 59, 59)), timeZoneId).UtcDateTime;

    private static string LocalDay(DateTime utc, ClientConfig client) =>
        LocalTime.ToLocal(utc, client.TimeZoneId).ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
