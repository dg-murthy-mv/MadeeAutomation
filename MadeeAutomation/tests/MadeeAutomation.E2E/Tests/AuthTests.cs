using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Round 002: Admin sign-in, lockout, temporary passwords, change / forgot / reset password, error pages.</summary>
[TestFixture]
[NonParallelizable]
public class AuthTests : PageTest
{
    private static E2ESettings Settings => GlobalSetup.Settings;
    private const string Password = World.AdminPassword;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = Settings.AdminUrl };

    private async Task ExpectSignedInAsync() =>
        await Expect(Page.Locator("[data-testid=nav-logout]")).ToBeVisibleAsync();

    [Test]
    public async Task T29_Pages_need_sign_in_and_a_wrong_password_is_refused()
    {
        await Page.GotoAsync("/Clients");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/Login"));

        await AdminSession.SignInAsync(Page, World.AdminEmail, "not-the-password-1");
        await Expect(Page.Locator("[data-testid=login-error]")).ToContainTextAsync("Invalid email or password");

        await AdminSession.SignInAsync(Page, "nobody@e2e.test", Password);
        await Expect(Page.Locator("[data-testid=login-error]")).ToContainTextAsync("Invalid email or password");

        await AdminSession.SignInAsync(Page, World.AdminEmail, Password);
        await ExpectSignedInAsync();

        await Page.ClickAsync("[data-testid=nav-logout]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("signed out");
        await Page.GotoAsync("/Clients");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/Login"));
    }

    [Test]
    public async Task T30_First_admin_setup_page_is_gone_once_an_admin_exists()
    {
        await Page.GotoAsync("/Account/Setup");
        await Expect(Page.Locator("[data-testid=error-code]")).ToHaveTextAsync("404");
    }

    [Test]
    public async Task T31_New_admin_gets_a_temporary_password_and_must_change_it()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, Password);
        await Page.GotoAsync("/AdminUsers/Create");
        await Page.FillAsync("#DisplayName", "New Teammate");
        await Page.FillAsync("#Email", "teammate@e2e.test");
        await Page.ClickAsync("[data-testid=save-admin]");

        var message = await Page.Locator("[data-testid=alert-success]").InnerTextAsync();
        var temporary = Regex.Match(message, @"Temporary password: (\S+)").Groups[1].Value;
        Assert.That(temporary, Has.Length.EqualTo(12));

        await Page.ClickAsync("[data-testid=nav-logout]");
        await AdminSession.SignInAsync(Page, "teammate@e2e.test", temporary);
        await Expect(Page.Locator("[data-testid=must-change]")).ToBeVisibleAsync();

        await Page.GotoAsync("/Clients");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/ChangePassword"), new() { Timeout = 10000 });

        await Page.FillAsync("#CurrentPassword", temporary);
        await Page.FillAsync("#NewPassword", "short1");
        await Page.FillAsync("#ConfirmPassword", "short1");
        await Page.ClickAsync("[data-testid=change-password-submit]");
        await Expect(Page.Locator("[data-testid=error-NewPassword]")).ToContainTextAsync("at least 10");

        await Page.FillAsync("#CurrentPassword", temporary);
        await Page.FillAsync("#NewPassword", "Brightside2026");
        await Page.FillAsync("#ConfirmPassword", "Brightside2026");
        await Page.ClickAsync("[data-testid=change-password-submit]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Password changed");
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    [Test]
    public async Task T32_Five_wrong_passwords_lock_the_account()
    {
        for (var i = 0; i < 5; i++)
            await AdminSession.SignInAsync(Page, "lockme@e2e.test", "wrong-password-" + i);

        await AdminSession.SignInAsync(Page, "lockme@e2e.test", Password);   // right password, still locked
        await Expect(Page.Locator("[data-testid=login-error]")).ToContainTextAsync("Too many failed attempts");

        var lockedUntil = await GlobalSetup.Db.ScalarAsync<DateTime?>("SELECT LockoutEndUtc FROM dbo.AdminUsers WHERE Email = 'lockme@e2e.test'");
        Assert.That(lockedUntil, Is.Not.Null);
    }

    [Test]
    public async Task T33_Forgot_password_email_link_resets_once()
    {
        await Page.GotoAsync("/Account/ForgotPassword");
        await Page.FillAsync("#Email", "resetme@e2e.test");
        await Page.ClickAsync("[data-testid=forgot-submit]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("reset link is on its way");

        // Unknown email: same message, no email.
        await Page.GotoAsync("/Account/ForgotPassword");
        await Page.FillAsync("#Email", "ghost@e2e.test");
        await Page.ClickAsync("[data-testid=forgot-submit]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("reset link is on its way");

        var mail = await Mailbox.WaitForAsync("resetme@e2e.test");
        Assert.That(Mailbox.To("ghost@e2e.test"), Is.Empty);
        var link = Regex.Match(mail.Last().Body, @"(http\S+ResetPassword\S+)").Groups[1].Value;
        Assert.That(link, Is.Not.Empty, mail.Last().Body);

        await Page.GotoAsync(link);
        await Page.FillAsync("#NewPassword", "Lighthouse2026");
        await Page.FillAsync("#ConfirmPassword", "Lighthouse2026");
        await Page.ClickAsync("[data-testid=reset-submit]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("has been reset");

        await Page.GotoAsync(link);
        await Expect(Page.Locator("[data-testid=reset-link-problem]")).ToContainTextAsync("already been used");

        await AdminSession.SignInAsync(Page, "resetme@e2e.test", Password);
        await Expect(Page.Locator("[data-testid=login-error]")).ToContainTextAsync("Invalid email or password");
        await AdminSession.SignInAsync(Page, "resetme@e2e.test", "Lighthouse2026");
        await ExpectSignedInAsync();

        var stored = await GlobalSetup.Db.ScalarAsync<string>(
            "SELECT TOP 1 TokenHash FROM dbo.PasswordResetTokens t JOIN dbo.AdminUsers u ON u.Id = t.AdminUserId WHERE u.Email = 'resetme@e2e.test'");
        var token = Regex.Match(link, "token=([^&]+)").Groups[1].Value;
        Assert.That(stored, Is.Not.EqualTo(token), "only a hash of the token is stored");
    }

    [Test]
    public async Task T34_Changing_password_signs_out_other_browsers()
    {
        // Browser 1 (this page) and browser 2 both signed in as changeme.
        await AdminSession.SignInAsync(Page, "changeme@e2e.test", Password);
        await ExpectSignedInAsync();

        await using var other = await Browser.NewContextAsync(ContextOptions());
        var otherPage = await other.NewPageAsync();
        await AdminSession.SignInAsync(otherPage, "changeme@e2e.test", Password);
        await Expect(otherPage.Locator("[data-testid=nav-logout]")).ToBeVisibleAsync();

        await Page.GotoAsync("/Account/ChangePassword");
        await Page.FillAsync("#CurrentPassword", "wrong-current-1");
        await Page.FillAsync("#NewPassword", "Waterfall2026");
        await Page.FillAsync("#ConfirmPassword", "Waterfall2026");
        await Page.ClickAsync("[data-testid=change-password-submit]");
        await Expect(Page.Locator("[data-testid=error-CurrentPassword]")).ToContainTextAsync("not your current password");

        await Page.FillAsync("#CurrentPassword", Password);
        await Page.FillAsync("#NewPassword", "Waterfall2026");
        await Page.FillAsync("#ConfirmPassword", "Waterfall2026");
        await Page.ClickAsync("[data-testid=change-password-submit]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Password changed");

        await otherPage.GotoAsync("/Clients");
        await Expect(otherPage).ToHaveURLAsync(new Regex("/Account/Login"));
        await Page.GotoAsync("/Clients");
        await ExpectSignedInAsync();
    }

    [Test]
    public async Task T35_Deactivated_admin_is_signed_out_and_cannot_sign_in()
    {
        await using var victim = await Browser.NewContextAsync(ContextOptions());
        var victimPage = await victim.NewPageAsync();
        await AdminSession.SignInAsync(victimPage, "deactivateme@e2e.test", Password);
        await Expect(victimPage.Locator("[data-testid=nav-logout]")).ToBeVisibleAsync();

        await AdminSession.SignInAsync(Page, World.AdminEmail, Password);
        await Page.GotoAsync("/AdminUsers");
        await Page.Locator("[data-testid=admin-row][data-email='deactivateme@e2e.test'] [data-testid=toggle-admin]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("deactivated");

        await victimPage.GotoAsync("/Clients");
        await Expect(victimPage).ToHaveURLAsync(new Regex("/Account/Login"));
        await AdminSession.SignInAsync(victimPage, "deactivateme@e2e.test", Password);
        await Expect(victimPage.Locator("[data-testid=login-error]")).ToContainTextAsync("Invalid email or password");

        // You can't deactivate yourself.
        await Expect(Page.Locator("[data-testid=admin-row][data-email='admin@e2e.test'] [data-testid=toggle-admin]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task T36_Friendly_error_pages()
    {
        await AdminSession.SignInAsync(Page, World.AdminEmail, Password);

        await Page.GotoAsync($"/Clients/Details/{Guid.NewGuid()}");
        await Expect(Page.Locator("[data-testid=error-title]")).ToHaveTextAsync("Page not found");

        await Page.GotoAsync("/This/Does/Not/Exist");
        await Expect(Page.Locator("[data-testid=error-code]")).ToHaveTextAsync("404");
    }
}
