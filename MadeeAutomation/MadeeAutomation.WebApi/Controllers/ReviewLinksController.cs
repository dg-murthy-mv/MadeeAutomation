using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Reviews;
using MadeeAutomation.WebApi.Security;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.WebApi.Controllers;

/// <summary>
/// Review booster short links sent to customers after they rate a visit:
///   GET /r/g/{bookingId} -> counts the click, then forwards to the business's Google review page
///   GET /r/f/{bookingId} -> the same for Facebook
/// Open to everyone (customers tap them in WhatsApp, Messenger or LINE). They only ever forward to the
/// review link the business saved in Admin - never to an address taken from the request.
/// </summary>
[ApiController]
[Route("r")]
public class ReviewLinksController : ControllerBase
{
    private readonly ReviewService _reviews;

    public ReviewLinksController(ReviewService reviews) => _reviews = reviews;

    [HttpGet("{site}/{bookingKey}")]
    public async Task<IActionResult> Open(string site, string bookingKey, CancellationToken cancellationToken)
    {
        var target = await _reviews.OpenLinkAsync(site, bookingKey, Request.Headers.UserAgent.ToString(), cancellationToken);
        if (target is null)
            return new ContentResult
            {
                StatusCode = StatusCodes.Status404NotFound,
                ContentType = "text/html; charset=utf-8",
                Content = "<!doctype html><meta name=viewport content=\"width=device-width\"><title>Link not found</title>" +
                          "<p style=\"font-family:sans-serif;padding:24px\">Sorry, this review link is no longer available. Thank you for your visit!</p>"
            };
        Response.Headers.CacheControl = "no-store";
        return Redirect(target);
    }
}

public class ReviewReplyRequest
{
    public int? Stars { get; set; }
    public string? ReviewerName { get; set; }
    public string Review { get; set; } = string.Empty;
    public string? Tone { get; set; }
}

/// <summary>AI reply drafts for the Admin Reviews page (header X-Internal-Key). The Claude key lives only in WebApi.</summary>
[ApiController]
[Route("api/internal/clients/{clientConfigId:guid}/reviews")]
[RequireInternalKey]
public class ReviewsInternalController : ControllerBase
{
    private readonly ReviewReplyWriter _writer;
    private readonly ICurrentClientService _clients;

    public ReviewsInternalController(ReviewReplyWriter writer, ICurrentClientService clients)
    {
        _writer = writer;
        _clients = clients;
    }

    [HttpPost("draft-reply")]
    public async Task<IActionResult> DraftReply(Guid clientConfigId, [FromBody] ReviewReplyRequest request, CancellationToken cancellationToken)
    {
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (client is null) return NotFound(new { success = false, error = "Client not found." });
        var result = await _writer.DraftAsync(client, request.Stars, request.ReviewerName, request.Review, request.Tone, cancellationToken);
        return Ok(new { success = result.Success, draft = result.Draft, error = result.Error });
    }
}
