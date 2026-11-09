using System.Text.Json;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Round 003: time zones and currencies across Thailand, India, Philippines and UAE.</summary>
[TestFixture]
[NonParallelizable]
public class Round3MarketTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private static JsonElement OrderBody(string customerId) =>
        JsonDocument.Parse(GlobalSetup.Fakes.OrdersFor(customerId).Single().BodyJson).RootElement;

    private static string LocalDate(string timeZoneId, int addDays = 0) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).AddDays(addDays).ToString("yyyy-MM-dd");

    [Test]
    public async Task T46_AI_gets_the_business_clock_and_currency()
    {
        var user = Line.NewUser("t46");
        await Line.SendAsync(World.IndiaCafe, Line.TextEvent(user, "hello"));
        await Waits.LineCallsAsync(user);

        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.UserMessage == "hello" && c.System.Contains("E2E India Cafe")).System;
        Assert.That(prompt, Does.Contain("time zone Asia/Kolkata, UTC+05:30"));
        Assert.That(prompt, Does.Contain("Local date: " + LocalDate("Asia/Kolkata")));
        Assert.That(prompt, Does.Contain("All prices are in INR"));
    }

    [Test]
    public async Task T47_Order_total_comes_from_the_menu_in_the_clients_currency()
    {
        var user = Line.NewUser("t47");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "2 croissants please"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.EndWith("Total: ฿90.00"));

        var order = OrderBody(user);
        Assert.That(order.GetProperty("currency").GetString(), Is.EqualTo("THB"));
        Assert.That(order.GetProperty("orderTotal").GetDecimal(), Is.EqualTo(90m));
        var line = order.GetProperty("items")[0];
        Assert.That(line.GetProperty("unitPrice").GetDecimal(), Is.EqualTo(45m));
        Assert.That(line.GetProperty("onMenu").GetBoolean(), Is.True);

        var log = await Waits.FinishedLogAsync(user);
        Assert.That((string)log["ResultSummary"]!, Does.Contain("total ฿90.00"));
    }

    [Test]
    public async Task T48_India_uses_rupees_with_lakh_grouping_and_IST_delivery_time()
    {
        var user = Line.NewUser("t48");
        await Line.SendAsync(World.IndiaCafe, Line.TextEvent(user, "1 catering tomorrow 9am"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.EndWith("Total: ₹1,25,000.00"));

        var order = OrderBody(user);
        var tomorrow = LocalDate("Asia/Kolkata", 1);
        Assert.That(order.GetProperty("currency").GetString(), Is.EqualTo("INR"));
        Assert.That(order.GetProperty("requestedDeliveryLocal").GetString(), Is.EqualTo($"{tomorrow}T09:00:00+05:30"));
        Assert.That(order.GetProperty("requestedDeliveryUtc").GetString(), Is.EqualTo($"{tomorrow}T03:30:00Z"));
        Assert.That(order.GetProperty("timeZone").GetString(), Is.EqualTo("Asia/Kolkata"));
    }

    [Test]
    public async Task T49_Thailand_delivery_time_is_converted_with_UTC_plus_7()
    {
        var user = Line.NewUser("t49");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "3 croissants tomorrow 9am"));
        await Waits.LineCallsAsync(user);

        var order = OrderBody(user);
        var tomorrow = LocalDate("Asia/Bangkok", 1);
        Assert.That(order.GetProperty("requestedDeliveryLocal").GetString(), Is.EqualTo($"{tomorrow}T09:00:00+07:00"));
        Assert.That(order.GetProperty("requestedDeliveryUtc").GetString(), Is.EqualTo($"{tomorrow}T02:00:00Z"));
        Assert.That(order.GetProperty("requestedDeliveryText").GetString(), Is.EqualTo("tomorrow 9am"));
    }

    [Test]
    public async Task T50_Choosing_a_country_fills_its_currency_and_time_zone()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync("/Clients/Create");

        foreach (var (country, currency, tz) in new[]
                 {
                     ("IN", "INR", "Asia/Kolkata"), ("PH", "PHP", "Asia/Manila"), ("AE", "AED", "Asia/Dubai"), ("TH", "THB", "Asia/Bangkok")
                 })
        {
            await Page.SelectOptionAsync("#Country", country);
            await Expect(Page.Locator("#Currency")).ToHaveValueAsync(currency);
            await Expect(Page.Locator("#TimeZoneId")).ToHaveValueAsync(tz);
        }

        await Page.SelectOptionAsync("#Country", "AE");
        await Page.FillAsync("#ClientName", "Dubai Dates Co");
        await Page.FillAsync("#ClientCode", "dubaidates");
        await Page.ClickAsync("[data-testid=save-client]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("created");

        var row = await GlobalSetup.Db.RowsAsync("SELECT Country, Currency, TimeZoneId FROM dbo.ClientConfigs WHERE ClientCode = 'dubaidates'");
        Assert.That(row[0]["Currency"], Is.EqualTo("AED"));
        Assert.That(row[0]["TimeZoneId"], Is.EqualTo("Asia/Dubai"));
    }

    [Test]
    public async Task T51_Admin_pages_use_the_admins_own_time_zone()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync("/Account/MyAccount");
        await Page.SelectOptionAsync("#TimeZoneId", "Asia/Kolkata");
        await Page.ClickAsync("[data-testid=save-my-account]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("saved");

        await Page.GotoAsync("/AdminUsers");
        await Expect(Page.Locator("[data-testid=admins-tz]")).ToHaveTextAsync("Asia/Kolkata");

        // Put it back for the other tests.
        await Page.GotoAsync("/Account/MyAccount");
        await Page.SelectOptionAsync("#TimeZoneId", "Asia/Bangkok");
        await Page.ClickAsync("[data-testid=save-my-account]");
    }

    [Test]
    public async Task T52_Unpriced_items_create_the_order_without_a_made_up_total()
    {
        // "Catering package" is not on the bakery's menu: the order still goes through, but no total is invented.
        var user = Line.NewUser("t52");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "1 catering please"));   // not on the bakery menu
        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.Not.Contain("Total"));

        var order = OrderBody(user);
        Assert.That(order.GetProperty("orderTotal").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(order.GetProperty("items")[0].GetProperty("onMenu").GetBoolean(), Is.False);
    }

    [Test]
    public async Task T53_Secret_fields_are_always_masked()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Clients/Edit/{World.Bakery.Id}");
        await Page.FillAsync("#LineChannelSecret", "not-shown-1234");

        // Chrome/Safari: dots via CSS on a text box. Browsers without that feature: a password box.
        var masked = await Page.EvaluateAsync<bool>(
            "() => { const e = document.getElementById('LineChannelSecret'); " +
            "return e.type === 'password' || getComputedStyle(e).webkitTextSecurity === 'disc'; }");
        Assert.That(masked, Is.True);
    }
}
