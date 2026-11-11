using System.Text.RegularExpressions;
using MadeeAutomation.Domain.Security;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Admin.Web in a real browser (Playwright, Chromium): clients, encrypted credentials, workflows,
/// execution log detail - and that what you set up in the UI actually drives the LINE pipeline.
/// </summary>
[TestFixture]
[NonParallelizable]
public class AdminTests : PageTest
{
    private static E2ESettings Settings => GlobalSetup.Settings;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = Settings.AdminUrl };

    /// <summary>Admin.Web needs a signed-in admin since Round 002.</summary>
    [SetUp]
    public async Task SignInAsync()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page.Locator("[data-testid=nav-logout]")).ToBeVisibleAsync();
    }

    // ---------------- helpers ----------------

    private async Task CreateClientAsync(string name, string code)
    {
        await Page.GotoAsync("/Clients/Create");
        await Page.FillAsync("#ClientName", name);
        await Page.FillAsync("#ClientCode", code);
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("created");
    }

    private async Task<Guid> ClientIdAsync(string code) =>
        (await GlobalSetup.Db.ScalarAsync<Guid>("SELECT Id FROM dbo.ClientConfigs WHERE ClientCode = @c", ("@c", code)));

    private async Task SaveLineCredentialsAsync(string code, string secret, string token)
    {
        await Page.GotoAsync($"/Clients/Edit/{await ClientIdAsync(code)}");
        await Page.FillAsync("#LineChannelSecret", secret);
        await Page.FillAsync("#LineChannelAccessToken", token);
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Client saved");
    }

    private async Task CreateLineWorkflowAsync(string code, string name, bool enabled = true)
    {
        await Page.GotoAsync($"/Clients/CreateWorkflow?clientConfigId={await ClientIdAsync(code)}");
        await Page.FillAsync("#Name", name);
        await Page.SelectOptionAsync("#HandlerKey", "customer-message");
        await Page.SelectOptionAsync("#Channel", "line");
        if (!enabled) await Page.UncheckAsync("#IsEnabled");
        await Page.ClickAsync("[data-testid=save-workflow]");
    }

    // ---------------- tests ----------------

    [Test]
    public async Task T21_New_client_shows_its_LINE_webhook_URL_and_missing_secret()
    {
        await CreateClientAsync("UI Bakery One", "UiBakery1");   // upper case is accepted and stored lower case

        await Expect(Page).ToHaveURLAsync(new Regex("/Clients/Edit/"));
        // Search for it: the list shows 10 clients per page, and the seeded clients alone fill page 1.
        await Page.GotoAsync("/Clients?search=uibakery1");
        var row = Page.Locator("[data-testid=client-row-uibakery1]");
        await Expect(row).ToBeVisibleAsync();

        await row.Locator("text=Manage").ClickAsync();
        await Expect(Page.Locator("[data-testid=line-webhook-url]"))
            .ToHaveTextAsync($"{Settings.WebApiUrl}/api/webhooks/line/uibakery1");
        await Expect(Page.Locator("[data-testid=line-secret-status]")).ToContainTextAsync("missing");
        await Expect(Page.Locator("[data-testid=line-token-status]")).ToContainTextAsync("mock");
    }

    [Test]
    public async Task T22_Duplicate_client_code_is_rejected()
    {
        await Page.GotoAsync("/Clients/Create");
        await Page.FillAsync("#ClientName", "Copy of bakery");
        await Page.FillAsync("#ClientCode", World.Bakery.Code);
        await Page.ClickAsync("[data-testid=save-client]");

        await Expect(Page.Locator("[data-testid=error-ClientCode]")).ToContainTextAsync("already uses this code");
        Assert.That(await GlobalSetup.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ClientConfigs WHERE ClientCode = @c", ("@c", World.Bakery.Code)),
            Is.EqualTo(1));
    }

    [Test]
    public async Task T23_Credentials_are_stored_encrypted_and_never_shown_again()
    {
        const string secret = "ui-secret-ABCD1234";
        const string token = "ui-token-WXYZ9876";
        await CreateClientAsync("UI Bakery Two", "uibakery2");
        await SaveLineCredentialsAsync("uibakery2", secret, token);

        await Expect(Page.Locator("[data-testid=line-secret-status]")).ToContainTextAsync("saved");
        await Expect(Page.Locator("[data-testid=line-token-status]")).ToContainTextAsync("saved");

        // Edit page: masked hint only, empty inputs, full values nowhere in the HTML.
        await Page.GotoAsync($"/Clients/Edit/{await ClientIdAsync("uibakery2")}");
        await Expect(Page.Locator("[data-testid=saved-LineChannelSecret]")).ToContainTextAsync("1234");
        await Expect(Page.Locator("#LineChannelSecret")).ToHaveValueAsync("");
        var html = await Page.ContentAsync();
        Assert.That(html, Does.Not.Contain(secret));
        Assert.That(html, Does.Not.Contain(token));

        // Database: encrypted with the configured key.
        var stored = await GlobalSetup.Db.ScalarAsync<string>("SELECT IntegrationSettingsJson FROM dbo.ClientConfigs WHERE ClientCode = 'uibakery2'");
        Assert.That(stored, Does.StartWith("enc:v1:"));
        Assert.That(stored, Does.Not.Contain(secret));
        Assert.That(SettingsCipher.Decrypt(stored!, SettingsCipher.ParseKey(E2ESettings.EncryptionKey)), Does.Contain(secret));

        // Saving with blank fields keeps them...
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=line-secret-status]")).ToContainTextAsync("saved");

        // ...and "Remove" clears one.
        await Page.GotoAsync($"/Clients/Edit/{await ClientIdAsync("uibakery2")}");
        await Page.CheckAsync("[data-testid=clear-LineChannelAccessToken]");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=line-token-status]")).ToContainTextAsync("No access token");
        await Expect(Page.Locator("[data-testid=line-secret-status]")).ToContainTextAsync("saved");
    }

    [Test]
    public async Task T24_Client_set_up_in_the_UI_answers_on_LINE()
    {
        await CreateClientAsync("UI Bakery Three", "uibakery3");
        await SaveLineCredentialsAsync("uibakery3", "ui3-secret", "ui3-token");
        await CreateLineWorkflowAsync("uibakery3", "LINE customer messages");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("created");

        var user = Line.NewUser("t24");
        var client = new SeededClient(await ClientIdAsync("uibakery3"), "uibakery3", "ui3-secret", null);
        var response = await Line.SendAsync(client, Line.TextEvent(user, "hello"));
        Assert.That(response.Int("received"), Is.EqualTo(1), response.Body);

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Authorization, Is.EqualTo("Bearer ui3-token"));
    }

    [Test]
    public async Task T25_Only_one_enabled_message_workflow_per_channel()
    {
        await CreateClientAsync("UI Bakery Four", "uibakery4");
        await CreateLineWorkflowAsync("uibakery4", "First LINE bot");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("created");

        await CreateLineWorkflowAsync("uibakery4", "Second LINE bot");
        await Expect(Page.Locator("[data-testid=error-Channel]")).ToContainTextAsync("already has an enabled message workflow");

        // Saving the second one DISABLED is fine...
        await Page.UncheckAsync("#IsEnabled");
        await Page.ClickAsync("[data-testid=save-workflow]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("created");

        // ...but enabling it while the first is on is refused.
        var second = Page.Locator("[data-testid=workflow-row]").Filter(new() { HasText = "Second LINE bot" });
        await second.Locator("[data-testid=toggle-workflow]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("already has an enabled message workflow");
        await Expect(second.Locator("[data-testid=workflow-enabled]")).ToHaveTextAsync("No");
    }

    [Test]
    public async Task T26_Invalid_trigger_config_JSON_is_rejected()
    {
        await Page.GotoAsync($"/Clients/CreateWorkflow?clientConfigId={World.Bakery.Id}");
        await Page.FillAsync("#Name", "Morning stock check");
        // Scheduled workflows use the "When to run" picker now (Phase 4); the JSON box is for webhook handlers.
        await Page.SelectOptionAsync("#HandlerKey", "lead-scoring");
        await Page.FillAsync("#TriggerConfigJson", "{ source: website");
        await Page.ClickAsync("[data-testid=save-workflow]");

        await Expect(Page.Locator("[data-testid=error-TriggerConfigJson]")).ToContainTextAsync("not valid JSON");
        Assert.That(await GlobalSetup.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkflowDefinitions WHERE Name = 'Morning stock check'"),
            Is.EqualTo(0));
    }

    [Test]
    public async Task T27_Execution_log_detail_shows_channel_customer_actions_and_payload()
    {
        var user = Line.NewUser("t27");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "2 croissants for tomorrow"));
        await Waits.FinishedLogAsync(user);

        await Page.GotoAsync($"/ExecutionLogs?clientConfigId={World.Bakery.Id}&search={user}");
        await Expect(Page.Locator("[data-testid=log-row]")).ToHaveCountAsync(1);
        await Page.Locator("[data-testid=log-link]").First.ClickAsync();

        await Expect(Page.Locator("[data-testid=log-status]")).ToHaveTextAsync("Succeeded");
        await Expect(Page.Locator("[data-testid=log-channel]")).ToHaveTextAsync("LINE");
        await Expect(Page.Locator("[data-testid=log-customer]")).ToContainTextAsync(user);
        await Expect(Page.Locator("[data-testid=log-summary]")).ToContainTextAsync("Order POS-");
        await Expect(Page.Locator("[data-testid=actions-table]")).ToContainTextAsync("SendMessage");
        await Expect(Page.Locator("[data-testid=log-payload]")).ToContainTextAsync("2 croissants for tomorrow");
    }

    [Test]
    public async Task T28_Disabling_the_workflow_in_the_UI_stops_replies()
    {
        await Page.GotoAsync($"/Clients/Details/{World.Toggle.Id}");
        var row = Page.Locator("[data-testid=workflow-row]").First;
        await row.Locator("[data-testid=toggle-workflow]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("disabled");

        var quiet = Line.NewUser("t28a");
        await Line.SendAsync(World.Toggle, Line.TextEvent(quiet, "hello"));
        var evt = await Waits.SettledEventAsync(quiet);
        Assert.That(evt["Status"], Is.EqualTo(5));
        Assert.That(GlobalSetup.Fakes.LineCallsFor(quiet), Is.Empty);

        await Page.Locator("[data-testid=workflow-row]").First.Locator("[data-testid=toggle-workflow]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("enabled");

        var answered = Line.NewUser("t28b");
        await Line.SendAsync(World.Toggle, Line.TextEvent(answered, "hello"));
        await Waits.LineCallsAsync(answered);
    }
}
