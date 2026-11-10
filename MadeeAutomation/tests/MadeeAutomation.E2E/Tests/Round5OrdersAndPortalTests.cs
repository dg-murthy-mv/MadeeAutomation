using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 005: every order is saved and sent to the client's staff (so a shop with no POS still gets it),
/// daily limits per menu item, and the client portal (a client's own user sees only their own business).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Round5OrdersAndPortalTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public void AcceptConfirmDialogs() => Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

    private async Task SignInAsAdminAsync()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    private async Task SignInToPortalAsync(string email = World.PortalEmail)
    {
        await AdminSession.SignInAsync(Page, email, World.PortalPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Portal$"));
    }

    /// <summary>Sends one LINE message and returns (customer id, the bot's reply).</summary>
    private static async Task<(string user, string reply)> AskAsync(SeededClient client, string text, string label)
    {
        var user = Line.NewUser(label);
        await Line.SendAsync(client, Line.TextEvent(user, text));
        var calls = await Waits.LineCallsAsync(user);
        return (user, calls[0].Text);
    }

    private static async Task<Dictionary<string, object?>> OrderOfAsync(string customerId)
    {
        var rows = await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c", ("@c", customerId)),
            r => r.Count > 0, 15000, $"saved order for {customerId}");
        return rows[0];
    }

    private static Task<List<LineCall>> StaffMessagesAsync(string contains) =>
        Eventually.GetAsync(
            () => GlobalSetup.Fakes.LineCalls.Where(c => c.To == World.CakeShopStaffLineId && c.Text.Contains(contains)).ToList(),
            m => m.Count > 0, 15000, $"staff LINE message containing '{contains}'");

    private ILocator OrderCard(string reference) => Page.Locator($"[data-testid=order-card][data-order-ref='{reference}']");
    private ILocator MenuRow(string name) => Page.Locator($"[data-testid=menu-row][data-item-name='{name}']");

    // ---------------- orders for a shop with no POS ----------------

    [Test]
    public async Task T63_Shop_with_no_POS_gets_the_order_on_LINE_and_email_and_it_is_saved()
    {
        var (user, reply) = await AskAsync(World.CakeShop, "2 tea less sugar", "t63");

        var order = await OrderOfAsync(user);
        var reference = (string)order["Reference"]!;
        Assert.That(reference, Does.Match(@"^ORD-\d{4}$"), "no POS: our own order reference");
        Assert.That(reply, Does.Contain(reference));
        Assert.That(reply, Does.EndWith("Total: ฿110.00"));
        Assert.That(order["PosOrderId"], Is.Null);
        Assert.That(order["Status"], Is.EqualTo(1), "New");
        Assert.That(order["Total"], Is.EqualTo(110m));
        Assert.That(order["CustomerName"], Is.EqualTo(FakeServer.CustomerNameFor(user)), "the LINE display name is saved");
        Assert.That(GlobalSetup.Fakes.OrdersFor(user), Is.Empty, "nothing goes to a POS");

        var staff = (await StaffMessagesAsync($"New order {reference}")).Single().Text;
        Assert.That(staff, Does.Contain($"New order {reference} - E2E Cake Shop"));
        Assert.That(staff, Does.Contain("2 x Thai tea - ฿110.00"), "\"tea\" matched the menu item");
        Assert.That(staff, Does.Contain("Total: ฿110.00"));
        Assert.That(staff, Does.Contain($"Customer: {FakeServer.CustomerNameFor(user)} (LINE)"));
        Assert.That(staff, Does.Contain("Note: less sugar"));
        Assert.That(staff, Does.Contain("No POS is connected"));

        var mail = await Eventually.GetAsync(() => Mailbox.To("owner@cakeshop.test").Where(m => m.Subject.Contains(reference)).ToList(),
            m => m.Count > 0, 15000, "new-order email");
        Assert.That(mail[0].Subject, Is.EqualTo($"[E2E Cake Shop] New order {reference}"));
        Assert.That(mail[0].Body, Does.Contain("2 x Thai tea"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(3), "Succeeded");
        Assert.That((string)log["ResultSummary"]!, Does.StartWith($"Order {reference} created"));
    }

    [Test]
    public async Task T64_Shop_with_a_POS_keeps_the_POS_reference_and_the_order_is_saved_too()
    {
        var (user, reply) = await AskAsync(World.Bakery, "3 croissants tomorrow 9am", "t64");

        var posOrder = GlobalSetup.Fakes.OrdersFor(user).Single();
        var order = await OrderOfAsync(user);
        Assert.That(order["Reference"], Is.EqualTo(posOrder.OrderId));
        Assert.That(order["PosOrderId"], Is.EqualTo(posOrder.OrderId));
        Assert.That(reply, Does.Contain(posOrder.OrderId));
        Assert.That(order["RequestedForUtc"], Is.Not.Null);
        Assert.That(order["RequestedForText"], Is.EqualTo("tomorrow 9am"));

        var items = await Db.RowsAsync("SELECT * FROM dbo.OrderItems WHERE OrderId = @o", ("@o", order["Id"]));
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0]["Name"], Is.EqualTo("Croissant"));
        Assert.That(items[0]["Quantity"], Is.EqualTo(3));
        Assert.That(items[0]["LineTotal"], Is.EqualTo(135m));

        var mail = await Eventually.GetAsync(() => Mailbox.To("owner@bakery.test").Where(m => m.Subject.Contains(posOrder.OrderId)).ToList(),
            m => m.Count > 0, 15000, "new-order email for the bakery");
        Assert.That(mail[0].Body, Does.Contain("It is also in your POS."));
        Assert.That(mail[0].Body, Does.Contain("09:00 (customer said \"tomorrow 9am\")"));
    }

    [Test]
    public async Task T65_Daily_limit_is_counted_per_day_and_a_cancelled_order_frees_it()
    {
        // Birthday cake: 3 a day.
        var (first, r1) = await AskAsync(World.CakeShop, "2 cake", "t65a");
        Assert.That(r1, Does.EndWith("Total: ฿1,000.00"));

        var (_, r2) = await AskAsync(World.CakeShop, "2 cake", "t65b");
        Assert.That(r2, Is.EqualTo("Sorry, only 1 Birthday cake left for today."));

        var (_, r3) = await AskAsync(World.CakeShop, "1 cake", "t65c");
        Assert.That(r3, Does.EndWith("Total: ฿500.00"));

        var (fullUser, r4) = await AskAsync(World.CakeShop, "1 cake", "t65d");
        Assert.That(r4, Is.EqualTo("Sorry, Birthday cake is fully booked for today."));
        Assert.That(await Db.RowsAsync("SELECT Id FROM dbo.Orders WHERE CustomerId = @c", ("@c", fullUser)), Is.Empty, "a refused order is not saved");

        // the AI is told too
        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.System.Contains("E2E Cake Shop")).System;
        Assert.That(prompt, Does.Contain("- Birthday cake: ฿500.00 [daily limit 3 - NONE left for today; can still be ordered for another day]"));

        // a mixed order: the cake is left out, the rest is ordered
        var (mixed, r5) = await AskAsync(World.CakeShop, "1 cake and 2 samosa", "t65e");
        Assert.That(r5, Does.Contain("(Not included - Birthday cake is fully booked for today)"));
        Assert.That(r5, Does.EndWith("Total: ฿50.00"));
        Assert.That((await OrderOfAsync(mixed))["Total"], Is.EqualTo(50m));

        // tomorrow has its own count
        var (_, r6) = await AskAsync(World.CakeShop, "3 cake tomorrow 9am", "t65f");
        Assert.That(r6, Does.EndWith("Total: ฿1,500.00"));
        var (_, r7) = await AskAsync(World.CakeShop, "1 cake tomorrow 9am", "t65g");
        Assert.That(r7, Does.Match(@"^Sorry, Birthday cake is fully booked for \w{3} \d{1,2} \w{3}\.$"));

        // Admin: the menu shows the usage, and cancelling an order frees it
        await SignInAsAdminAsync();
        await Page.GotoAsync($"/Menu?clientConfigId={World.CakeShop.Id}");
        await Expect(MenuRow("Birthday cake").Locator("[data-testid=menu-item-limit]")).ToHaveTextAsync("3 of 3 ordered today");

        var firstRef = (string)(await OrderOfAsync(first))["Reference"]!;
        await Page.GotoAsync($"/Orders?clientConfigId={World.CakeShop.Id}&show=new");
        await OrderCard(firstRef).Locator("[data-testid=order-cancel]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"{firstRef} cancelled");

        var (_, r8) = await AskAsync(World.CakeShop, "2 cake", "t65h");
        Assert.That(r8, Does.EndWith("Total: ฿1,000.00"), "the 2 cancelled cakes are free again");
    }

    [Test]
    public async Task T66_Admin_orders_page_accept_complete_and_tabs()
    {
        var (user, _) = await AskAsync(World.CakeShop, "4 samosa", "t66");
        var reference = (string)(await OrderOfAsync(user))["Reference"]!;

        await SignInAsAdminAsync();
        await Page.GotoAsync($"/Clients/Details/{World.CakeShop.Id}");
        await Expect(Page.Locator("[data-testid=posfirst-status]")).ToContainTextAsync("No POS");
        await Expect(Page.Locator("[data-testid=orders-only-warning]")).ToHaveCountAsync(0);   // staff LINE + email are set
        await Page.ClickAsync("[data-testid=open-orders]");

        var card = OrderCard(reference);
        await Expect(card.Locator("[data-testid=order-items]")).ToContainTextAsync("4 x Samosa");
        await Expect(card.Locator("[data-testid=order-total]")).ToHaveTextAsync("฿100.00");
        await Expect(card.Locator("[data-testid=order-customer]")).ToContainTextAsync(FakeServer.CustomerNameFor(user));

        await card.Locator("[data-testid=order-accept]").ClickAsync();
        await Expect(OrderCard(reference).Locator("[data-testid=order-status]")).ToHaveTextAsync("Accepted");
        await Expect(OrderCard(reference)).ToContainTextAsync("Accepted by E2E Admin");

        await OrderCard(reference).Locator("[data-testid=order-complete]").ClickAsync();
        await Expect(OrderCard(reference)).ToHaveCountAsync(0);   // left the "To do" tab
        await Page.ClickAsync("[data-testid=orders-tab-done]");
        await Expect(OrderCard(reference).Locator("[data-testid=order-status]")).ToHaveTextAsync("Completed");
        await Expect(OrderCard(reference).Locator("[data-testid=order-accept]")).ToHaveCountAsync(0);

        Assert.That((await OrderOfAsync(user))["Status"], Is.EqualTo(3), "Completed");
    }

    // ---------------- client portal ----------------

    [Test]
    public async Task T67_Admin_adds_a_portal_user_who_must_choose_a_password_and_lands_on_their_portal()
    {
        await SignInAsAdminAsync();
        await Page.GotoAsync($"/Clients/Details/{World.CakeShop.Id}");
        await Page.ClickAsync("[data-testid=new-portal-user]");

        // an email that already has an account is refused
        await Page.FillAsync("#DisplayName", "Duplicate");
        await Page.FillAsync("#Email", World.AdminEmail);
        await Page.ClickAsync("[data-testid=save-portal-user]");
        await Expect(Page.Locator("[data-testid=error-Email]")).ToContainTextAsync("already");

        await Page.FillAsync("#DisplayName", "Nok Owner");
        await Page.FillAsync("#Email", "Nok@CakeShop.test");
        await Page.ClickAsync("[data-testid=save-portal-user]");
        var message = await Page.Locator("[data-testid=alert-success]").InnerTextAsync();
        var temporary = Regex.Match(message, @"Temporary password: (\S+)").Groups[1].Value;
        Assert.That(temporary, Has.Length.EqualTo(12));
        await Expect(Page.Locator("[data-testid=portal-user-row][data-email='nok@cakeshop.test'] [data-testid=portal-user-status]"))
            .ToHaveTextAsync("Must change password");

        // portal users are not listed with the admins
        await Page.GotoAsync("/AdminUsers");
        await Expect(Page.Locator("[data-testid=admin-row][data-email='nok@cakeshop.test']")).ToHaveCountAsync(0);
        await Page.ClickAsync("[data-testid=nav-logout]");

        // first sign-in: must choose a password, then the portal
        await AdminSession.SignInAsync(Page, "nok@cakeshop.test", temporary);
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/ChangePassword"));
        await Page.GotoAsync("/Portal");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/ChangePassword"));
        await Page.FillAsync("#CurrentPassword", temporary);
        await Page.FillAsync("#NewPassword", "CakeShop2026xx");
        await Page.FillAsync("#ConfirmPassword", "CakeShop2026xx");
        await Page.ClickAsync("[data-testid=change-password-submit]");

        await Expect(Page).ToHaveURLAsync(new Regex("/Portal$"));
        await Expect(Page.Locator("[data-testid=portal-client-name]")).ToHaveTextAsync("E2E Cake Shop");
        await Expect(Page.Locator("[data-testid=nav-admins]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=nav-orders]")).ToBeVisibleAsync();
    }

    [Test]
    public async Task T68_Portal_user_cannot_reach_admin_pages_or_another_client()
    {
        await SignInToPortalAsync();

        // every admin page answers "Not allowed"
        foreach (var path in new[]
                 {
                     "/Clients", $"/Clients/Details/{World.PortalCafe.Id}", $"/Clients/Edit/{World.PortalCafe.Id}", "/Clients/Create",
                     $"/Clients/CreateWorkflow?clientConfigId={World.PortalCafe.Id}", $"/Clients/TestWorkflow/{World.PortalCafe.WorkflowId}",
                     "/AdminUsers", "/AdminUsers/Create", $"/ClientUsers/Create?clientConfigId={World.PortalCafe.Id}",
                     $"/ExecutionLogs?clientConfigId={World.PortalCafe.Id}", $"/InboundEvents?clientConfigId={World.PortalCafe.Id}"
                 })
        {
            var response = await Page.GotoAsync(path);
            Assert.That(response!.Status, Is.EqualTo(403), path);
            await Expect(Page.Locator("[data-testid=error-title]")).ToHaveTextAsync("Not allowed");
        }

        // another client's id in the URL is ignored: they still get their OWN pages
        await Page.GotoAsync($"/Menu?clientConfigId={World.SpiceCafe.Id}");
        await Expect(Page.Locator("body")).ToContainTextAsync("E2E Portal Cafe");
        await Expect(MenuRow("Thai tea")).ToHaveCountAsync(1);
        await Expect(MenuRow("Masala chai")).ToHaveCountAsync(0);

        await Page.GotoAsync($"/Hours?clientConfigId={World.SpiceCafe.Id}");
        await Expect(Page.Locator("body")).ToContainTextAsync("Asia/Bangkok");
        await Expect(Page.Locator("body")).Not.ToContainTextAsync("Diwali");

        var (spiceUser, _) = await AskAsync(World.SpiceCafe, "1 samosa", "t68");
        var spiceRef = (string)(await OrderOfAsync(spiceUser))["Reference"]!;
        await Page.GotoAsync($"/Orders?clientConfigId={World.SpiceCafe.Id}&show=all");
        await Expect(OrderCard(spiceRef)).ToHaveCountAsync(0);

        // another client's menu item: not found
        var spiceItem = (await Db.RowsAsync("SELECT Id FROM dbo.MenuItems WHERE ClientConfigId = @c AND Name = 'Samosa'", ("@c", World.SpiceCafe.Id)))[0]["Id"]!;
        var edit = await Page.GotoAsync($"/Menu/Edit/{spiceItem}");
        Assert.That(edit!.Status, Is.EqualTo(404));

        // forged form posts (with the portal user's own valid anti-forgery token) change nothing
        await Page.GotoAsync("/Menu");
        const string post = @"async (path) => {
            const token = document.querySelector('input[name=__RequestVerificationToken]').value;
            const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                                          body: '__RequestVerificationToken=' + encodeURIComponent(token) });
            return r.status; }";

        Assert.That(await Page.EvaluateAsync<int>(post, $"/Menu/ToggleAvailable/{spiceItem}"), Is.EqualTo(404));
        Assert.That(await Page.EvaluateAsync<int>(post, $"/Menu/Delete/{spiceItem}"), Is.EqualTo(404));
        var item = await Db.RowsAsync("SELECT IsAvailable FROM dbo.MenuItems WHERE Id = @i", ("@i", spiceItem));
        Assert.That(item, Has.Count.EqualTo(1), "still there");
        Assert.That(item[0]["IsAvailable"], Is.EqualTo(true), "still available");

        var diwali = (await Db.RowsAsync("SELECT Id FROM dbo.ClosedDates WHERE ClientConfigId = @c", ("@c", World.SpiceCafe.Id)))[0]["Id"]!;
        await Page.EvaluateAsync<int>(post, $"/Hours/DeleteClosedDate?clientConfigId={World.SpiceCafe.Id}&id={diwali}");
        Assert.That(await Db.RowsAsync("SELECT Id FROM dbo.ClosedDates WHERE Id = @i", ("@i", diwali)), Has.Count.EqualTo(1), "Diwali is still a closed date");

        var spiceOrderId = (await OrderOfAsync(spiceUser))["Id"]!;
        Assert.That(await Page.EvaluateAsync<int>(post, $"/Orders/SetStatus?clientConfigId={World.SpiceCafe.Id}&id={spiceOrderId}&status=Cancelled"), Is.EqualTo(404));
        Assert.That((await OrderOfAsync(spiceUser))["Status"], Is.EqualTo(1), "still New");

        Assert.That(await Page.EvaluateAsync<int>(post, $"/ClientUsers/ToggleActive/{Guid.NewGuid()}"), Is.EqualTo(403));
    }

    [Test]
    public async Task T69_Portal_user_changes_menu_hours_and_business_info_and_the_bot_uses_them()
    {
        await SignInToPortalAsync();

        // ---- menu: new item with a daily limit, sold-out switch ----
        await Page.ClickAsync("[data-testid=nav-menu]");
        await Page.FillAsync("[data-testid=item-name]", "Butter cake");
        await Page.FillAsync("[data-testid=item-price]", "120");
        await Page.FillAsync("[data-testid=item-daily-limit]", "2");
        await Page.ClickAsync("[data-testid=add-item]");
        await Expect(MenuRow("Butter cake").Locator("[data-testid=menu-item-limit]")).ToHaveTextAsync("0 of 2 ordered today");

        var (_, ok) = await AskAsync(World.PortalCafe, "2 cake", "t69a");
        Assert.That(ok, Does.EndWith("Total: ฿240.00"));
        var (_, full) = await AskAsync(World.PortalCafe, "1 cake", "t69b");
        Assert.That(full, Is.EqualTo("Sorry, Butter cake is fully booked for today."));

        await MenuRow("Thai tea").Locator("[data-testid=toggle-available]").ClickAsync();
        await Expect(MenuRow("Thai tea").Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Sold out");
        var (_, soldOut) = await AskAsync(World.PortalCafe, "1 tea", "t69c");
        Assert.That(soldOut, Is.EqualTo("Sorry, Thai tea is not available today."));
        await MenuRow("Thai tea").Locator("[data-testid=toggle-available]").ClickAsync();
        await Expect(MenuRow("Thai tea").Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Available");

        // ---- opening hours ----
        await Page.ClickAsync("[data-testid=nav-hours]");
        ILocator Day(string d, string field) => Page.Locator($"[data-testid=hours-day][data-day={d}] [data-testid={field}]");
        await Day("Monday", "open1").FillAsync("08:00");
        await Day("Monday", "close1").FillAsync("18:00");
        await Page.ClickAsync("[data-testid=copy-monday]");
        await Page.ClickAsync("[data-testid=save-hours]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("saved");

        // ---- business information ----
        var (_, before) = await AskAsync(World.PortalCafe, "is there parking?", "t69d");
        Assert.That(before, Does.Contain("staff member"));

        await Page.ClickAsync("[data-testid=nav-business-info]");
        await Page.FillAsync("[data-testid=business-info-text]", "Delivery: Ban Chang only.\nParking: free in front of the shop.");
        await Page.FillAsync("[data-testid=business-info-email]", "orders@portalcafe.test");
        await Page.ClickAsync("[data-testid=save-business-info]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Saved");

        var (asker, after) = await AskAsync(World.PortalCafe, "is there parking?", "t69e");
        Assert.That(after, Is.EqualTo("Parking: free in front of the shop."));
        var prompt = GlobalSetup.Fakes.ClaudeCalls.Last(c => c.UserMessage == "is there parking?").System;
        Assert.That(prompt, Does.Contain("Mon 08:00-18:00"), "the hours saved in the portal reach the AI");

        // new orders now go to the email the client set themselves
        var (buyer, _) = await AskAsync(World.PortalCafe, "1 samosa", "t69f");
        var reference = (string)(await OrderOfAsync(buyer))["Reference"]!;
        var mail = await Eventually.GetAsync(() => Mailbox.To("orders@portalcafe.test").Where(m => m.Subject.Contains(reference)).ToList(),
            m => m.Count > 0, 15000, "new-order email to the address set in the portal");
        Assert.That(mail[0].Body, Does.Contain("1 x Samosa"));

        // the portal start page shows it all
        await Page.ClickAsync("[data-testid=nav-portal-home]");
        await Expect(Page.Locator("[data-testid=portal-hours-status]")).ToHaveTextAsync(new Regex("OPEN now|CLOSED now|Closed today"));
        await Expect(Page.Locator("[data-testid=portal-menu-count]")).ToHaveTextAsync("3");
        await Expect(OrderCard(reference)).ToBeVisibleAsync();
    }

    [Test]
    public async Task T70_Portal_user_sees_and_handles_only_their_own_orders()
    {
        var (mine, _) = await AskAsync(World.PortalCafe, "2 samosa", "t70a");
        var myRef = (string)(await OrderOfAsync(mine))["Reference"]!;
        var (other, _) = await AskAsync(World.CakeShop, "1 samosa", "t70b");
        var otherRef = (string)(await OrderOfAsync(other))["Reference"]!;

        await SignInToPortalAsync();
        await Page.ClickAsync("[data-testid=nav-orders]");
        await Page.ClickAsync("[data-testid=orders-tab-all]");

        // Both shops number their own orders from ORD-0001, so check by customer, not by reference.
        await Expect(Page.Locator("[data-testid=order-card]").Filter(new() { HasText = FakeServer.CustomerNameFor(mine) })).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-testid=order-card]").Filter(new() { HasText = FakeServer.CustomerNameFor(other) })).ToHaveCountAsync(0);
        Assert.That(otherRef, Does.StartWith("ORD-"));

        var card = Page.Locator("[data-testid=order-card]").Filter(new() { HasText = FakeServer.CustomerNameFor(mine) });
        await card.Locator("[data-testid=order-accept]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"{myRef} accepted");

        var saved = await OrderOfAsync(mine);
        Assert.That(saved["Status"], Is.EqualTo(2), "Accepted");
        Assert.That(saved["StatusChangedBy"], Is.EqualTo("Portal Owner"));
        Assert.That((await OrderOfAsync(other))["Status"], Is.EqualTo(1), "the other shop's order is untouched");

        // My account: a portal user has no time zone to choose
        await Page.GotoAsync("/Account/MyAccount");
        await Expect(Page.Locator("#TimeZoneId")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task T71_Deactivated_portal_user_is_signed_out_at_once()
    {
        await SignInToPortalAsync(World.PortalSecondEmail);

        // an admin deactivates them from the client's page (a second browser)
        await using (var adminContext = await Browser.NewContextAsync(new() { BaseURL = GlobalSetup.Settings.AdminUrl }))
        {
            var admin = await adminContext.NewPageAsync();
            await AdminSession.SignInAsync(admin, World.AdminEmail, World.AdminPassword);
            await admin.GotoAsync($"/Clients/Details/{World.PortalCafe.Id}");
            await admin.Locator($"[data-testid=portal-user-row][data-email='{World.PortalSecondEmail}'] [data-testid=toggle-portal-user]").ClickAsync();
            await Expect(admin.Locator("[data-testid=alert-success]")).ToContainTextAsync("deactivated");
        }

        await Page.GotoAsync("/Orders");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/Login"));
        await AdminSession.SignInAsync(Page, World.PortalSecondEmail, World.PortalPassword);
        await Expect(Page.Locator("body")).ToContainTextAsync("Invalid email or password");
    }
}

/// <summary>Round 005: the client portal on a phone and a tablet.</summary>
[TestFixture("iPhone 13")]
[TestFixture("iPad (gen 7)")]
[NonParallelizable]
public class PortalDeviceTests : PageTest
{
    private readonly string _device;

    public PortalDeviceTests(string device) => _device = device;

    public override BrowserNewContextOptions ContextOptions() =>
        new(Playwright.Devices[_device]) { BaseURL = GlobalSetup.Settings.AdminUrl };

    [Test]
    public async Task T72_Every_portal_page_fits_the_screen()
    {
        await AdminSession.SignInAsync(Page, World.PortalEmail, World.PortalPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Portal$"));

        var tooWide = new List<string>();
        foreach (var path in new[] { "/Portal", "/Orders?show=all", "/Menu", "/Hours", "/Portal/BusinessInfo", "/Account/MyAccount", "/Account/ChangePassword" })
        {
            await Page.GotoAsync(path);
            var fits = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");
            if (!fits) tooWide.Add(path);
        }

        Assert.That(tooWide, Is.Empty, $"{_device}: these portal pages scroll sideways");
    }
}
