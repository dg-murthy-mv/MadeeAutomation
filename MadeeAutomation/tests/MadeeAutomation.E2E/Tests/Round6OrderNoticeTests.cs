using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 006: when staff accept or cancel an order, the customer is told on the channel they
/// ordered on, in their own language - and the order shows whether that worked.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Round6OrderNoticeTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public async Task SignInAsync()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    /// <summary>Places an order by LINE and returns (customer id, saved order row).</summary>
    private static async Task<(string user, Dictionary<string, object?> order)> OrderAsync(SeededClient client, string text, string label)
    {
        var user = Line.NewUser(label);
        await Line.SendAsync(client, Line.TextEvent(user, text));
        await Waits.FinishedLogAsync(user);
        var rows = await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c", ("@c", user)),
            r => r.Count > 0, 15000, $"saved order for {user}");
        return (user, rows[0]);
    }

    private static async Task<Dictionary<string, object?>> ReloadAsync(Dictionary<string, object?> order) =>
        (await Db.RowsAsync("SELECT * FROM dbo.Orders WHERE Id = @i", ("@i", order["Id"])))[0];

    /// <summary>The card of one order. References repeat across clients, so the customer id picks the client's page.</summary>
    private async Task<ILocator> OpenCardAsync(SeededClient client, Dictionary<string, object?> order)
    {
        await Page.GotoAsync($"/Orders?clientConfigId={client.Id}&show=all");
        return Page.Locator($"[data-testid=order-card][data-order-ref='{order["Reference"]}']");
    }

    [Test]
    public async Task T73_Accepting_an_order_tells_the_customer()
    {
        var (user, order) = await OrderAsync(World.CakeShop, "2 tea tomorrow 9am", "t73");
        var reference = (string)order["Reference"]!;
        Assert.That(order["Language"], Is.EqualTo("en"));

        var card = await OpenCardAsync(World.CakeShop, order);
        await card.Locator("[data-testid=order-accept]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"{reference} accepted. The customer was told on LINE.");

        var calls = await Waits.LineCallsAsync(user, 2);
        Assert.That(calls[1].Kind, Is.EqualTo("push"));
        Assert.That(calls[1].Text, Does.StartWith($"Your order {reference} (2 x Thai tea) for "));
        Assert.That(calls[1].Text, Does.EndWith("09:00 is confirmed by E2E Cake Shop. Total: ฿110.00. Thank you!"));

        await Expect(card.Locator("[data-testid=order-notice]")).ToContainTextAsync("Customer told:");
        Assert.That((await ReloadAsync(order))["CustomerNoticeSent"], Is.EqualTo(true));

        // Completed: nothing more is sent
        await card.Locator("[data-testid=order-complete]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToHaveTextAsync($"Order {reference} completed.");
        await Waits.QuietPeriodAsync();
        Assert.That(GlobalSetup.Fakes.LineCallsFor(user), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task T74_Cancelling_tells_the_customer_in_their_language_with_the_reason()
    {
        var (thai, order) = await OrderAsync(World.CakeShop, "2 samosa ครับ", "t74a");
        var reference = (string)order["Reference"]!;
        Assert.That(order["Language"], Is.EqualTo("th"), "the AI reported Thai");

        var card = await OpenCardAsync(World.CakeShop, order);
        await card.Locator("[data-testid=order-cancel-reason-input]").FillAsync("The oven is broken today");
        await card.Locator("[data-testid=order-cancel]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"{reference} cancelled. The customer was told on LINE.");

        // The fake AI answers "[language] same text": Thai was asked for, and our facts are unchanged.
        var calls = await Waits.LineCallsAsync(thai, 2);
        Assert.That(calls[1].Text, Is.EqualTo(
            $"[th] Sorry, E2E Cake Shop could not take your order {reference} (2 x Samosa). Reason: The oven is broken today. Please message us if you would like to change it."));

        await Expect(card.Locator("[data-testid=order-cancel-reason]")).ToHaveTextAsync("Reason: The oven is broken today");
        var saved = await ReloadAsync(order);
        Assert.That(saved["Status"], Is.EqualTo(4), "Cancelled");
        Assert.That(saved["CancelReason"], Is.EqualTo("The oven is broken today"));

        // When the translation is unusable, the English message is sent instead of nothing.
        var (second, order2) = await OrderAsync(World.CakeShop, "1 samosa ค่ะ", "t74b");
        var card2 = await OpenCardAsync(World.CakeShop, order2);
        await card2.Locator("[data-testid=order-cancel-reason-input]").FillAsync("notranslate");
        await card2.Locator("[data-testid=order-cancel]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("The customer was told on LINE.");
        var calls2 = await Waits.LineCallsAsync(second, 2);
        Assert.That(calls2[1].Text, Does.StartWith($"Sorry, E2E Cake Shop could not take your order {order2["Reference"]}"));
    }

    [Test]
    public async Task T75_Customer_messages_can_be_switched_off_for_a_client()
    {
        await Page.GotoAsync($"/Clients/Edit/{World.HoursEdit.Id}");
        await Expect(Page.Locator("[data-testid=notify-customer]")).ToBeCheckedAsync();   // on by default
        await Page.UncheckAsync("[data-testid=notify-customer]");
        await Page.ClickAsync("[data-testid=save-client]");

        var (user, order) = await OrderAsync(World.HoursEdit, "1 croissant", "t75");
        var card = await OpenCardAsync(World.HoursEdit, order);
        await Expect(Page.Locator("[data-testid=orders-notice-off]")).ToBeVisibleAsync();
        await card.Locator("[data-testid=order-accept]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("switched off, so please tell the customer yourself");

        await Waits.QuietPeriodAsync();
        Assert.That(GlobalSetup.Fakes.LineCallsFor(user), Has.Count.EqualTo(1), "only the bot's first reply - no notice");
        Assert.That((await ReloadAsync(order))["CustomerNoticeSent"], Is.Null);
        await Expect(card.Locator("[data-testid=order-notice]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task T76_When_the_customer_cannot_be_told_staff_are_warned()
    {
        // This client's LINE token always fails, so nothing can be delivered.
        var (_, order) = await OrderAsync(World.FailSend, "1 croissant", "t76");
        var card = await OpenCardAsync(World.FailSend, order);
        await card.Locator("[data-testid=order-accept]").ClickAsync();

        await Expect(Page.Locator("[data-testid=alert-success]")).ToHaveTextAsync($"Order {order["Reference"]} accepted.");
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("could NOT be told automatically");
        await Expect(card.Locator("[data-testid=order-notice]")).ToContainTextAsync("Customer NOT told:");

        var saved = await ReloadAsync(order);
        Assert.That(saved["Status"], Is.EqualTo(2), "the order is still accepted");
        Assert.That(saved["CustomerNoticeSent"], Is.EqualTo(false));
    }
}
