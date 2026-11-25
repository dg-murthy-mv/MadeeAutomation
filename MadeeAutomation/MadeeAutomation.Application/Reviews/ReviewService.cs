using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.Reviews;

/// <summary>Ratings and review-link numbers for one period.</summary>
public sealed class ReviewPeriod
{
    public int Ratings { get; init; }
    public decimal? Average { get; init; }

    /// <summary>How many 1..5 star ratings (index 0 = 1 star).</summary>
    public int[] Spread { get; init; } = new int[5];

    /// <summary>Customers sent the review links.</summary>
    public int Asked { get; init; }

    /// <summary>Customers who opened the Google / Facebook link (each customer counted once per site).</summary>
    public int GoogleClicks { get; init; }
    public int FacebookClicks { get; init; }

    public int Clicks => GoogleClicks + FacebookClicks;
}

/// <summary>Everything the Reviews page shows.</summary>
public sealed class ReviewDashboard
{
    public required ClientConfig Client { get; init; }
    public required string MonthName { get; init; }
    public required string LastMonthName { get; init; }
    public required ReviewPeriod ThisMonth { get; init; }
    public required ReviewPeriod LastMonth { get; init; }
    public required ReviewPeriod AllTime { get; init; }

    /// <summary>The latest rated visits (newest first), with comments.</summary>
    public required List<Booking> Recent { get; init; }

    /// <summary>The plan includes ratings (and so review links and AI reply drafts).</summary>
    public bool PlanIncludes { get; init; }

    /// <summary>The links go through our short link, so clicks are counted.</summary>
    public bool ClicksCounted { get; init; }

    /// <summary>This month's average minus last month's (null when either month has no ratings).</summary>
    public decimal? AverageChange => ThisMonth.Average is { } a && LastMonth.Average is { } b ? a - b : null;
}

