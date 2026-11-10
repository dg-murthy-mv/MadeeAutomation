using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 007: search, filters and paging on the Menu and Orders pages.
/// Uses the "bigmenu" client: 30 menu items and 45 orders seeded before the run (see World).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Round7SearchAndPagingTests : PageTest
{
    private static string Menu => $"/Menu?clientConfigId={World.BigMenu.Id}";
    private static string Orders => $"/Orders?clientConfigId={World.BigMenu.Id}";

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public async Task SignInAsync()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    private ILocator Rows => Page.Locator("[data-testid=menu-row]");
    private ILocator Cards => Page.Locator("[data-testid=order-card]");
    private ILocator Summary => Page.Locator("[data-testid=pager-summary]");

    [Test]
    public async Task T77_Menu_search_filters_and_paging()
    {
        await Page.GotoAsync(Menu);
        await Expect(Page.Locator("[data-testid=menu-total]")).ToHaveTextAsync("30 item(s)");
        await Expect(Rows).ToHaveCountAsync(25);
        await Expect(Summary).ToHaveTextAsync("Showing 1-25 of 30 items");
        await Expect(Page.Locator("[data-testid=menu-item-name]").First).ToHaveTextAsync("Cake 13");   // categories A-Z

        await Page.ClickAsync("[data-testid=pager-next]");
        await Expect(Rows).ToHaveCountAsync(5);
        await Expect(Summary).ToHaveTextAsync("Showing 26-30 of 30 items");
        await Expect(Page.Locator("[data-testid=menu-table]")).ToContainTextAsync("Other");            // items with no category go last

        // search: by name, and by an "other name" in Thai
        await Page.FillAsync("[data-testid=menu-search]", "Coffee 0");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-9 of 9 items");
        await Page.FillAsync("[data-testid=menu-search]", "กาแฟ");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Rows).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-testid=menu-item-name]")).ToHaveTextAsync("Coffee 07");
        await Page.ClickAsync("[data-testid=menu-clear]");
        await Expect(Rows).ToHaveCountAsync(25);

        // filters: category, no category, status, and both together
        await Page.SelectOptionAsync("[data-testid=menu-category-filter]", "Cakes");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Rows).ToHaveCountAsync(12);
        await Page.SelectOptionAsync("[data-testid=menu-category-filter]", "-");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Rows).ToHaveCountAsync(6);
        await Page.GotoAsync(Menu + "&st=soldout");
        await Expect(Rows).ToHaveCountAsync(3);
        await Page.GotoAsync(Menu + "&st=limited");
        await Expect(Rows).ToHaveCountAsync(5);
        await Page.GotoAsync(Menu + "&cat=Drinks&st=available");
        await Expect(Rows).ToHaveCountAsync(11);
        await Page.FillAsync("[data-testid=menu-search]", "zzz");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Page.Locator("[data-testid=menu-no-match]")).ToBeVisibleAsync();

        // page size, and a row button comes back to the same page of the same list
        await Page.GotoAsync(Menu);
        await Page.SelectOptionAsync("[data-testid=menu-page-size]", "10");
        await Page.ClickAsync("[data-testid=menu-apply]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-10 of 30 items");
        await Page.ClickAsync("[data-testid=pager-page-3]");
        await Expect(Summary).ToHaveTextAsync("Showing 21-30 of 30 items");
        var url = Page.Url;

        var row = Page.Locator("[data-testid=menu-row][data-item-name='Special 26']");
        await row.Locator("[data-testid=toggle-available]").ClickAsync();
        await Expect(row.Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Sold out");
        Assert.That(Page.Url, Is.EqualTo(url), "sold out: back on page 3 with 10 per page");

        await row.Locator("[data-testid=edit-item]").ClickAsync();
        await Page.CheckAsync("[data-testid=item-available]");
        await Page.ClickAsync("[data-testid=save-item]");
        await Expect(row.Locator("[data-testid=menu-item-status]")).ToHaveTextAsync("Available");
        Assert.That(Page.Url, Is.EqualTo(url), "edit: back on page 3 with 10 per page");

        // a page number past the end shows the last page, not an empty list
        await Page.GotoAsync(Menu + "&st=limited&page=9");
        await Expect(Rows).ToHaveCountAsync(5);
    }

    [Test]
    public async Task T78_Orders_search_day_filter_and_paging()
    {
        await Page.GotoAsync(Orders);
        await Expect(Cards).ToHaveCountAsync(20);
        await Expect(Summary).ToHaveTextAsync("Showing 1-20 of 38 orders");                // To do = New + Accepted
        await Expect(Page.Locator("[data-testid=order-ref]").First).ToHaveTextAsync("ORD-0038");   // newest first

        await Page.ClickAsync("[data-testid=pager-next]");
        await Expect(Cards).ToHaveCountAsync(18);
        await Page.ClickAsync("[data-testid=orders-tab-all]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-20 of 45 orders");

        // search: order number, customer, item
        await Page.FillAsync("[data-testid=orders-search]", "ORD-0017");
        await Page.ClickAsync("[data-testid=orders-apply]");
        await Expect(Cards).ToHaveCountAsync(1);
        await Page.FillAsync("[data-testid=orders-search]", "Somchai");
        await Page.ClickAsync("[data-testid=orders-apply]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-5 of 5 orders");
        await Page.ClickAsync("[data-testid=orders-tab-new]");                             // the tab keeps the search
        await Expect(Cards).ToHaveCountAsync(3);
        await Page.ClickAsync("[data-testid=orders-clear]");                               // clear keeps the tab
        await Expect(Summary).ToHaveTextAsync("Showing 1-20 of 30 orders");
        await Page.GotoAsync(Orders + "&show=all&q=Cake+13&ps=50");
        await Expect(Cards).ToHaveCountAsync(23);

        // day filter
        await Page.GotoAsync(Orders + "&show=all");
        await Page.ClickAsync("[data-testid=orders-tomorrow]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-9 of 9 orders");
        await Page.ClickAsync("[data-testid=orders-today]");
        await Expect(Summary).ToHaveTextAsync("Showing 1-20 of 36 orders");
        await Page.FillAsync("[data-testid=orders-search]", "nothing-like-this");
        await Page.ClickAsync("[data-testid=orders-apply]");
        await Expect(Page.Locator("[data-testid=orders-empty]")).ToContainTextAsync("No orders match this search.");

        // the pager and page size; Accept comes back to the same tab, search and page
        await Page.GotoAsync(Orders + "&show=all&ps=10&page=5");
        await Expect(Summary).ToHaveTextAsync("Showing 41-45 of 45 orders");
        await Page.GotoAsync(Orders + "&show=new&ps=10&page=2&q=Coffee");
        var url = Page.Url;
        var reference = await Page.Locator("[data-testid=order-ref]").First.InnerTextAsync();
        await Cards.First.Locator("[data-testid=order-accept]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"{reference} accepted");
        Assert.That(Page.Url, Is.EqualTo(url));
    }
}
