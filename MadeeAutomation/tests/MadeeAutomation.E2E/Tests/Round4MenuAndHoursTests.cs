using System.Text.Json;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Round 004: structured menu (aliases, sold out) and opening hours with "open now" per time zone.</summary>
[TestFixture]
[NonParallelizable]
public class Round4MenuAndHoursTests : PageTest
{
    private static readonly Regex OpenStatus = new(@"(OPEN now \(closes at \d\d:\d\d\)|CLOSED now \(opens again .+\)|Closed today \(opens again .+\))\.");

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public async Task SignInAsync()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync(); // the Delete confirm
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    private static string SpicePrompt(string message) =>
        GlobalSetup.Fakes.ClaudeCalls.Last(c => c.UserMessage == message && c.System.Contains("E2E Spice Cafe")).System;

    private ILocator Row(string name) => Page.Locator($"[data-testid=menu-row][data-item-name='{name}']");

    [Test]
    public async Task T56_AI_gets_the_structured_menu_hours_and_open_now_status()
    {
        var user = Line.NewUser("t56");
        await Line.SendAsync(World.SpiceCafe, Line.TextEvent(user, "hello there"));
        await Waits.LineCallsAsync(user);

        var prompt = SpicePrompt("hello there");
        Assert.That(prompt, Does.Contain("OPENING HOURS"));
        Assert.That(prompt, Does.Contain("Mon 07:00-14:00 and 16:00-22:00"));
        Assert.That(prompt, Does.Contain("Sun closed"));
        Assert.That(prompt, Does.Contain("Diwali"));
        Assert.That(prompt, Does.Match("RIGHT NOW: " + OpenStatus));
        Assert.That(prompt, Does.Contain("[Drinks]"));
        Assert.That(prompt, Does.Contain("- Masala chai: ₹60.00 (customers may also say: chai, tea)"));
        Assert.That(prompt, Does.Contain("SOLD OUT today"));
        Assert.That(prompt, Does.Contain("Paneer roll"));
    }

