using Microsoft.Playwright;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>Signs a Playwright page in to Admin.Web.</summary>
public static class AdminSession
{
    public static async Task SignInAsync(IPage page, string email, string password)
    {
        await page.GotoAsync("/Account/Login");
        await page.FillAsync("#Email", email);
        await page.FillAsync("#Password", password);
        await page.ClickAsync("[data-testid=login-submit]");
    }
}
