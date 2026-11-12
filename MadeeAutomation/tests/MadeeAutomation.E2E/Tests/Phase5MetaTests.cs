using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Phase 5: WhatsApp and Messenger.
///   - Meta's webhook checks: the verify token, and X-Hub-Signature-256 on every call
///   - the menu buttons on WhatsApp (reply buttons + lists) and Messenger (quick replies + cards)
///   - the customer's name: from the WhatsApp message, looked up on Messenger
///   - safety: another WhatsApp number, re-delivered messages, and WhatsApp refusing buttons
/// Meta's servers are played by the fake server (Connectors:Meta:GraphBaseUrl).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Phase5MetaTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;
    private static FakeServer Fakes => GlobalSetup.Fakes;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    /// <summary>Sends a WhatsApp message and returns what we sent back (at least `count` calls).</summary>
    private static async Task<List<MetaCall>> WhatsAppAsync(string from, object message, int count = 1, string name = "Maria Cruz",
        string phoneNumberId = World.MetaPhoneNumberId)
    {
        var before = Fakes.MetaCallsTo(from).Count;
        var r = await Meta.PostAsync($"/api/webhooks/whatsapp/{World.MetaCafe.Code}", Meta.WhatsAppBody(from, name, phoneNumberId, message), World.MetaSecret);
        Assert.That(r.Status, Is.EqualTo(HttpStatusCode.OK), r.Body);
        var calls = await Eventually.GetAsync(() => Fakes.MetaCallsTo(from), c => c.Count >= before + count, 15000, $"WhatsApp reply to {from}");
        await Task.Delay(500);   // let any second part arrive
        return Fakes.MetaCallsTo(from).Skip(before).ToList();
    }

    private static async Task<List<MetaCall>> MessengerAsync(string psid, object evt, int count = 1)
    {
        var before = Fakes.MetaCallsTo(psid).Count;
        var r = await Meta.PostAsync($"/api/webhooks/messenger/{World.MetaCafe.Code}", Meta.MessengerBody(psid, evt), World.MetaSecret);
        Assert.That(r.Status, Is.EqualTo(HttpStatusCode.OK), r.Body);
        var calls = await Eventually.GetAsync(() => Fakes.MetaCallsTo(psid), c => c.Count >= before + count, 15000, $"Messenger reply to {psid}");
        await Task.Delay(500);
        return Fakes.MetaCallsTo(psid).Skip(before).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> OrdersOfAsync(string customer, int count = 1) =>
        await Eventually.GetAsync(() => Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c", ("@c", customer)),
            r => r.Count >= count, 15000, $"order from {customer}");

    private static string Pick(List<(string Title, string Id, string? Description)> choices, string title)
    {
        var match = choices.Where(c => c.Title == title).ToList();
        Assert.That(match, Is.Not.Empty, $"no '{title}' in: {string.Join(", ", choices.Select(c => c.Title))}");
        return match[0].Id;
    }

    [Test]
    public async Task T93_Meta_verify_token_and_signature_are_checked()
    {
        var ok = await Meta.GetAsync($"/api/webhooks/whatsapp/metacafe?hub.mode=subscribe&hub.verify_token={World.MetaVerifyToken}&hub.challenge=4242");
        Assert.That(ok.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ok.Body, Is.EqualTo("4242"));
        var fb = await Meta.GetAsync($"/api/webhooks/messenger/metacafe?hub.mode=subscribe&hub.verify_token={World.MetaVerifyToken}&hub.challenge=77");
        Assert.That(fb.Body, Is.EqualTo("77"));
        var wrong = await Meta.GetAsync("/api/webhooks/whatsapp/metacafe?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1");
        Assert.That(wrong.Status, Is.EqualTo(HttpStatusCode.Forbidden));

        const string empty = "{\"object\":\"whatsapp_business_account\",\"entry\":[]}";
        Assert.That((await Meta.PostAsync("/api/webhooks/whatsapp/metacafe", empty, null)).Status, Is.EqualTo(HttpStatusCode.Unauthorized), "no signature");
        Assert.That((await Meta.PostAsync("/api/webhooks/whatsapp/metacafe", empty, null, "sha256=" + new string('0', 64))).Status,
            Is.EqualTo(HttpStatusCode.Unauthorized), "wrong signature");
        Assert.That((await Meta.PostAsync("/api/webhooks/messenger/metacafe", "{\"entry\":[]}", "another-secret")).Status,
            Is.EqualTo(HttpStatusCode.Unauthorized), "signed with another app's secret");

        var noSecret = await Meta.PostAsync("/api/webhooks/whatsapp/metanosecret", empty, "anything");
        Assert.That(noSecret.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(noSecret.Body, Does.Contain("App Secret is not configured"));

        Assert.That((await Meta.PostAsync("/api/webhooks/whatsapp/metacafe", empty, World.MetaSecret)).Status, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T94_WhatsApp_order_with_buttons_and_lists()
    {
        var from = Meta.NewWhatsAppNumber();

        // Greeting -> the categories as reply buttons (3 or fewer) - no time-picker on WhatsApp.
        var reply = await WhatsAppAsync(from, Meta.WaText("hello"));
        var choices = Meta.WaChoices(reply);
        Assert.That(choices.Select(c => c.Title), Does.Contain("Drinks"));
        Assert.That(reply[0].Type, Is.EqualTo("button"));
        var ev = (await Db.RowsAsync("SELECT CustomerName, Channel FROM dbo.InboundEvents WHERE CustomerId = @c", ("@c", from)))[0];
        Assert.That(ev["CustomerName"], Is.EqualTo("Maria Cruz"));

        // A category -> a list of items (one row each), then the other buttons.
        reply = await WhatsAppAsync(from, Meta.WaTap(Pick(choices, "Drinks")), count: 2);
        Assert.That(reply[0].Type, Is.EqualTo("list"));
        var rows = Meta.WaChoices(reply.Take(1));
        var coffee = rows.Single(r => r.Title == "Iced coffee");
        Assert.That(coffee.Description, Does.Contain("120.00"));
        Assert.That(coffee.Id, Does.StartWith("a=add"));
        var juice = rows.Single(r => r.Title.StartsWith("Calamansi"));
        Assert.That(juice.Title.Length, Is.LessThan(25), "WhatsApp row titles are max 24 characters");
        Assert.That(juice.Description, Does.Contain("Calamansi juice with honey and mint"), "full name in the description");

        // Pick the row -> added; checkout -> asked to type a time.
        reply = await WhatsAppAsync(from, Meta.WaTap(coffee.Id, list: true));
        Assert.That(reply[0].Text, Does.StartWith("Added 1 x Iced coffee."));
        reply = await WhatsAppAsync(from, Meta.WaTap("a=checkout"));
        Assert.That(reply[0].Text, Does.Contain("Or type a time"));
        Assert.That(Meta.WaChoices(reply).Select(c => c.Title), Does.Contain("As soon as possible").And.Not.Contain("Choose a time"));

        // Typed answer -> order placed, with the WhatsApp name.
        reply = await WhatsAppAsync(from, Meta.WaText("now please"));
        var order = (await OrdersOfAsync(from))[0];
        Assert.That(order["Channel"], Is.EqualTo("whatsapp"));
        Assert.That(order["CustomerName"], Is.EqualTo("Maria Cruz"));
        Assert.That((string)order["Reference"]!, Does.StartWith("ORD-"));
        Assert.That(reply[0].Text, Does.Contain((string)order["Reference"]!));

        // Bakery: the sold-out item is in the words, not a row.
        var other = Meta.NewWhatsAppNumber();
        var home = await WhatsAppAsync(other, Meta.WaText("hello"), name: "Jose");
        reply = await WhatsAppAsync(other, Meta.WaTap(Pick(Meta.WaChoices(home), "Bakery")), count: 2, name: "Jose");
        Assert.That(reply[0].Text, Does.Contain("Pandesal"));
        Assert.That(Meta.WaChoices(reply).Select(c => c.Title), Does.Not.Contain("Pandesal"));
    }

    [Test]
    public async Task T95_WhatsApp_safety_other_number_duplicates_and_refused_buttons()
    {
        // A message to a different WhatsApp business number is ignored.
        var stranger = Meta.NewWhatsAppNumber();
        var r = await Meta.PostAsync("/api/webhooks/whatsapp/metacafe",
            Meta.WhatsAppBody(stranger, "X", "999999999999999", Meta.WaText("hello")), World.MetaSecret);
        Assert.That(r.Status, Is.EqualTo(HttpStatusCode.OK));
        await Task.Delay(2500);
        Assert.That(Fakes.MetaCallsTo(stranger), Is.Empty);
        Assert.That(await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InboundEvents WHERE CustomerId = @c", ("@c", stranger)), Is.EqualTo(0));

        // Meta re-delivers the same message -> stored once.
        var from = Meta.NewWhatsAppNumber();
        var body = Meta.WhatsAppBody(from, "Dup", World.MetaPhoneNumberId, Meta.WaText("hello"), messageId: "wamid.e2e-dup-" + Guid.NewGuid().ToString("N"));
        await Meta.PostAsync("/api/webhooks/whatsapp/metacafe", body, World.MetaSecret);
        var again = await Meta.PostAsync("/api/webhooks/whatsapp/metacafe", body, World.MetaSecret);
        Assert.That(again.Int("duplicates"), Is.EqualTo(1));

        // WhatsApp refuses the buttons (400) -> the customer still gets the words.
        var fussy = Meta.NewWhatsAppNumber("66400");
        var calls = await WhatsAppAsync(fussy, Meta.WaText("hello"), count: 2, name: "Fussy");
        Assert.That(calls[0].StatusReturned, Is.EqualTo(400));
        Assert.That(calls[1].Type, Is.EqualTo("text"));
        Assert.That(calls[1].StatusReturned, Is.EqualTo(200));
    }

    [Test]
    public async Task T96_Messenger_order_with_quick_replies_and_cards()
    {
        var psid = "PSID" + Guid.NewGuid().ToString("N")[..12];

        var reply = await MessengerAsync(psid, Meta.FbText("hello"));
        var quick = Meta.FbQuickReplies(reply);
        Assert.That(quick.Select(q => q.Title), Does.Contain("Drinks"));
        Assert.That(JsonDocument.Parse(reply[0].Body).RootElement.GetProperty("messaging_type").GetString(), Is.EqualTo("RESPONSE"));

        reply = await MessengerAsync(psid, Meta.FbQuickReply(quick.First(q => q.Title == "Drinks").Payload), count: 2);
        Assert.That(reply.Select(c => c.Type), Is.EqualTo(new[] { "text", "template" }));
        var coffee = Meta.FbCards(reply).Single(c => c.Title == "Iced coffee");
        Assert.That(coffee.Payloads[0], Does.StartWith("a=add"));
        Assert.That(Meta.FbQuickReplies(reply.Skip(1)), Is.Not.Empty, "quick replies go on the last message");

        reply = await MessengerAsync(psid, Meta.FbPostback(coffee.Payloads[0]));
        Assert.That(reply[0].Text, Does.StartWith("Added 1 x Iced coffee."));
        reply = await MessengerAsync(psid, Meta.FbPostback("a=checkout"));
        Assert.That(reply[0].Text, Does.Contain("Or type a time"));
        await MessengerAsync(psid, Meta.FbQuickReply("a=when&t=now"));

        var order = (await OrdersOfAsync(psid))[0];
        Assert.That(order["Channel"], Is.EqualTo("messenger"));
        Assert.That(order["CustomerName"], Is.EqualTo("Maria Santos"), "looked up from Messenger");

        // The Page's own messages come back as echoes - ignored.
        var events = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InboundEvents WHERE CustomerId = @c", ("@c", psid));
        await Meta.PostAsync("/api/webhooks/messenger/metacafe",
            Meta.MessengerBody(psid, new { message = new { mid = "m_echo_" + Guid.NewGuid().ToString("N"), text = "ours", is_echo = true } }), World.MetaSecret);
        await Task.Delay(1500);
        Assert.That(await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InboundEvents WHERE CustomerId = @c", ("@c", psid)), Is.EqualTo(events));
    }

    [Test]
    public async Task T97_Admin_Meta_settings_and_credential_checks()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));

        await Page.GotoAsync($"/Clients/Details/{World.MetaCafe.Id}");
        await Expect(Page.Locator("[data-testid=whatsapp-status]")).ToHaveTextAsync("Credentials saved");
        await Expect(Page.Locator("[data-testid=whatsapp-secret-status]")).ToHaveTextAsync("App secret saved");
        await Expect(Page.Locator("[data-testid=messenger-status]")).ToHaveTextAsync("Credentials saved");

        await Page.ClickAsync("[data-testid=check-whatsapp]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("+66 80 000 0000");
        await Page.ClickAsync("[data-testid=check-messenger]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("E2E Page");

        await Page.GotoAsync($"/Clients/Details/{World.MetaNoSecret.Id}");
        await Expect(Page.Locator("[data-testid=whatsapp-secret-status]")).ToContainTextAsync("No app secret here");

        // The Edit page never shows a saved secret - only a masked hint.
        await Page.GotoAsync($"/Clients/Edit/{World.MetaCafe.Id}");
        await Expect(Page.Locator("#MetaAppSecret")).ToHaveAttributeAsync("placeholder", "Saved ••••cret - leave blank to keep");
        await Expect(Page.Locator("#MetaAppSecret")).ToHaveValueAsync("");

        // A bad token is explained.
        await Page.FillAsync("#MessengerPageAccessToken", "bad-token-e2e");
        await Page.ClickAsync("[data-testid=save-client]");
        await Page.GotoAsync($"/Clients/Details/{World.MetaCafe.Id}");
        await Page.ClickAsync("[data-testid=check-messenger]");
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("Meta rejected the Messenger token");

        // A Page token without pages_read_engagement: explained as fine for messages, not as an error.
        await Page.GotoAsync($"/Clients/Edit/{World.MetaCafe.Id}");
        await Page.FillAsync("#MessengerPageAccessToken", "noread-token-e2e");
        await Page.ClickAsync("[data-testid=save-client]");
        await Page.GotoAsync($"/Clients/Details/{World.MetaCafe.Id}");
        await Page.ClickAsync("[data-testid=check-messenger]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("messages will work");

        // put the good token back for any later run
        await Page.GotoAsync($"/Clients/Edit/{World.MetaCafe.Id}");
        await Page.FillAsync("#MessengerPageAccessToken", "fb-token-e2e");
        await Page.ClickAsync("[data-testid=save-client]");
    }
}
