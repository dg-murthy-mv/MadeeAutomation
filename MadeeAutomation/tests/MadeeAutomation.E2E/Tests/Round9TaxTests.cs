using System.Text.Json;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 009: tax (GST / VAT) shown on orders. One tax per client, worked out by our code:
/// either already inside the menu prices, or added on top.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Round9TaxTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    /// <summary>Sends one thing to the client and returns the bot's next message to that customer.</summary>
    private static async Task<string> SendAsync(SeededClient client, string user, object lineEvent)
    {
        var before = GlobalSetup.Fakes.LineCallsFor(user).Count;
        await Line.SendAsync(client, lineEvent);
        var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(user)),
            c => c.Count > before, 15000, $"a reply to {user}");
        return calls[before].Text;
    }

    private static Task<string> SayAsync(SeededClient client, string user, string text) => SendAsync(client, user, Line.TextEvent(user, text));
    private static Task<string> TapAsync(SeededClient client, string user, string data) => SendAsync(client, user, Line.TapEvent(user, data));

    private static async Task<Dictionary<string, object?>> OrderAsync(string user, int index = 0) =>
        (await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c ORDER BY CreatedAtUtc", ("@c", user)),
            r => r.Count > index, 15000, $"saved order for {user}"))[index];

    private static async Task<object?> MenuItemIdAsync(SeededClient client, string name) =>
        (await Db.RowsAsync("SELECT Id FROM dbo.MenuItems WHERE ClientConfigId = @c AND Name = @n", ("@c", client.Id), ("@n", name)))[0]["Id"];

    private async Task SignInAsync()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    [Test]
    public async Task T84_Tax_included_in_prices_is_shown_on_the_order()
    {
        // Thailand, VAT 7% included: 2 x 55 = 110.00, of which 7.20 is VAT.
        var user = Line.NewUser("t84");
        var reply = await SayAsync(World.TaxIncluded, user, "2 tea");
        Assert.That(reply, Does.EndWith("Total: ฿110.00 (includes VAT 7%: ฿7.20)"));

        var order = await OrderAsync(user);
        Assert.That(order["Total"], Is.EqualTo(110m));
        Assert.That(order["Subtotal"], Is.EqualTo(102.80m));
        Assert.That(order["TaxAmount"], Is.EqualTo(7.20m));
        Assert.That(order["TaxName"], Is.EqualTo("VAT"));
        Assert.That(order["TaxRatePercent"], Is.EqualTo(7m));
        Assert.That(order["PricesIncludeTax"], Is.EqualTo(true));

        var staff = await Eventually.GetAsync(
            () => Task.FromResult(GlobalSetup.Fakes.LineCalls.Where(c => c.To == World.TaxStaffLineId && c.Text.Contains((string)order["Reference"]!)).ToList()),
            m => m.Count > 0, 15000, "staff message");
        Assert.That(staff[0].Text, Does.Contain("Total: ฿110.00 (includes VAT 7%: ฿7.20)"));

        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.System.Contains("E2E Tax Included")).System;
        Assert.That(prompt, Does.Contain("MENU prices already include VAT 7%."));

        // the same with the menu buttons
        var tea = await MenuItemIdAsync(World.TaxIncluded, "Thai tea");
        Assert.That(await TapAsync(World.TaxIncluded, user, $"a=add&i={tea}&q=2"), Is.EqualTo("Added 2 x Thai tea.\nCart: 2 item(s), ฿110.00"));
        Assert.That(await TapAsync(World.TaxIncluded, user, "a=cart"), Does.EndWith("Total: ฿110.00 (includes VAT 7%: ฿7.20)"));
        await TapAsync(World.TaxIncluded, user, "a=checkout");
        Assert.That(await TapAsync(World.TaxIncluded, user, "a=when&t=now"), Does.EndWith("Total: ฿110.00 (includes VAT 7%: ฿7.20)"));
        Assert.That((await OrderAsync(user, 1))["TaxAmount"], Is.EqualTo(7.20m));

        // Admin: the Menu page says what the prices include, the Orders page shows the tax
        await SignInAsync();
        await Page.GotoAsync($"/Menu?clientConfigId={World.TaxIncluded.Id}");
        await Expect(Page.Locator("[data-testid=menu-tax]")).ToHaveTextAsync("(including VAT 7%)");
        await Page.GotoAsync($"/Orders?clientConfigId={World.TaxIncluded.Id}&q={order["Reference"]}");
        await Expect(Page.Locator("[data-testid=order-total]")).ToHaveTextAsync("฿110.00");
        await Expect(Page.Locator("[data-testid=order-tax]")).ToHaveTextAsync("includes VAT 7%: ฿7.20");
    }

    [Test]
    public async Task T85_Tax_added_on_top_is_shown_and_sent_to_the_POS()
    {
        // India, GST 5% added on top: 2 x 25 = 50.00 + 2.50 = 52.50.
        var user = Line.NewUser("t85");
        var reply = await SayAsync(World.TaxAdded, user, "2 samosa");
        Assert.That(reply, Does.EndWith("Subtotal: ₹50.00\nGST 5%: ₹2.50\nTotal: ₹52.50"));

        var order = await OrderAsync(user);
        Assert.That(order["Subtotal"], Is.EqualTo(50m));
        Assert.That(order["TaxAmount"], Is.EqualTo(2.50m));
        Assert.That(order["Total"], Is.EqualTo(52.50m));
        Assert.That(order["PricesIncludeTax"], Is.EqualTo(false));

        var pos = JsonDocument.Parse(GlobalSetup.Fakes.OrdersFor(user).Single().BodyJson).RootElement;
        Assert.That(pos.GetProperty("orderTotal").GetDecimal(), Is.EqualTo(52.50m));
        Assert.That(pos.GetProperty("subtotal").GetDecimal(), Is.EqualTo(50m));
        Assert.That(pos.GetProperty("taxAmount").GetDecimal(), Is.EqualTo(2.50m));
        Assert.That(pos.GetProperty("taxName").GetString(), Is.EqualTo("GST"));
        Assert.That(pos.GetProperty("taxRatePercent").GetDecimal(), Is.EqualTo(5m));
        Assert.That(pos.GetProperty("pricesIncludeTax").GetBoolean(), Is.False);

        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.System.Contains("E2E Tax Added")).System;
        Assert.That(prompt, Does.Contain("MENU prices do NOT include GST 5%"));

        // the cart shows what the customer will pay
        var samosa = await MenuItemIdAsync(World.TaxAdded, "Samosa");
        Assert.That(await TapAsync(World.TaxAdded, user, $"a=add&i={samosa}&q=2"), Is.EqualTo("Added 2 x Samosa.\nCart: 2 item(s), ₹52.50 (with GST 5%)"));
        Assert.That(await TapAsync(World.TaxAdded, user, "a=cart"), Does.EndWith("2 x Samosa - ₹50.00\nSubtotal: ₹50.00\nGST 5%: ₹2.50\nTotal: ₹52.50"));
        await TapAsync(World.TaxAdded, user, "a=checkout");
        Assert.That(await TapAsync(World.TaxAdded, user, "a=when&t=now"), Does.EndWith("Subtotal: ₹50.00\nGST 5%: ₹2.50\nTotal: ₹52.50"));

        // the Orders page, and the message when staff accept
        await SignInAsync();
        var second = await OrderAsync(user, 1);
        await Page.GotoAsync($"/Orders?clientConfigId={World.TaxAdded.Id}&q={second["Reference"]}");
        await Expect(Page.Locator("[data-testid=order-total]")).ToHaveTextAsync("₹52.50");
        await Expect(Page.Locator("[data-testid=order-tax]")).ToHaveTextAsync("₹50.00 + GST 5% ₹2.50");

        var before = GlobalSetup.Fakes.LineCallsFor(user).Count;
        await Page.ClickAsync("[data-testid=order-accept]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("The customer was told on LINE.");
        var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(user)), c => c.Count > before, 15000, "accept message");
        Assert.That(calls[^1].Text, Does.Contain("Total: ₹52.50."));
    }

    [Test]
    public async Task T86_Tax_is_set_and_cleared_on_the_client_page()
    {
        var user = Line.NewUser("t86");
        Assert.That(await SayAsync(World.TaxForm, user, "2 samosa"), Does.EndWith("Total: ฿50.00"), "no tax set: a plain total");

        await SignInAsync();
        await Page.GotoAsync($"/Clients/Edit/{World.TaxForm.Id}");
        await Expect(Page.Locator("[data-testid=tax-included]")).ToBeCheckedAsync();

        // a name and a rate go together
        await Page.FillAsync("[data-testid=tax-name]", "VAT");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=tax-rate-error]")).ToContainTextAsync("Enter the tax rate");
        await Page.FillAsync("[data-testid=tax-name]", "");
        await Page.FillAsync("[data-testid=tax-rate]", "7");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=tax-name-error]")).ToContainTextAsync("Enter the tax name");

        // 2.5% added on top: 25.00 + 0.63 (0.625 rounded up) = 25.63
        await Page.FillAsync("[data-testid=tax-name]", "VAT");
        await Page.FillAsync("[data-testid=tax-rate]", "2.5");
        await Page.UncheckAsync("[data-testid=tax-included]");
        await Page.ClickAsync("[data-testid=save-client]");
        Assert.That(await SayAsync(World.TaxForm, user, "1 samosa"), Does.EndWith("Subtotal: ฿25.00\nVAT 2.5%: ฿0.63\nTotal: ฿25.63"));

        // clearing both removes the tax lines; the earlier order keeps the tax it was taken with
        await Page.GotoAsync($"/Clients/Edit/{World.TaxForm.Id}");
        await Expect(Page.Locator("[data-testid=tax-name]")).ToHaveValueAsync("VAT");
        await Page.FillAsync("[data-testid=tax-name]", "");
        await Page.FillAsync("[data-testid=tax-rate]", "");
        await Page.ClickAsync("[data-testid=save-client]");
        Assert.That(await SayAsync(World.TaxForm, user, "2 samosa"), Does.EndWith("Total: ฿50.00"));

        Assert.That((await OrderAsync(user, 1))["TaxAmount"], Is.EqualTo(0.63m));
        Assert.That((await OrderAsync(user, 2))["TaxAmount"], Is.Null);
    }
}