/// <summary>
/// Review booster:
///   - after a customer rates their visit, they get a one-tap link to review the business on Google
///     (and Facebook when set). Every customer who answers the rating gets it - whatever stars they
///     gave. Google does not allow asking only happy customers ("review gating").
///   - the links go through our WebApi (/r/g/{booking}), which counts the click and forwards the
///     customer. Link previews made by the chat apps are not counted.
///   - the numbers for the Reviews page.
/// Config: WebApi:PublicBaseUrl - the WebApi's public address. Not set = the plain review link is sent
/// (it still works, but clicks are not counted).
/// </summary>
public class ReviewService
{
    private readonly IReviewRepository _reviews;
    private readonly ICurrentClientService _clients;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(IReviewRepository reviews, ICurrentClientService clients, IConfiguration configuration, TimeProvider clock,
        ILogger<ReviewService> logger)
    {
        _reviews = reviews;
        _clients = clients;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The WebApi's public address (no trailing /), or null when not set.</summary>
    public string? PublicBaseUrl =>
        _configuration["WebApi:PublicBaseUrl"] is { Length: > 0 } url && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http"
            ? url.Trim().TrimEnd('/')
            : null;

    // ---------------------------------------------------------------------------------------------
    // Links
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks a review link typed in Admin. Empty = no link (fine). Returns the cleaned link, or an error.
    /// Only https links are accepted (Google and Facebook links always are).
    /// </summary>
    public static (string? Url, string? Error) CleanLink(string? input, string site)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, null);
        var text = input.Trim();
        if (text.Length > 500) return (null, "The link is too long (500 characters at most).");
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Contains('.'))
            return (null, $"Paste the full {Name(site)} link, starting with https://");
        return (uri.ToString(), null);
    }

    public static string Name(string site) => site == ReviewSites.Facebook ? "Facebook" : "Google";

    /// <summary>The link sent to the customer: our short link when the WebApi address is set, else the review link itself.</summary>
    public string LinkFor(ClientConfig client, Guid bookingId, string site)
    {
        var target = site == ReviewSites.Facebook ? client.FacebookReviewUrl : client.GoogleReviewUrl;
        return PublicBaseUrl is { } baseUrl
            ? $"{baseUrl}/r/{ReviewSites.Letter(site)}/{bookingId:N}"
            : target ?? "";
    }

    /// <summary>The review request added after the rating, or null when this client does not send one.</summary>
    public string? RequestText(ClientConfig client, Guid bookingId)
    {
        if (!client.UsesReviewRequests) return null;
        var lines = new List<string>
        {
            $"Would you mind sharing your experience in a short review? It helps other customers find {client.ClientName}:"
        };
        if (!string.IsNullOrWhiteSpace(client.GoogleReviewUrl)) lines.Add($"⭐ Google: {LinkFor(client, bookingId, ReviewSites.Google)}");
        if (!string.IsNullOrWhiteSpace(client.FacebookReviewUrl)) lines.Add($"👍 Facebook: {LinkFor(client, bookingId, ReviewSites.Facebook)}");
        lines.Add("Thank you!");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The review request for a customer who just answered the rating question (any stars), and marks the
    /// booking as asked. isTest = the Admin "Test" console: the text is shown, nothing is saved.
    /// </summary>
    public async Task<string?> RequestAsync(ClientConfig client, Guid? bookingId, bool isTest, CancellationToken cancellationToken = default)
    {
        if (bookingId is not { } id) return null;
        var text = RequestText(client, id);
        if (text is null || isTest) return text;
        try
        {
            await _reviews.SetAskedAsync(client.Id, id, UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The links still go out; only the "asked" count misses one.
            _logger.LogWarning(ex, "Could not mark booking {BookingId} as asked for a review", id);
        }
        return text;
    }

    /// <summary>
    /// A customer opened /r/{letter}/{booking}: counts the click (not for link previews) and returns where to send
    /// them - null when the link is unknown or the business removed its review link.
    /// </summary>
    public async Task<string?> OpenLinkAsync(string? letter, string? bookingKey, string? userAgent, CancellationToken cancellationToken = default)
    {
        var site = ReviewSites.FromLetter(letter);
        if (site is null || !Guid.TryParseExact(bookingKey ?? "", "N", out var bookingId)) return null;

        var clientId = await _reviews.GetBookingClientAsync(bookingId, cancellationToken);
        if (clientId is null) return null;
        var client = await _clients.GetClientAsync(clientId.Value, cancellationToken);
        var target = site == ReviewSites.Facebook ? client?.FacebookReviewUrl : client?.GoogleReviewUrl;
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (!IsLinkPreview(userAgent))
        {
            try
            {
                await _reviews.AddClickAsync(new ReviewClick
                {
                    ClientConfigId = clientId.Value,
                    BookingId = bookingId,
                    Site = site,
                    ClickedAtUtc = UtcNow
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Forward the customer anyway: a lost count is better than a broken link.
                _logger.LogWarning(ex, "Could not count a review link click for booking {BookingId}", bookingId);
            }
        }
        return target;
    }

    /// <summary>
    /// WhatsApp, Messenger, LINE and other apps open a link once to draw its preview. Those visits are not
    /// customers and are not counted.
    /// </summary>
    public static bool IsLinkPreview(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return true;
        var ua = userAgent.ToLowerInvariant();
        string[] bots = { "facebookexternalhit", "facebot", "meta-externalagent", "whatsapp", "linespider", "line-poker", "bot", "crawler",
                          "spider", "preview", "slackbot", "telegrambot", "skypeuripreview", "curl/", "python-requests", "go-http-client" };
        return bots.Any(ua.Contains);
    }

    // ---------------------------------------------------------------------------------------------
    // Dashboard
    // ---------------------------------------------------------------------------------------------

    public async Task<ReviewDashboard> DashboardAsync(ClientConfig client, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var local = LocalTime.ToLocal(now, client.TimeZoneId);
        var monthStart = new DateTime(local.Year, local.Month, 1);
        var thisFrom = LocalTime.FromLocal(monthStart, client.TimeZoneId).UtcDateTime;
        var lastFrom = LocalTime.FromLocal(monthStart.AddMonths(-1), client.TimeZoneId).UtcDateTime;
        var allFrom = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = now.AddMinutes(1);

        var allRated = await _reviews.GetRatedAsync(client.Id, allFrom, end, cancellationToken);
        var allClicks = await _reviews.GetClicksAsync(client.Id, allFrom, end, cancellationToken);

        async Task<ReviewPeriod> PeriodAsync(DateTime from, DateTime to) =>
            Period(allRated.Where(b => b.FeedbackAtUtc >= from && b.FeedbackAtUtc < to).ToList(),
                allClicks.Where(c => c.ClickedAtUtc >= from && c.ClickedAtUtc < to).ToList(),
                await _reviews.CountAskedAsync(client.Id, from, to, cancellationToken));

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return new ReviewDashboard
        {
            Client = client,
            MonthName = monthStart.ToString("MMMM yyyy", culture),
            LastMonthName = monthStart.AddMonths(-1).ToString("MMMM", culture),
            ThisMonth = await PeriodAsync(thisFrom, end),
            LastMonth = await PeriodAsync(lastFrom, thisFrom),
            AllTime = await PeriodAsync(allFrom, end),
            Recent = allRated.Take(20).ToList(),
            PlanIncludes = PlanCatalog.For(client.Plan).Feedback,
            ClicksCounted = PublicBaseUrl is not null
        };
    }

    private static ReviewPeriod Period(List<Booking> rated, List<ReviewClick> clicks, int asked)
    {
        var stars = rated.Where(b => b.FeedbackRating is >= 1 and <= 5).Select(b => b.FeedbackRating!.Value).ToList();
        var spread = new int[5];
        foreach (var s in stars) spread[s - 1]++;

        // One customer tapping the same link three times is one click.
        int Unique(string site) => clicks.Where(c => c.Site == site)
            .Select(c => c.BookingId?.ToString() ?? c.Id.ToString()).Distinct().Count();

        return new ReviewPeriod
        {
            Ratings = stars.Count,
            Average = stars.Count == 0 ? null : Math.Round((decimal)stars.Average(), 1, MidpointRounding.AwayFromZero),
            Spread = spread,
            Asked = asked,
            GoogleClicks = Unique(ReviewSites.Google),
            FacebookClicks = Unique(ReviewSites.Facebook)
        };
    }
}

/// <summary>
/// AI reply drafts: the owner pastes a review from Google or Facebook, the AI writes a short reply for
/// them to check, change and post. Runs in WebApi (it has the Claude key); Admin calls it.
/// Nothing is posted anywhere automatically.
/// </summary>
public class ReviewReplyWriter
{
    private readonly IClaudeService _claude;
    private readonly ILogger<ReviewReplyWriter> _logger;

    public ReviewReplyWriter(IClaudeService claude, ILogger<ReviewReplyWriter> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    public const int MaxReviewLength = 3000;

    public sealed record Result(bool Success, string? Draft, string? Error);

    public async Task<Result> DraftAsync(ClientConfig client, int? stars, string? reviewerName, string review, string? tone,
        CancellationToken cancellationToken = default)
    {
        review = (review ?? "").Trim();
        if (review.Length == 0 && stars is null) return new Result(false, null, "Paste the review (or choose the stars).");
        if (review.Length > MaxReviewLength) review = review[..MaxReviewLength];
        var clinic = client.Vertical == Domain.Enums.VerticalPack.Clinic;

        var system =
            $"You write a reply from a business to a customer review it received on Google or Facebook. The business: {client.ClientName} " +
            $"({(clinic ? "a medical clinic" : "a spa / salon / wellness business")}) in {client.Country}.\n" +
            "Rules:\n" +
            "- 2 to 4 short sentences, warm and professional, in the SAME language as the review (English if unsure).\n" +
            "- Thank the reviewer, by first name when a name is given.\n" +
            "- Good review: thank them for specific things they mentioned and invite them back.\n" +
            "- Mixed or bad review: thank them, apologise sincerely without excuses or blame, say the team will look into it, and invite them " +
            "to message the business directly so it can be put right. Never argue.\n" +
            "- Do not invent facts, names, offers, discounts or promises. Do not mention any price.\n" +
            (clinic
                ? "- Privacy: never confirm that the reviewer is a patient, and never mention any treatment, condition, test or visit detail.\n"
                : "") +
            (string.IsNullOrWhiteSpace(tone) ? "" : $"- The owner's wish for the tone: {tone.Trim()}\n") +
            "Answer with the reply text only - no quotes, no title, no notes.";
        var user = $"Stars: {(stars is { } s ? $"{s} of 5" : "not given")}\n" +
                   $"Reviewer: {(string.IsNullOrWhiteSpace(reviewerName) ? "not given" : reviewerName.Trim())}\n" +
                   $"Review:\n{(review.Length == 0 ? "(no text, stars only)" : review)}";

        try
        {
            string draft;
            using (AiUsageScope.Begin(client.Id, "review-reply"))
                draft = (await _claude.CompleteAsync(system, user, cancellationToken)).Trim().Trim('"').Trim();
            if (draft.Length is 0 or > 1500) return new Result(false, null, "The AI answer was not usable - please try again.");
            return new Result(true, draft, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Review reply draft failed for {Client}", client.ClientCode);
            return new Result(false, null, "The AI could not write a draft just now - please try again in a minute.");
        }
    }
}