    [Test]
    public async Task T57_Bot_answers_are_you_open_from_the_structured_hours()
    {
        var user = Line.NewUser("t57");
        await Line.SendAsync(World.SpiceCafe, Line.TextEvent(user, "are you open now?"));
        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.Match(OpenStatus.ToString()));
    }

    [Test]
    public async Task T58_Aliases_match_menu_items_and_the_total_is_in_rupees()
    {
        var user = Line.NewUser("t58");
        await Line.SendAsync(World.SpiceCafe, Line.TextEvent(user, "2 tea and 3 samosa"));
        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.EndWith("Total: ₹195.00"));

        var order = JsonDocument.Parse(GlobalSetup.Fakes.OrdersFor(user).Single().BodyJson).RootElement;
        var items = order.GetProperty("items");
        Assert.That(items[0].GetProperty("name").GetString(), Is.EqualTo("Masala chai"), "\"tea\" is an alias of Masala chai");
        Assert.That(items[0].GetProperty("lineTotal").GetDecimal(), Is.EqualTo(120m));
        Assert.That(order.GetProperty("orderTotal").GetDecimal(), Is.EqualTo(195m));
    }

    [Test]
    public async Task T59_Sold_out_items_are_never_ordered()
    {
        var onlySoldOut = Line.NewUser("t59a");
        await Line.SendAsync(World.SpiceCafe, Line.TextEvent(onlySoldOut, "1 paneer roll"));
        var calls = await Waits.LineCallsAsync(onlySoldOut);
        Assert.That(calls[0].Text, Is.EqualTo("Sorry, Paneer roll is not available today."));
        await Waits.FinishedLogAsync(onlySoldOut);
        Assert.That(GlobalSetup.Fakes.OrdersFor(onlySoldOut), Is.Empty);

        var mixed = Line.NewUser("t59b");
        await Line.SendAsync(World.SpiceCafe, Line.TextEvent(mixed, "1 paneer roll and 2 samosa"));
        calls = await Waits.LineCallsAsync(mixed);
        Assert.That(calls[0].Text, Does.Contain("(Not included - not available today: Paneer roll)"));
        Assert.That(calls[0].Text, Does.EndWith("Total: ₹50.00"));
        var items = JsonDocument.Parse(GlobalSetup.Fakes.OrdersFor(mixed).Single().BodyJson).RootElement.GetProperty("items");
        Assert.That(items.GetArrayLength(), Is.EqualTo(1));
        Assert.That(items[0].GetProperty("name").GetString(), Is.EqualTo("Samosa"));
    }

    [Test]
    public async Task T60_Client_page_shows_menu_count_and_open_now()
    {
        await Page.GotoAsync($"/Clients/Details/{World.SpiceCafe.Id}");
        await Expect(Page.Locator("[data-testid=menu-count]")).ToHaveTextAsync("3 item(s)");
        await Expect(Page.Locator("[data-testid=hours-status]")).ToHaveTextAsync(OpenStatus);
        await Page.ClickAsync("[data-testid=open-menu]");
        await Expect(Page.Locator("[data-testid=menu-row]")).ToHaveCountAsync(3);
        await Expect(Row("Paneer roll").Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Sold out");
        await Expect(Row("Masala chai").Locator("[data-testid=menu-item-price]")).ToHaveTextAsync("₹60.00");
    }

    [Test]
    public async Task T61_Menu_import_add_edit_sold_out_and_delete()
    {
        await Page.GotoAsync($"/Menu?clientConfigId={World.MenuEdit.Id}");

        // import the old text menu
        await Expect(Page.Locator("[data-testid=import-banner]")).ToBeVisibleAsync();
        await Page.ClickAsync("[data-testid=import-menu]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Imported 3 item(s)");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("1 had no price");
        await Expect(Page.Locator("[data-testid=import-banner]")).ToHaveCountAsync(0);
        await Expect(Row("Sourdough loaf").Locator("[data-testid=menu-item-price]")).ToHaveTextAsync("฿180.00");
        await Expect(Row("Birthday cake").Locator("[data-testid=menu-item-price]")).ToHaveTextAsync("No price");

        // a duplicate name is refused
        await Page.FillAsync("[data-testid=item-name]", "croissant");
        await Page.FillAsync("[data-testid=item-price]", "50");
        await Page.ClickAsync("[data-testid=add-item]");
        await Expect(Page.Locator("[data-testid=item-name-error]")).ToContainTextAsync("already");

        // add with aliases (duplicates removed)
        await Page.FillAsync("[data-testid=item-name]", "Thai tea");
        await Page.FillAsync("[data-testid=item-price]", "55");
        await Page.FillAsync("[data-testid=item-category]", "Drinks");
        await Page.FillAsync("[data-testid=item-aliases]", "cha yen, ชาเย็น, cha yen");
        await Page.ClickAsync("[data-testid=add-item]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("added");
        await Expect(Row("Thai tea")).ToContainTextAsync("cha yen, ชาเย็น");

        // edit the price
        await Row("Thai tea").Locator("[data-testid=edit-item]").ClickAsync();
        await Page.FillAsync("[data-testid=item-price]", "60");
        await Page.ClickAsync("[data-testid=save-item]");
        await Expect(Row("Thai tea").Locator("[data-testid=menu-item-price]")).ToHaveTextAsync("฿60.00");

        // sold out and back
        await Row("Thai tea").Locator("[data-testid=toggle-available]").ClickAsync();
        await Expect(Row("Thai tea").Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Sold out");
        await Row("Thai tea").Locator("[data-testid=toggle-available]").ClickAsync();
        await Expect(Row("Thai tea").Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Available");

        // delete
        await Row("Thai tea").Locator("[data-testid=delete-item]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("deleted");
        await Expect(Page.Locator("[data-testid=menu-row]")).ToHaveCountAsync(3);
    }

    [Test]
    public async Task T62_Opening_hours_page_saves_split_shifts_and_closed_dates()
    {
        await Page.GotoAsync($"/Hours?clientConfigId={World.HoursEdit.Id}");
        await Expect(Page.Locator("body")).ToContainTextAsync("Asia/Dubai");
        await Expect(Page.Locator("[data-testid=hours-now]")).ToContainTextAsync("not set");

        ILocator Day(string d, string field) => Page.Locator($"[data-testid=hours-day][data-day={d}] [data-testid={field}]");

        // same open and close time is refused
        await Day("Monday", "open1").FillAsync("09:00");
        await Day("Monday", "close1").FillAsync("09:00");
        await Page.ClickAsync("[data-testid=save-hours]");
        await Expect(Page.Locator("[data-testid=hours-errors]")).ToContainTextAsync("can't be the same");

        // Monday 09:00-13:00 and 17:00-23:00, copied to every day, Friday closed
        await Day("Monday", "close1").FillAsync("13:00");
        await Day("Monday", "open2").FillAsync("17:00");
        await Day("Monday", "close2").FillAsync("23:00");
        await Page.ClickAsync("[data-testid=copy-monday]");
        await Day("Friday", "day-closed").CheckAsync();
        await Page.ClickAsync("[data-testid=save-hours]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("saved");

        await Expect(Day("Thursday", "open2")).ToHaveValueAsync("17:00");
        await Expect(Day("Friday", "day-closed")).ToBeCheckedAsync();
        await Expect(Page.Locator("[data-testid=hours-now]")).ToContainTextAsync(OpenStatus);

        // closed date: add, duplicate refused, past refused, remove
        var date = DateTime.UtcNow.Date.AddDays(20).ToString("yyyy-MM-dd");
        await Page.FillAsync("[data-testid=closed-date]", date);
        await Page.FillAsync("[data-testid=closed-reason]", "National Day");
        await Page.ClickAsync("[data-testid=add-closed-date]");
        await Expect(Page.Locator("[data-testid=closed-date-row]")).ToContainTextAsync("National Day");
        await Page.FillAsync("[data-testid=closed-date]", date);
        await Page.ClickAsync("[data-testid=add-closed-date]");
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("already");
        await Page.FillAsync("[data-testid=closed-date]", "2020-01-01");
        await Page.ClickAsync("[data-testid=add-closed-date]");
        await Expect(Page.Locator("[data-testid=alert-error]")).ToContainTextAsync("past");
        await Page.ClickAsync("[data-testid=delete-closed-date]");
        await Expect(Page.Locator("[data-testid=no-closed-dates]")).ToBeVisibleAsync();

        // the bot now gets these hours
        var user = Line.NewUser("t62");
        await Line.SendAsync(World.HoursEdit, Line.TextEvent(user, "hello hours edit"));
        await Waits.LineCallsAsync(user);
        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.UserMessage == "hello hours edit").System;
        Assert.That(prompt, Does.Contain("Mon 09:00-13:00 and 17:00-23:00"));
        Assert.That(prompt, Does.Contain("Fri closed"));
    }
}
