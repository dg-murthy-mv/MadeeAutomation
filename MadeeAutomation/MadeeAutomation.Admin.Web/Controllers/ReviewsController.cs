using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Reviews;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// Review booster (client portal + MadeeVision staff): ratings this month vs last month, how many customers got the
/// review links and opened them, the latest comments, the review links themselves, and AI reply drafts.
/// </summary>
public class ReviewsController : ClientScopedController
{
    private readonly IClientRepository _clients;
    private readonly IReviewRepository _reviews;
    private readonly ReviewService _service;
    private readonly IApiService _api;

    public ReviewsController(IClientRepository clients, IReviewRepository reviews, ReviewService service, IApiService api)
    {
        _clients = clients;
        _reviews = reviews;
        _service = service;
        _api = api;
    }

    public async Task<IActionResult> Index(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(await PageAsync(client, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLinks([Bind(Prefix = "Links")] ReviewLinksForm form, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(form.ClientConfigId));
        if (client is null) return NotFound();

        var (google, googleError) = ReviewService.CleanLink(form.GoogleReviewUrl, ReviewSites.Google);
        var (facebook, facebookError) = ReviewService.CleanLink(form.FacebookReviewUrl, ReviewSites.Facebook);
        if (googleError is not null) ModelState.AddModelError("Links.GoogleReviewUrl", googleError);
        if (facebookError is not null) ModelState.AddModelError("Links.FacebookReviewUrl", facebookError);
        if (!ModelState.IsValid)
        {
            var page = await PageAsync(client, cancellationToken);
            page.Links = form;
            return View(nameof(Index), page);
        }

        await _reviews.SaveLinksAsync(client.Id, google, facebook, form.Enabled, cancellationToken);
        TempData["Success"] = google is null && facebook is null
            ? "Review links removed - customers are no longer asked for a review."
            : form.Enabled
                ? "Review links saved. Every customer who answers the rating question now gets them."
                : "Review links saved, but sending them is switched off.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Draft([Bind(Prefix = "Draft")] ReviewDraftForm form, CancellationToken cancellationToken = default)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(form.ClientConfigId));
        if (client is null) return NotFound();
        var page = await PageAsync(client, cancellationToken);
        page.Draft = form;

        if (!page.Dashboard.PlanIncludes)
            page.DraftError = "AI reply drafts are not in your plan. See Plan to upgrade.";
        else if (string.IsNullOrWhiteSpace(form.Review) && form.Stars is null)
            ModelState.AddModelError("Draft.Review", "Paste the review you'd like to answer.");

        if (ModelState.IsValid && page.DraftError is null)
        {
            var result = await _api.DraftReviewReplyAsync(client.Id, form.Stars, form.ReviewerName, form.Review ?? "", form.Tone, cancellationToken);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Draft)) page.DraftText = result.Draft;
            else page.DraftError = result.Error ?? "No draft came back - please try again.";
        }
        return View(nameof(Index), page);
    }

    private async Task<ReviewsPageModel> PageAsync(ClientConfig client, CancellationToken cancellationToken) => new()
    {
        Dashboard = await _service.DashboardAsync(client, cancellationToken),
        Links = new ReviewLinksForm
        {
            ClientConfigId = client.Id,
            GoogleReviewUrl = client.GoogleReviewUrl,
            FacebookReviewUrl = client.FacebookReviewUrl,
            Enabled = client.ReviewRequestsEnabled
        },
        Draft = new ReviewDraftForm { ClientConfigId = client.Id, Stars = 5 }
    };
}
