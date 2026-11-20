using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Plans: a new client starts on a 15-day trial; Starter limits staff and channels and locks the extra
/// booking options; plan emails go to the owner with a copy to MadeeVision; an unrenewed plan pauses the
/// assistant (one short reply a day); the owner sees the plan in the portal and can ask for one; recording
/// a payment renews it. World.PlanSalon (4 staff, LINE only) is the client these tests move between plans,
/// in this order: T120 -> T124.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PlanTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private async Task SignInAsync(string email, string password)
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, email, password);
    }

    private static Task SetPlanAsync(int plan, double? endsInHours) =>
        Db.ExecAsync("UPDATE dbo.ClientConfigs SET [Plan] = @p, PlanEndsAtUtc = CASE WHEN @h IS NULL THEN NULL ELSE DATEADD(MINUTE, CAST(@h * 60 AS int), SYSUTCDATETIME()) END WHERE Id = @id",
            ("@p", plan), ("@h", (object?)endsInHours ?? DBNull.Value), ("@id", World.PlanSalon.Id));

    private static async Task<Dictionary<string, object?>> PlanRowAsync() =>
        (await Db.RowsAsync("SELECT [Plan], PlanEndsAtUtc, PlanPrice, BillingCycle, Status, BookingWaitlistEnabled FROM dbo.ClientConfigs WHERE Id = @id",
            ("@id", World.PlanSalon.Id))).Single();

    private static Task<List<LineCall>> RepliesAsync(string user, int atLeast) =>
        Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(user).ToList()), c => c.Count >= atLeast, 15000, $"{atLeast} reply(ies) to {user}");

    private static List<LineCall> StaffMessages() => GlobalSetup.Fakes.LineCallsFor(World.PlanSalonStaffLineId).ToList();

    [Test]
    public async Task T120_A_new_client_starts_on_a_15_day_free_trial()
    {
        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync("/Clients/Create");
        await Page.FillAsync("#ClientName", "E2E Trial Salon");
        await Page.FillAsync("#ClientCode", "trialsalon");
        await Page.SelectOptionAsync("#Country", "PH");
        await Page.SelectOptionAsync("#TimeZoneId", "Asia/Manila");
        await Page.SelectOptionAsync("#Currency", "PHP");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToBeVisibleAsync();

        var row = (await Db.RowsAsync("SELECT [Plan], PlanEndsAtUtc FROM dbo.ClientConfigs WHERE ClientCode = 'trialsalon'")).Single();
        Assert.That(row["Plan"], Is.EqualTo(1), "Trial");
        var days = ((DateTime)row["PlanEndsAtUtc"]! - DateTime.UtcNow).TotalDays;
        Assert.That(days, Is.InRange(14.9, 15.1));

        await Page.GotoAsync("/Billing?show=trial");
        await Expect(Page.Locator("[data-testid=billing-row][data-client=trialsalon]")).ToContainTextAsync("Free trial - 15 days left");
        // Clients from before plans existed are Demo: no limits.
        await Page.GotoAsync("/Billing?show=demo");
        await Expect(Page.Locator($"[data-testid=billing-row][data-client=glowspa]")).ToContainTextAsync("Demo (no limits)");
    }

    [Test]
    public async Task T121_Starter_limits_staff_and_channels_and_locks_the_extra_options()
    {
        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Billing/Client/{World.PlanSalon.Id}");
        await Page.SelectOptionAsync("[data-testid=plan-tier]", "Starter");
        await Page.FillAsync("[data-testid=plan-ends]", DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd"));
        await Page.ClickAsync("[data-testid=plan-save]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("E2E Plan Salon is now on Starter until");
        await Expect(Page.Locator("[data-testid=usage-staff-value]")).ToHaveTextAsync("4 / 3");
        Assert.That((await PlanRowAsync())["Plan"], Is.EqualTo(2));

        // A 5th working stylist is refused.
        await Page.GotoAsync($"/Staff/Create?clientConfigId={World.PlanSalon.Id}");
        await Page.FillAsync("[data-testid=staff-name]", "Fe");
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-testid=staff-form-error]")).ToContainTextAsync("The Starter plan allows up to 3 working staff");

        // A second messaging channel is refused.
        await Page.GotoAsync($"/Clients/CreateWorkflow?clientConfigId={World.PlanSalon.Id}");
        await Page.FillAsync("#Name", "WhatsApp bookings");
        await Page.SelectOptionAsync("#HandlerKey", "booking-message");
        await Page.SelectOptionAsync("#Channel", "whatsapp");
        await Page.ClickAsync("[data-testid=save-workflow]");
        await Expect(Page.Locator("body")).ToContainTextAsync("The Starter plan allows 1 messaging channel");

        // Waitlist, 2-hour reminder and ratings are locked; saving keeps the stored choice for an upgrade.
        await Page.GotoAsync($"/Bookings/Settings?clientConfigId={World.PlanSalon.Id}");
        await Expect(Page.Locator("[data-testid=plan-lock-waitlist]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=plan-lock-reminder2]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=plan-lock-feedback]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=set-waitlist]")).ToBeDisabledAsync();
        await Expect(Page.Locator("[data-testid=set-waitlist]")).Not.ToBeCheckedAsync();
        await Page.ClickAsync("[data-testid=save-settings]");
        Assert.That((await PlanRowAsync())["BookingWaitlistEnabled"], Is.EqualTo(true));
    }

    [Test]
    public async Task T122_Plan_emails_then_a_paused_assistant_answers_once_a_day()
    {
        // Ending in 4 days: the "ends on" email to the owner, with a copy to MadeeVision.
        await SetPlanAsync(2, 100);
        var ending = await Mailbox.WaitForAsync(World.PlanSalonOwnerEmail);
        Assert.That(ending.Any(m => m.Subject.Contains("your Starter plan ends on")), Is.True, string.Join(" | ", ending.Select(m => m.Subject)));
        var copies = await Mailbox.WaitForAsync(World.BillingEmail);
        Assert.That(copies.Any(m => m.Subject.StartsWith("[MadeeVision copy] E2E Plan Salon")), Is.True);

        // Ended 4 days ago (grace over): paused.
        await SetPlanAsync(2, -100);
        await Eventually.GetAsync(() => Task.FromResult(Mailbox.To(World.PlanSalonOwnerEmail)),
            m => m.Any(x => x.Subject.Contains("booking assistant paused")), 20000, "the paused email");

        var before = StaffMessages().Count;
        var customer = Line.NewUser("paused");
        await Line.SendAsync(World.PlanSalon, Line.TextEvent(customer, "hello, can I book a haircut?"));
        var replies = await RepliesAsync(customer, 1);
        Assert.That(replies[0].Text, Is.EqualTo("Thanks for your message! A member of our team will reply shortly."));
        Assert.That(replies[0].Buttons, Is.Empty, "no booking buttons while paused");

        await Line.SendAsync(World.PlanSalon, Line.TextEvent(customer, "hello?"));
        await Task.Delay(4000);
        Assert.That(GlobalSetup.Fakes.LineCallsFor(customer).Count, Is.EqualTo(1), "only one short reply a day");

        var other = Line.NewUser("paused2");
        await Line.SendAsync(World.PlanSalon, Line.TextEvent(other, "hi"));
        await RepliesAsync(other, 1);
        await Task.Delay(1000);
        Assert.That(StaffMessages().Skip(before).Count(m => m.Text.Contains("not renewed")), Is.EqualTo(1), "one staff alert a day");
    }

    [Test]
    public async Task T123_The_owner_sees_the_plan_in_the_portal_and_asks_for_one()
    {
        await SignInAsync(World.PlanSalonPortalEmail, World.PortalPassword);
        await Expect(Page.Locator("[data-testid=plan-banner]")).ToHaveAttributeAsync("data-state", "Paused");
        await Page.ClickAsync("[data-testid=nav-plan]");
        await Expect(Page.Locator("[data-testid=plan-label]")).ToHaveTextAsync("Starter - paused (not renewed)");
        await Expect(Page.Locator("[data-testid=price-Business]")).ToHaveTextAsync("₱2,990.00");

        await Page.ClickAsync("[data-testid=ask-Business]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("We've received your request for the Business plan");
        var request = await Eventually.GetAsync(() => Task.FromResult(Mailbox.To(World.BillingEmail)),
            m => m.Any(x => x.Subject.StartsWith("Plan request")), 15000, "the plan request email");
        var mail = request.First(x => x.Subject.StartsWith("Plan request"));
        Assert.That(mail.Subject, Is.EqualTo("Plan request: E2E Plan Salon wants Business"));
        Assert.That(mail.Body, Does.Contain(World.PlanSalonPortalEmail));

        var billing = await Page.GotoAsync("/Billing");
        Assert.That(billing!.Status, Is.EqualTo(403), "MadeeVision's billing is closed to clients");
    }

    [Test]
    public async Task T124_Recording_a_payment_renews_the_plan_and_the_assistant_answers_again()
    {
        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Billing/Client/{World.PlanSalon.Id}");
        await Page.SelectOptionAsync("[data-testid=pay-plan]", "Business");
        await Page.FillAsync("[data-testid=pay-amount]", "2990");
        await Page.SelectOptionAsync("[data-testid=pay-method]", "GCash");
        await Page.FillAsync("[data-testid=pay-reference]", "GC-112233");
        await Page.ClickAsync("[data-testid=pay-save]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Payment of ₱2,990.00 recorded. E2E Plan Salon is on Business until");
        await Expect(Page.Locator("[data-testid=payment-row]")).ToContainTextAsync("GCash (GC-112233)");
        await Expect(Page.Locator("[data-testid=plan-status]")).ToHaveAttributeAsync("data-state", "Active");

        var row = await PlanRowAsync();
        Assert.That(row["Plan"], Is.EqualTo(3), "Business");
        Assert.That(row["PlanPrice"], Is.Null, "list price");
        Assert.That(((DateTime)row["PlanEndsAtUtc"]! - DateTime.UtcNow).TotalDays, Is.InRange(27, 32.1));
        var payment = (await Db.RowsAsync("SELECT Amount, Method, Reference, RecordedBy FROM dbo.SubscriptionPayments WHERE ClientConfigId = @id",
            ("@id", World.PlanSalon.Id))).Single();
        Assert.That(payment["Amount"], Is.EqualTo(2990m));
        Assert.That(payment["RecordedBy"], Is.EqualTo(World.AdminEmail));

        // The booking assistant is back.
        var customer = Line.NewUser("renewed");
        await Line.SendAsync(World.PlanSalon, Line.TextEvent(customer, "hello"));
        var replies = await RepliesAsync(customer, 1);
        // The real assistant (one category, so it shows the services and "My bookings"), not the short paused reply.
        Assert.That(replies[0].Text, Is.Not.EqualTo("Thanks for your message! A member of our team will reply shortly."));
        Assert.That(replies[0].Buttons, Does.Contain("My bookings"));

        // Business includes the waitlist again - the saved choice comes back.
        await Page.GotoAsync($"/Bookings/Settings?clientConfigId={World.PlanSalon.Id}");
        await Expect(Page.Locator("[data-testid=plan-lock-waitlist]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=set-waitlist]")).ToBeCheckedAsync();

        await Page.GotoAsync("/Billing?show=paid");
        await Expect(Page.Locator("[data-testid=tile-mrr-value]")).ToContainTextAsync("₱2,990.00");
    }
}
