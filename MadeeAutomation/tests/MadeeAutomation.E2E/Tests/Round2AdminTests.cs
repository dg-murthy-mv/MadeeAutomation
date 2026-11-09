using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Round 002 Admin features: secret fields, Check LINE, workflow Test button, staff alert settings, Inbound events.</summary>
[TestFixture]
[NonParallelizable]
public class Round2AdminTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public async Task SignInAsync()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page.Locator("[data-testid=nav-logout]")).ToBeVisibleAsync();
    }

    [Test]
    public async Task T37_Credential_fields_are_not_password_inputs()
    {
        await Page.GotoAsync($"/Clients/Edit/{World.Bakery.Id}");
        await Expect(Page.Locator("#LineChannelSecret")).ToHaveAttributeAsync("type", "text");
        await Expect(Page.Locator("#LineChannelAccessToken")).ToHaveAttributeAsync("autocomplete", "off");
        await Expect(Page.Locator("input[type=password]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task T38_Check_LINE_access_token()
    {
        await Page.GotoAsync($"/Clients/Details/{World.Bakery.Id}");
        await Page.ClickAsync("[data-testid=check-line]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("E2E Bot");

        await Page.GotoAsync($"/Clients/Details/{World.FailSend.Id}");
        await Page.ClickAsync("[data-testid=check-line]");
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("rejected the channel access token");
    }

    [Test]
    public async Task T39_Test_button_runs_the_AI_but_sends_nothing()
    {
        var ordersBefore = GlobalSetup.Fakes.PosOrders.Count;
        var lineBefore = GlobalSetup.Fakes.LineCalls.Count;

        await Page.GotoAsync($"/Clients/TestWorkflow/{World.Bakery.WorkflowId}");
        await Page.FillAsync("#Message", "3 croissants for tomorrow");
        await Page.ClickAsync("[data-testid=run-test]");

        await Expect(Page.Locator("[data-testid=test-status]")).ToHaveTextAsync("Succeeded", new() { Timeout = 30000 });
        await Expect(Page.Locator("[data-testid=test-summary]")).ToContainTextAsync("[Test] Order TEST-ORDER created");
        await Expect(Page.Locator("[data-testid=test-reply]")).ToContainTextAsync("3 x Croissant");

        Assert.That(GlobalSetup.Fakes.PosOrders.Count, Is.EqualTo(ordersBefore), "no POSFirst order");
        Assert.That(GlobalSetup.Fakes.LineCalls.Count, Is.EqualTo(lineBefore), "nothing sent on LINE");
    }

    [Test]
    public async Task T40_Staff_LINE_user_id_is_validated_and_alerts_are_pushed_to_it()
    {
        await Page.GotoAsync($"/Clients/Edit/{World.Toggle.Id}");
        await Page.FillAsync("#StaffAlertLineUserId", "my-line-name");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=error-StaffAlertLineUserId]")).ToContainTextAsync("U followed by 32");

        var staff = "U" + Guid.NewGuid().ToString("N");
        await Page.FillAsync("#StaffAlertLineUserId", staff);
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Client saved");

        // A refund request is handed to staff -> the alert is pushed to the staff LINE user.
        var customer = Line.NewUser("t40");
        await Line.SendAsync(World.Toggle, Line.TextEvent(customer, "I want a refund"));
        var alert = await Eventually.GetAsync(() => GlobalSetup.Fakes.LineCalls.Where(c => c.Kind == "push" && c.To == staff).ToList(),
            c => c.Count > 0, 15000, "staff LINE alert");
        Assert.That(alert[0].Text, Does.Contain(customer));
        Assert.That(alert[0].Authorization, Is.EqualTo("Bearer toggle-token"), "pushed through the client's own LINE account");
    }

    [Test]
    public async Task T41_Inbound_events_page_shows_why_a_message_was_skipped()
    {
        var user = Line.NewUser("t41");
        await Line.SendAsync(World.NoFlow, Line.TextEvent(user, "hello"));
        await Waits.SettledEventAsync(user);

        await Page.GotoAsync($"/InboundEvents?clientConfigId={World.NoFlow.Id}&status=Skipped");
        var row = Page.Locator("[data-testid=inbound-row]").Filter(new() { HasText = user });
        await Expect(row.Locator("[data-testid=inbound-status]")).ToHaveTextAsync("Skipped");
        await Expect(row.Locator("[data-testid=inbound-error]")).ToContainTextAsync("No enabled message workflow for LINE");
    }
}
