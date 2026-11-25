using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Review booster. World.ReviewSalon = "E2E Review Salon", Manila, LINE, rating question sent at once after Completed.
/// A completed visit is written straight into the database with its feedback message due now; the reminder worker then
/// sends the rating question to the test customer.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReviewTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;
    private static string WebApi => GlobalSetup.Settings.WebApiUrl;
    private static int _number = 7000;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private const string GoogleLink = "https://g.page/r/REVIEWSALON/review";
    private const string FacebookLink = "https://www.facebook.com/reviewsalon/reviews";

    private sealed class Chat
    {
        public string User { get; }
        public LineCall Last { get; private set; } = null!;

        public Chat(string label) => User = Line.NewUser(label);

        private async Task<LineCall> SendAsync(object lineEvent)
        {
            var before = GlobalSetup.Fakes.LineCallsFor(User).Count;
            await Line.SendAsync(World.ReviewSalon, lineEvent);
            var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User)),
                c => c.Count > before, 15000, $"a reply to {User}");
            return Last = calls[^1];
        }

        public Task<LineCall> SayAsync(string text) => SendAsync(Line.TextEvent(User, text));
        public Task<LineCall> TapAsync(string label) => SendAsync(Line.TapEvent(User, Last.DataOf(label)));
        public async Task<LineCall> TapOnAsync(LineCall message, string label) => Last = await SendAsync(Line.TapEvent(User, message.DataOf(label)));

        public Task<LineCall> PushedAsync(Func<LineCall, bool> match, string what) =>
            Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User).Where(match).ToList()), l => l.Count > 0, 90000, what)
                .ContinueWith(t => Last = t.Result[0]);
    }

    /// <summary>A completed haircut yesterday for this chat user, with the rating question due now. Returns the booking id.</summary>
    private static async Task<Guid> CompletedVisitAsync(Chat chat, string name)
    {
        var id = Guid.NewGuid();
        var number = Interlocked.Increment(ref _number);
        var start = DateTime.UtcNow.AddDays(-1).Date.AddHours(3);
        await Db.ExecAsync(@"
            INSERT INTO dbo.Bookings (Id, ClientConfigId, BookingNumber, Reference, Source, Channel, CustomerId, CustomerName, ServiceName, DurationMinutes,
                StaffName, StaffPreference, StartUtc, EndUtc, BusyFromUtc, BusyUntilUtc, LocalDate, AtHome, Status, CreatedAtUtc)
            VALUES (@id, @c, @n, @ref, 'chat', 'line', @u, @name, 'Haircut', 45, 'Bea', 'any', @s, DATEADD(MINUTE, 45, @s), @s, DATEADD(MINUTE, 45, @s),
                CAST(@s AS date), 0, 3, SYSUTCDATETIME());
            INSERT INTO dbo.BookingMessages (Id, ClientConfigId, BookingId, Kind, DueUtc, CreatedAtUtc)
            VALUES (NEWID(), @c, @id, 'feedback', DATEADD(MINUTE, -1, SYSUTCDATETIME()), SYSUTCDATETIME());",
            ("@id", id), ("@c", World.ReviewSalon.Id), ("@n", number), ("@ref", $"BK-{number}"), ("@u", chat.User), ("@name", name), ("@s", start));
        return id;
    }

    private static HttpClient NoRedirects() => new(new HttpClientHandler { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> OpenAsync(string url, string userAgent)
    {
        using var http = NoRedirects();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await http.SendAsync(request);
    }

    private static Task<int> ClicksAsync() =>
        Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewClicks WHERE ClientConfigId = @c", ("@c", World.ReviewSalon.Id));

    private async Task SaveLinksAsync(string google, string facebook, bool enabled)
    {
        await Page.GotoAsync($"/Reviews?clientConfigId={World.ReviewSalon.Id}");
        await Page.Locator("[data-testid=review-google]").FillAsync(google);
        await Page.Locator("[data-testid=review-facebook]").FillAsync(facebook);
        if (enabled) await Page.Locator("[data-testid=review-enabled]").CheckAsync();
        else await Page.Locator("[data-testid=review-enabled]").UncheckAsync();
        await Page.Locator("[data-testid=review-links-save]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToBeVisibleAsync();
    }

    [Test]
    public async Task T125_Every_rating_gets_the_review_links_and_real_taps_are_counted()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await SaveLinksAsync("g.page/r/REVIEWSALON/review", FacebookLink, enabled: true);
        await Expect(Page.Locator("[data-testid=review-google]")).ToHaveValueAsync(GoogleLink);

        // 5 stars, no comment -> links.
        var happy = new Chat("t125a");
        var happyId = await CompletedVisitAsync(happy, "Maria Cruz");
        var question = await happy.PushedAsync(c => c.Text.StartsWith("Thank you for visiting"), "the rating question");
        await happy.TapOnAsync(question, "5 - Excellent");
        await happy.TapAsync("Skip");
        Assert.That(happy.Last.Text, Does.StartWith("Thank you for your feedback!"));
        Assert.That(happy.Last.Text, Does.Contain($"Google: {WebApi}/r/g/{happyId:N}").And.Contain($"Facebook: {WebApi}/r/f/{happyId:N}"));

        // 1 star with a comment -> the same links (Google does not allow asking only happy customers).
        var unhappy = new Chat("t125b");
        var unhappyId = await CompletedVisitAsync(unhappy, "Jose Reyes");
        question = await unhappy.PushedAsync(c => c.Text.StartsWith("Thank you for visiting"), "the rating question");
        await unhappy.TapOnAsync(question, "1 - Bad");
        await unhappy.SayAsync("Waited 40 minutes");
        Assert.That(unhappy.Last.Text, Does.Contain($"Google: {WebApi}/r/g/{unhappyId:N}"));
        Assert.That(await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE Id IN (@a, @b) AND ReviewAskedAtUtc IS NOT NULL",
            ("@a", happyId), ("@b", unhappyId)), Is.EqualTo(2));

        // The short link: a chat-app preview is forwarded but not counted; the customer's taps are.
        var link = $"{WebApi}/r/g/{happyId:N}";
        var before = await ClicksAsync();
        using (var preview = await OpenAsync(link, "facebookexternalhit/1.1"))
        {
            Assert.That((int)preview.StatusCode, Is.EqualTo(302));
            Assert.That(preview.Headers.Location?.ToString(), Is.EqualTo(GoogleLink));
        }
        Assert.That(await ClicksAsync(), Is.EqualTo(before));
        using (var tap = await OpenAsync(link, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)"))
            Assert.That(tap.Headers.Location?.ToString(), Is.EqualTo(GoogleLink));
        using (await OpenAsync(link, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)")) { }
        Assert.That(await ClicksAsync(), Is.EqualTo(before + 2));
        using (var unknown = await OpenAsync($"{WebApi}/r/g/{Guid.NewGuid():N}", "Mozilla/5.0"))
            Assert.That((int)unknown.StatusCode, Is.EqualTo(404));

        // The Reviews page: this month's numbers (the same customer tapping twice counts once).
        await Page.GotoAsync($"/Reviews?clientConfigId={World.ReviewSalon.Id}");
        await Expect(Page.Locator("[data-testid=tile-ratings-value]")).ToHaveTextAsync("2");
        await Expect(Page.Locator("[data-testid=tile-average-value]")).ToHaveTextAsync("3.0/5");
        await Expect(Page.Locator("[data-testid=tile-asked-value]")).ToHaveTextAsync("2");
        await Expect(Page.Locator("[data-testid=tile-clicks-value]")).ToHaveTextAsync("1");
        await Expect(Page.Locator("[data-testid=tile-clicks-note]")).ToContainTextAsync("Google 1");
        await Expect(Page.Locator("[data-testid=tile-rate-value]")).ToHaveTextAsync("50%");
        await Expect(Page.Locator("[data-testid=taps-by-site]")).ToContainTextAsync("Google");
        var low = Page.Locator("[data-testid=review-row]").Filter(new() { HasText = "Waited 40 minutes" });
        await Expect(low).ToContainTextAsync("★☆☆☆☆");
        await Expect(low).ToHaveAttributeAsync("class", "table-danger");
    }

    [Test]
    public async Task T126_Owner_checks_links_gets_AI_reply_drafts_and_can_switch_off()
    {
        // The salon's own portal user: menu link, link check, AI reply.
        await AdminSession.SignInAsync(Page, World.ReviewSalonPortalEmail, World.PortalPassword);
        await Page.Locator("[data-testid=nav-reviews]").ClickAsync();
        await Expect(Page.Locator("h2")).ToHaveTextAsync("Reviews");

        await Page.Locator("[data-testid=review-google]").FillAsync("http://example.com/review");
        await Page.Locator("[data-testid=review-links-save]").ClickAsync();
        await Expect(Page.Locator("[data-testid=review-google-error]")).ToContainTextAsync("starting with https://");

        await Page.SelectOptionAsync("[data-testid=draft-stars]", "4");
        await Page.Locator("[data-testid=draft-name]").FillAsync("Ana Santos");
        await Page.Locator("[data-testid=draft-review]").FillAsync("Nice haircut, a bit of a wait.");
        await Page.Locator("[data-testid=draft-submit]").ClickAsync();
        await Expect(Page.Locator("[data-testid=draft-text]")).ToHaveValueAsync("Thank you, Ana! [reply to a 4-star review]");

        // Switched off: the rating still runs, the thank-you has no links.
        await SaveLinksAsync(GoogleLink, "", enabled: false);
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("switched off");
        var chat = new Chat("t126");
        await CompletedVisitAsync(chat, "Liza Gomez");
        var question = await chat.PushedAsync(c => c.Text.StartsWith("Thank you for visiting"), "the rating question");
        await chat.TapOnAsync(question, "4 - Good");
        await chat.TapAsync("Skip");
        Assert.That(chat.Last.Text, Is.EqualTo("Thank you for your feedback!"));
        await SaveLinksAsync(GoogleLink, FacebookLink, enabled: true);

        // A clinic's draft keeps patient privacy; a Starter plan is locked.
        await Page.Locator("[data-testid=nav-logout]").ClickAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Reviews?clientConfigId={World.Clinic.Id}");
        await Page.Locator("[data-testid=draft-review]").FillAsync("Dr Rao explained everything.");
        await Page.Locator("[data-testid=draft-submit]").ClickAsync();
        await Expect(Page.Locator("[data-testid=draft-text]")).ToHaveValueAsync("Thank you! [reply to a 5-star review]");
        var system = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.System.StartsWith("You write a reply from a business")).System;
        Assert.That(system, Does.Contain("never confirm that the reviewer is a patient"));

        try
        {
            await Db.ExecAsync("UPDATE dbo.ClientConfigs SET [Plan] = 2, PlanEndsAtUtc = DATEADD(DAY, 10, SYSUTCDATETIME()) WHERE Id = @id", ("@id", World.ReviewSalon.Id));
            await Page.GotoAsync($"/Reviews?clientConfigId={World.ReviewSalon.Id}");
            await Expect(Page.Locator("[data-testid=reviews-plan-lock]")).ToBeVisibleAsync();
            await Expect(Page.Locator("[data-testid=plan-lock-reply]")).ToBeVisibleAsync();
        }
        finally
        {
            await Db.ExecAsync("UPDATE dbo.ClientConfigs SET [Plan] = 0, PlanEndsAtUtc = NULL WHERE Id = @id", ("@id", World.ReviewSalon.Id));
        }
    }
}
