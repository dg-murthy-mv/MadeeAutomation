using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Billing;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// The client's own plan page (portal "Plan"): what they are on, days left, this month's usage, the plans,
/// and an "I'd like this plan" button that emails MadeeVision. MadeeVision staff see the same page.
/// </summary>
public class PlanController : ClientScopedController
{
    private readonly IClientRepository _clients;
    private readonly SubscriptionService _service;
    private readonly IEmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlanController> _logger;

    public PlanController(IClientRepository clients, SubscriptionService service, IEmailSender email, IConfiguration configuration,
        ILogger<PlanController> logger)
    {
        _clients = clients;
        _service = service;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(new MyPlanPageModel
        {
            Client = client,
            Usage = await _service.UsageAsync(client, cancellationToken),
            Price = PlanCatalog.For(client.Plan).IsPaid ? PlanCatalog.PriceFor(client) : null
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(Guid clientConfigId, PlanTier plan, BillingCycle cycle, string? message,
        CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        if (!PlanCatalog.For(plan).IsPaid) return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });

        var wanted = PlanCatalog.For(plan);
        var price = PlanCatalog.ListPrice(plan, client.Currency, cycle);
        var to = _configuration["Billing:NotifyEmail"];
        var body =
            $"{client.ClientName} ({client.ClientCode}, {client.Country}) would like the {wanted.Name} plan, billed {cycle.ToString().ToLowerInvariant()}" +
            (price is { } p ? $" ({Money.Format(p, client.Currency)})" : "") + ".\n\n" +
            $"Asked by: {SignedInAs} ({User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "-"})\n" +
            $"Now on: {SubscriptionStatus.Of(client, _service.UtcNow).Label}\n" +
            $"Staff alert email: {client.StaffAlertEmail ?? "-"}\n" +
            (string.IsNullOrWhiteSpace(message) ? "" : $"\nTheir message:\n{message.Trim()}\n") +
            $"\nRecord the payment in Admin -> Billing -> {client.ClientName}.";

        var sent = false;
        if (_email.IsConfigured && !string.IsNullOrWhiteSpace(to))
        {
            var result = await _email.SendAsync(to, $"Plan request: {client.ClientName} wants {wanted.Name}", body, cancellationToken);
            sent = result.Success;
            if (!result.Success) _logger.LogWarning("Plan request from {Client} could not be emailed: {Error}", client.ClientCode, result.Error);
        }
        else
        {
            _logger.LogWarning("Plan request from {Client} for {Plan}: Billing:NotifyEmail or email is not set up. {Body}", client.ClientCode, wanted.Name, body);
        }

        if (sent)
            TempData["Success"] = $"Thank you! We've received your request for the {wanted.Name} plan. MadeeVision will contact you within one working day with the payment details.";
        else
            TempData["Error"] = $"Your request for the {wanted.Name} plan could not be sent automatically. Please message MadeeVision on WhatsApp or facebook.com/madeevision.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }
}
