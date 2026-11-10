using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 003: Admin on a phone and a tablet (Playwright device emulation).
/// Runs on Chromium by default; run-browsers.ps1 also runs it on WebKit (Safari's engine).
/// Firefox can't emulate mobile devices, so this fixture is skipped there.
/// </summary>
[TestFixture("iPhone 13")]
[TestFixture("iPad (gen 7)")]
[NonParallelizable]
public class DeviceTests : PageTest
{
    private readonly string _device;

    public DeviceTests(string device) => _device = device;

    public override BrowserNewContextOptions ContextOptions() =>
        new(Playwright.Devices[_device]) { BaseURL = GlobalSetup.Settings.AdminUrl };

    [SetUp]
    public async Task SignInAsync()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Clients"));
    }

    [Test]
    public async Task T54_Every_admin_page_fits_the_screen()
    {
        var pages = new[]
        {
            "/Clients", $"/Clients/Details/{World.Bakery.Id}", $"/Clients/Edit/{World.Bakery.Id}", "/Clients/Create",
            $"/Clients/TestWorkflow/{World.Bakery.WorkflowId}", $"/ExecutionLogs?clientConfigId={World.Bakery.Id}",
            $"/InboundEvents?clientConfigId={World.Bakery.Id}", "/AdminUsers", "/Account/MyAccount", "/Account/ChangePassword",
            $"/Menu?clientConfigId={World.SpiceCafe.Id}", $"/Hours?clientConfigId={World.SpiceCafe.Id}",
            $"/Orders?clientConfigId={World.SpiceCafe.Id}&show=all", $"/ClientUsers/Create?clientConfigId={World.SpiceCafe.Id}"
        };

        var tooWide = new List<string>();
        foreach (var path in pages)
        {
            await Page.GotoAsync(path);
            var fits = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");
            if (!fits) tooWide.Add(path);
        }

        Assert.That(tooWide, Is.Empty, $"{_device}: these pages scroll sideways");
    }

    [Test]
    public async Task T55_Menu_opens_from_the_menu_button()
    {
        await Page.GotoAsync("/Clients");
        await Expect(Page.Locator("[data-testid=nav-admins]")).Not.ToBeVisibleAsync();
        await Page.ClickAsync(".navbar-toggler");
        await Expect(Page.Locator("[data-testid=nav-admins]")).ToBeVisibleAsync();
        await Page.ClickAsync("[data-testid=nav-admins]");
        await Expect(Page.Locator("[data-testid=admins-table]")).ToBeVisibleAsync();
    }
}
