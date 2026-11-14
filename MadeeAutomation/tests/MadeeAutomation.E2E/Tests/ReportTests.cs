using System.Text;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Reports (013): AI usage is recorded per client, the client report adds up last month correctly in the
/// client's time zone, CSV downloads, "Email this report", the MadeeVision report and who may see what.
/// Last month's rows are written straight into the database with known numbers, so these tests don't
/// depend on what the other tests did today.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReportTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private static readonly object SeedLock = new();
    private static bool _seeded;

    /// <summary>First day of last month, Manila wall clock.</summary>
    private static DateTime FirstOfLastMonthManila()
    {
        var now = DateTime.UtcNow.AddHours(8);
        return new DateTime(now.Year, now.Month, 1).AddMonths(-1);
    }

    private static DateTime ManilaToUtc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-8), DateTimeKind.Utc);
    private static DateTime BangkokToUtc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-7), DateTimeKind.Utc);

    /// <summary>
    /// Glow Spa, last month: Joy Swedish completed (Tagalog) + Joy Swedish no-show + Mark foot spa completed
    /// (asked at 11 PM, closed) + Mark foot spa cancelled + Joy foot spa walk-in completed; 4 messages from 2
    /// customers (one seen the month before, one tap, one at 11 PM) + an "add friend"; 2 AI calls ($0.009).
    /// Bakery (Bangkok), last month: 2 + 1 Croissant completed, 1 Brownie cancelled.
    /// </summary>
    private static async Task SeedAsync()
    {
        lock (SeedLock) { if (_seeded) return; _seeded = true; }
        var first = FirstOfLastMonthManila();
        var spa = World.Spa.Id;
        var joy = await Db.ScalarAsync<Guid>("SELECT Id FROM dbo.StaffMembers WHERE ClientConfigId = @c AND Name = 'Joy'", ("@c", spa));
        var mark = await Db.ScalarAsync<Guid>("SELECT Id FROM dbo.StaffMembers WHERE ClientConfigId = @c AND Name = 'Mark'", ("@c", spa));
        var n = 900;
        async Task BookingAsync(string service, decimal price, Guid staff, string staffName, DateTime local, int status, string source = "chat",
            string? language = "en", DateTime? madeLocal = null)
        {
            n++;
            await Db.ExecAsync(@"
                INSERT INTO dbo.Bookings (Id, ClientConfigId, BookingNumber, Reference, Source, Channel, CustomerId, CustomerName, ServiceName, DurationMinutes,
                    Currency, Price, Total, StaffMemberId, StaffName, StaffPreference, StartUtc, EndUtc, BusyFromUtc, BusyUntilUtc, LocalDate, AtHome, Status,
                    Language, CreatedAtUtc)
                VALUES (NEWID(), @c, @n, @ref, @src, 'line', @cust, @name, @svc, 60, 'PHP', @price, @price, @staff, @staffName, 'any', @start, @end, @start, @end,
                    @day, 0, @status, @lang, @made);",
                ("@c", spa), ("@n", n), ("@ref", $"BK-{n}"), ("@src", source), ("@cust", "Useed" + n), ("@name", "Seed " + n), ("@svc", service),
                ("@price", price), ("@staff", staff), ("@staffName", staffName), ("@start", ManilaToUtc(local)), ("@end", ManilaToUtc(local.AddHours(1))),
                ("@day", local.Date), ("@status", status), ("@lang", (object?)language ?? DBNull.Value), ("@made", ManilaToUtc(madeLocal ?? local.AddDays(-1))));
        }
        await BookingAsync("Swedish massage", 800, joy, "Joy", first.AddDays(4).AddHours(14), 3, language: "tl");
        await BookingAsync("Swedish massage", 800, joy, "Joy", first.AddDays(5).AddHours(15), 6);
        await BookingAsync("Foot spa", 500, mark, "Mark", first.AddDays(5).AddHours(11), 3, madeLocal: first.AddDays(4).AddHours(23));
        await BookingAsync("Foot spa", 500, mark, "Mark", first.AddDays(6).AddHours(11), 4);
        await BookingAsync("Foot spa", 500, joy, "Joy", first.AddDays(7).AddHours(16), 3, source: "staff", language: null);

        var e = 0;
        async Task EventAsync(string customer, DateTime local, string kind = "text")
        {
            e++;
            await Db.ExecAsync(@"
                INSERT INTO dbo.InboundEvents (Id, ClientConfigId, Channel, ExternalEventId, CustomerId, Kind, MessageText, Status, Attempts, ReceivedAtUtc)
                VALUES (NEWID(), @c, 'line', @ext, @cust, @kind, 'x', 3, 1, @at);",
                ("@c", spa), ("@ext", "report-seed-" + e), ("@cust", customer), ("@kind", kind), ("@at", ManilaToUtc(local)));
        }
        await EventAsync("Ureportold", first.AddMonths(-1).AddDays(3).AddHours(12));
        await EventAsync("Ureportold", first.AddDays(2).AddHours(12));
        await EventAsync("Ureportnew", first.AddDays(2).AddHours(12).AddMinutes(5));
        await EventAsync("Ureportnew", first.AddDays(2).AddHours(12).AddMinutes(6), "tap");
        await EventAsync("Ureportnew", first.AddDays(3).AddHours(23));
        await EventAsync("Ureportnew", first.AddDays(3).AddHours(9), "follow");

        foreach (var (purpose, input, output, cost, day) in new[] { ("booking-message", 1000, 200, 0.006m, 2), ("notice-translate", 500, 100, 0.003m, 3) })
            await Db.ExecAsync(@"
                INSERT INTO dbo.AiUsage (Id, ClientConfigId, Purpose, Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, CostUsd, IsTest, CreatedAtUtc)
                VALUES (NEWID(), @c, @p, 'seed', @i, @o, 0, 0, @cost, 0, @at);",
                ("@c", spa), ("@p", purpose), ("@i", input), ("@o", output), ("@cost", cost), ("@at", ManilaToUtc(first.AddDays(day).AddHours(12))));

        var firstBangkok = new DateTime(DateTime.UtcNow.AddHours(7).Year, DateTime.UtcNow.AddHours(7).Month, 1).AddMonths(-1);
        foreach (var (num, status, item, qty, unit, local) in new[]
                 {
                     (901, 3, "Croissant", 2, 45m, firstBangkok.AddDays(9).AddHours(8)),
                     (902, 3, "Croissant", 1, 45m, firstBangkok.AddDays(9).AddHours(20)),
                     (903, 4, "Brownie", 1, 60m, firstBangkok.AddDays(10).AddHours(9))
                 })
        {
            var id = Guid.NewGuid();
            await Db.ExecAsync(@"
                INSERT INTO dbo.Orders (Id, ClientConfigId, OrderNumber, Reference, Channel, CustomerId, CustomerName, Currency, Total, OrderDate, Status, CreatedAtUtc)
                VALUES (@id, @c, @n, @ref, 'line', @cust, 'Seed', 'THB', @total, @day, @status, @at);",
                ("@id", id), ("@c", World.Bakery.Id), ("@n", num), ("@ref", $"ORD-{num}"), ("@cust", "Ureportb" + num), ("@total", qty * unit),
                ("@day", local.Date), ("@status", status), ("@at", BangkokToUtc(local)));
            await Db.ExecAsync("INSERT INTO dbo.OrderItems (Id, OrderId, Name, Quantity, UnitPrice, LineTotal) VALUES (NEWID(), @o, @name, @q, @u, @t);",
                ("@o", id), ("@name", item), ("@q", qty), ("@u", unit), ("@t", qty * unit));
        }
    }

    private Task<string> TileAsync(string key) => Page.Locator($"[data-testid=tile-{key}-value]").InnerTextAsync();

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private async Task<(string Name, string Text)> DownloadAsync(string selector)
    {
        var download = await Page.RunAndWaitForDownloadAsync(() => Page.ClickAsync(selector));
        var path = await download.PathAsync();
        var bytes = await File.ReadAllBytesAsync(path!);
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM so Excel shows ₱ / ฿");
        return (download.SuggestedFilename, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Test]
    public async Task T106_AI_usage_is_recorded_against_the_client()
    {
        var before = await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.AiUsage WHERE ClientConfigId = @c AND Purpose = 'booking-message' AND Model <> 'seed'",
            ("@c", World.Spa.Id));
        var user = Line.NewUser("t106");
        await Line.SendAsync(World.Spa, Line.TextEvent(user, "Pwede po foot spa bukas 3pm?"));
        await Eventually.GetAsync(() => Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.AiUsage WHERE ClientConfigId = @c AND Purpose = 'booking-message' AND Model <> 'seed'",
            ("@c", World.Spa.Id)), n => n > before, 20000, "an AiUsage row");
        var row = (await Db.RowsAsync("SELECT TOP 1 * FROM dbo.AiUsage WHERE ClientConfigId = @c AND Model <> 'seed' ORDER BY CreatedAtUtc DESC", ("@c", World.Spa.Id))).Single();
        Assert.That(row["InputTokens"], Is.EqualTo(1000));
        Assert.That(row["OutputTokens"], Is.EqualTo(200));
        Assert.That(row["CostUsd"], Is.EqualTo(0.006m), "1,000 in at $3 + 200 out at $15 per million");
        Assert.That(row["IsTest"], Is.EqualTo(false));
    }

    [Test]
    public async Task T107_Spa_report_adds_up_last_month_and_downloads()
    {
        await SeedAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Reports?clientConfigId={World.Spa.Id}&period=last-month");

        Assert.That(await TileAsync("appointments"), Is.EqualTo("4"), "cancelled left out");
        Assert.That(await TileAsync("revenue"), Is.EqualTo("₱1,800.00"), "completed only");
        Assert.That(await TileAsync("noshow"), Is.EqualTo("1"));
        Assert.That(await Page.Locator("[data-testid=tile-noshow]").InnerTextAsync(), Does.Contain("25%"));
        Assert.That(await TileAsync("requests"), Is.EqualTo("4"));
        Assert.That(await Page.Locator("[data-testid=tile-requests]").InnerTextAsync(), Does.Contain("1 while you were closed"));
        Assert.That(await TileAsync("messages"), Is.EqualTo("4"), "the 'add friend' is not counted");
        Assert.That(await Page.Locator("[data-testid=tile-messages]").InnerTextAsync(), Does.Contain("2 customers").And.Contain("1 new"));

        Assert.That(Squash(await Page.Locator("[data-testid=staff-row][data-staff='Joy']").InnerTextAsync()), Is.EqualTo("Joy 3 2 1 ₱1,300.00"));
        Assert.That(Squash(await Page.Locator("[data-testid=staff-row][data-staff='Mark']").InnerTextAsync()), Is.EqualTo("Mark 1 1 0 ₱500.00"));
        var services = await Page.Locator("[data-testid=top-services-row]").AllInnerTextsAsync();
        Assert.That(services[0], Does.StartWith("Foot spa").And.Contain("₱1,000.00"));
        Assert.That(await Page.Locator("[data-testid=fact-new]").InnerTextAsync(), Is.EqualTo("1 / 1"));
        Assert.That(await Page.Locator("[data-testid=fact-taps]").InnerTextAsync(), Is.EqualTo("1 / 3"));
        Assert.That(await Page.Locator("[data-testid=fact-afterhours]").InnerTextAsync(), Is.EqualTo("1"));
        Assert.That(await Page.Locator("[data-testid=client-ai-total]").InnerTextAsync(), Is.EqualTo("$0.0090"), "MadeeVision staff see the AI cost");

        var (name, csv) = await DownloadAsync("[data-testid=report-csv]");
        var lines = csv.Trim().Split("\r\n");
        Assert.That(name, Does.StartWith("glowspa-bookings-").And.EndWith(".csv"));
        Assert.That(lines[0], Does.StartWith("\"Reference\",\"Date\",\"Time\",\"Service\""));
        Assert.That(lines.Count(l => l.Contains("\"BK-90")), Is.EqualTo(5), "all 5, cancelled included");
        Assert.That(csv, Does.Contain("\"No-show\"").And.Contain("\"Staff (walk-in / phone)\"").And.Contain("\"800.00\""));

        Mailbox.Clear();
        await Page.ClickAsync("[data-testid=report-email]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Report sent to owner@glowspa.test");
        var mail = (await Mailbox.WaitForAsync("owner@glowspa.test")).Last();
        Assert.That(mail.Body, Does.Contain("Appointments: 4").And.Contain("Revenue from completed appointments: ₱1,800.00"));
    }

    [Test]
    public async Task T108_Bakery_report_counts_orders_and_best_sellers()
    {
        await SeedAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Reports?clientConfigId={World.Bakery.Id}&period=last-month");
        Assert.That(await TileAsync("orders"), Is.EqualTo("2"));
        Assert.That(await Page.Locator("[data-testid=tile-orders]").InnerTextAsync(), Does.Contain("1 cancelled"));
        Assert.That(await TileAsync("sales"), Is.EqualTo("฿135.00"));
        Assert.That(await Page.Locator("[data-testid=tile-sales]").InnerTextAsync(), Does.Contain("Average order ฿67.50"));
        var items = await Page.Locator("[data-testid=top-items-row]").AllInnerTextsAsync();
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0], Does.StartWith("Croissant").And.Contain("3 sold").And.Contain("฿135.00"));

        var (name, csv) = await DownloadAsync("[data-testid=report-csv]");
        Assert.That(name, Does.StartWith("bakery-orders-"));
        Assert.That(csv, Does.Contain("\"2 x Croissant\"").And.Contain("\"ORD-903\""));

        // Bad custom dates: told, and this month is shown instead.
        await Page.GotoAsync($"/Reports?clientConfigId={World.Bakery.Id}&period=custom&from=2026-11-10&to=2026-11-01");
        await Expect(Page.Locator("[data-testid=period-error]")).ToContainTextAsync("before the start date");
    }

    [Test]
    public async Task T109_MadeeVision_report_and_who_may_see_it()
    {
        await SeedAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync("/AdminReports?period=last-month");
        var spa = Page.Locator("[data-testid=client-row][data-client=glowspa]");
        Assert.That(await spa.Locator("[data-testid=client-messages]").InnerTextAsync(), Does.StartWith("4"));
        Assert.That(await spa.Locator("[data-testid=client-ai]").InnerTextAsync(), Does.StartWith("$0.0090").And.Contain("2 calls"));
        await Expect(Page.Locator("[data-testid=by-country-row][data-name=Philippines]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=by-type-row][data-name='Beauty & wellness']")).ToBeVisibleAsync();
        var (name, csv) = await DownloadAsync("[data-testid=report-csv]");
        Assert.That(name, Does.StartWith("madeeautomation-clients-"));
        Assert.That(csv, Does.Contain("\"glowspa\"").And.Contain("\"bakery\""));

        await Page.GotoAsync("/AdminReports?period=this-month");
        await Expect(Page.Locator("[data-testid=client-row][data-client=glowspa] [data-testid=client-last]")).ToHaveTextAsync("Today");

        // The spa's own portal user: their report (no AI cost), never the MadeeVision report.
        await Page.Context.ClearCookiesAsync();
        await AdminSession.SignInAsync(Page, World.SpaPortalEmail, World.PortalPassword);
        await Page.GotoAsync("/Reports?period=last-month");
        Assert.That(await TileAsync("appointments"), Is.EqualTo("4"));
        await Expect(Page.Locator("[data-testid=client-ai-cost]")).ToHaveCountAsync(0);
        await Page.GotoAsync($"/Reports?clientConfigId={World.Bakery.Id}&period=last-month");
        Assert.That(await TileAsync("appointments"), Is.EqualTo("4"), "another client's id still shows their own");
        var response = await Page.GotoAsync("/AdminReports");
        Assert.That(response!.Status, Is.EqualTo(403));
    }
}
