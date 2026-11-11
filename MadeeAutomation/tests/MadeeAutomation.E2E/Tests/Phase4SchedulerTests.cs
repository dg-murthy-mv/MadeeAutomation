using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Phase 4, reliability:
///   - scheduled jobs run at the time in the CLIENT's own time zone (07:00 Bangkok = 00:00 UTC)
///   - a run missed while WebApi was off is skipped, not run hours late
///   - a failing job is retried (here every 3 s, twice), then the client's staff are told
///   - a paused client's jobs do not run
///   - POS down: the order is kept, staff are told, "Send to POS again" fixes it later
///   - LINE briefly unavailable: the push is retried with the same retry key (no double message)
/// WebApi runs with Scheduler:PollSeconds=1 and Scheduler:RetryDelaysMinutes=0.05,0.05 (see AppHost).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Phase4SchedulerTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private static Task<Dictionary<string, object?>> JobAsync(Guid id) =>
        Db.RowsAsync("SELECT * FROM dbo.WorkflowDefinitions WHERE Id = @id", ("@id", id)).ContinueWith(t => t.Result[0]);

    private static Task<Dictionary<string, object?>> JobWhenAsync(Guid id, Func<Dictionary<string, object?>, bool> done, string what, int timeoutMs = 20000) =>
        Eventually.GetAsync(() => JobAsync(id), done, timeoutMs, what);

    private static Task<List<Dictionary<string, object?>>> RunsAsync(Guid id) =>
        Db.RowsAsync("SELECT Status, TriggerPayloadJson FROM dbo.ExecutionLogs WHERE WorkflowDefinitionId = @id ORDER BY StartedAtUtc", ("@id", id));

    /// <summary>Pretend the job became due minutesAgo minutes ago.</summary>
    private static Task MakeDueAsync(Guid id, int minutesAgo = 0) =>
        Db.ExecAsync("UPDATE dbo.WorkflowDefinitions SET NextRunAtUtc = DATEADD(MINUTE, -@m, SYSUTCDATETIME()), RetryAttempt = 0 WHERE Id = @id",
            ("@id", id), ("@m", minutesAgo));

    private static string Status(Dictionary<string, object?> job) => job["LastRunStatus"] as string ?? "";

    private async Task SignInAsync()
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));
    }

    [Test]
    public async Task T87_Scheduled_job_runs_at_the_clients_local_time_and_only_once()
    {
        // WebApi works out the first time by itself - without running the job.
        var job = await JobWhenAsync(World.SchedOkJob, j => j["NextRunAtUtc"] is DateTime, "first next-run time");
        var next = (DateTime)job["NextRunAtUtc"]!;
        Assert.That((next.Hour, next.Minute), Is.EqualTo((0, 0)), "07:00 in Bangkok is 00:00 UTC");
        Assert.That(next, Is.GreaterThan(DateTime.UtcNow));
        Assert.That(await RunsAsync(World.SchedOkJob), Is.Empty, "finding the first time must not run the job");

        // Manila cron "30 6 * * *" = 22:30 UTC
        var manila = await JobWhenAsync(World.SchedFailJob, j => j["NextRunAtUtc"] is DateTime, "Manila next-run time");
        var m = (DateTime)manila["NextRunAtUtc"]!;
        Assert.That((m.Hour, m.Minute), Is.EqualTo((22, 30)));

        // Due now -> runs once, staff get the low-stock list, next run moves to the next 07:00 Bangkok.
        await MakeDueAsync(World.SchedOkJob);
        job = await JobWhenAsync(World.SchedOkJob, j => Status(j).StartsWith("Succeeded"), "a successful run");
        Assert.That(Status(job), Does.Contain("Next:").And.Contain("07:00"));
        var after = (DateTime)job["NextRunAtUtc"]!;
        Assert.That(after, Is.GreaterThan(DateTime.UtcNow));
        Assert.That(after.Hour, Is.EqualTo(0));

        var mail = await Mailbox.WaitForAsync("owner@schedok.test");
        Assert.That(mail[0].Body, Does.Contain("Low stock alert").And.Contain("Butter"));

        await Task.Delay(2500);
        var runs = await RunsAsync(World.SchedOkJob);
        Assert.That(runs, Has.Count.EqualTo(1), "a due job runs exactly once");
        Assert.That(runs[0]["Status"], Is.EqualTo(3));
        Assert.That(runs[0]["TriggerPayloadJson"] as string, Does.Contain("scheduledForLocal"));
    }

    [Test]
    public async Task T88_A_run_missed_while_WebApi_was_off_is_skipped_not_run_late()
    {
        await JobWhenAsync(World.SchedOkJob, j => j["NextRunAtUtc"] is DateTime, "first next-run time");
        var before = (await RunsAsync(World.SchedOkJob)).Count;

        await MakeDueAsync(World.SchedOkJob, minutesAgo: 120);
        var job = await JobWhenAsync(World.SchedOkJob, j => Status(j).StartsWith("Missed"), "the missed run noted");
        Assert.That(Status(job), Does.Contain("WebApi was not running"));
        Assert.That((DateTime)job["NextRunAtUtc"]!, Is.GreaterThan(DateTime.UtcNow));
        Assert.That(await RunsAsync(World.SchedOkJob), Has.Count.EqualTo(before), "a 2-hour-old run is not executed");
    }

    [Test]
    public async Task T89_A_failing_job_is_retried_then_staff_are_told()
    {
        await JobWhenAsync(World.SchedFailJob, j => j["NextRunAtUtc"] is DateTime, "first next-run time");
        var posCallsBefore = GlobalSetup.Fakes.PosAttempts.Count(a => a.ApiKey == "lowstock-down");

        await MakeDueAsync(World.SchedFailJob);
        var job = await JobWhenAsync(World.SchedFailJob, j => (int)j["RetryAttempt"]! == 1, "first retry booked");
        Assert.That(Status(job), Does.StartWith("Failed - retry 1 of 2"));
        Assert.That(GlobalSetup.Fakes.PosAttempts.Count(a => a.ApiKey == "lowstock-down") - posCallsBefore, Is.GreaterThanOrEqualTo(3),
            "inside one run, a stock read that gets 503 is tried 3 times");

        job = await JobWhenAsync(World.SchedFailJob, j => Status(j).StartsWith("Failed after 2 retries"), "giving up", 60000);
        Assert.That(job["RetryAttempt"], Is.EqualTo(0));
        Assert.That(((DateTime)job["NextRunAtUtc"]!).Hour, Is.EqualTo(22), "back on the normal schedule (06:30 Manila)");

        var runs = await RunsAsync(World.SchedFailJob);
        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs.Select(r => r["Status"]), Is.All.EqualTo(4));

        var mail = await Mailbox.WaitForAsync("owner@schedfail.test");
        Assert.That(mail[0].Body, Does.Contain("Manila low-stock check").And.Contain("failed 3 times"));
    }

    [Test]
    public async Task T90_A_paused_clients_scheduled_job_does_not_run()
    {
        await JobWhenAsync(World.SchedPausedJob, j => j["NextRunAtUtc"] is DateTime, "first next-run time");
        await MakeDueAsync(World.SchedPausedJob);
        var job = await JobWhenAsync(World.SchedPausedJob, j => Status(j).StartsWith("Skipped"), "skip noted");
        Assert.That(Status(job), Does.Contain("Paused"));
        Assert.That(await RunsAsync(World.SchedPausedJob), Is.Empty);
        Assert.That((DateTime)job["NextRunAtUtc"]!, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task T91_POS_down_order_is_kept_staff_told_and_can_be_sent_again()
    {
        var user = Line.NewUser("t91");
        await Line.SendAsync(World.PosDown, Line.TextEvent(user, "2 shawarma"));

        var order = (await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c", ("@c", user)),
            r => r.Count == 1 && r[0]["PosError"] is string, 20000, "order saved with the POS error"))[0];
        var reference = (string)order["Reference"]!;
        Assert.That(reference, Does.StartWith("ORD-"), "our own reference when the POS did not answer");
        Assert.That(order["PosOrderId"], Is.Null);
        Assert.That((string)order["PosError"]!, Does.Contain("503"));

        // 503 = the POS did not take it, so it is safe to try again: 3 tries, ONE idempotency key.
        var tries = GlobalSetup.Fakes.PosAttempts.Where(a => a.ApiKey == "pos-down-e2e" && a.Path == "/api/orders").ToList();
        Assert.That(tries, Has.Count.EqualTo(3));
        Assert.That(tries.Select(t => t.IdempotencyKey).Distinct().Count(), Is.EqualTo(1));
        Assert.That(tries[0].IdempotencyKey, Is.Not.Empty);

        // The customer still gets the confirmation.
        var reply = await Waits.LineCallsAsync(user);
        Assert.That(reply[0].Text, Does.Contain(reference));

        // Staff: LINE answered 503 once, the push was retried with the SAME retry key (LINE drops duplicates).
        var staff = await Eventually.GetAsync(
            () => Task.FromResult(GlobalSetup.Fakes.LineCalls.Where(c => c.To == World.PosDownStaffLineId && c.Text.Contains(reference)).ToList()),
            c => c.Count >= 2, 20000, "staff message retried");
        Assert.That(staff.Select(s => s.StatusReturned), Is.EqualTo(new[] { 503, 200 }));
        Assert.That(staff[0].RetryKey, Is.Not.Empty.And.EqualTo(staff[1].RetryKey));
        Assert.That(staff[1].Text, Does.Contain("could NOT be sent to your POS"));

        // Orders page: error + "Send to POS again". Still down -> error stays. Back up -> sent once.
        await SignInAsync();
        await Page.GotoAsync($"/Orders?clientConfigId={World.PosDown.Id}&q={reference}");
        await Expect(Page.Locator("[data-testid=order-pos-error]")).ToBeVisibleAsync();
        await Page.ClickAsync("[data-testid=order-send-to-pos]");
        await Expect(Page.Locator("[data-testid=order-pos-error]")).ToBeVisibleAsync();

        GlobalSetup.Fakes.RecoverPos("pos-down-e2e");
        await Page.ClickAsync("[data-testid=order-send-to-pos]");
        await Expect(Page.Locator("[data-testid=order-in-pos]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=order-pos-error]")).ToHaveCountAsync(0);

        var saved = (await Db.RowsAsync("SELECT PosOrderId, PosError FROM dbo.Orders WHERE CustomerId = @c", ("@c", user)))[0];
        Assert.That(saved["PosOrderId"] as string, Does.StartWith("POS-"));
        Assert.That(saved["PosError"], Is.Null);
        var inPos = GlobalSetup.Fakes.PosOrders.Where(o => o.ApiKey == "pos-down-e2e").ToList();
        Assert.That(inPos, Has.Count.EqualTo(1), "the POS got the order exactly once");
        Assert.That(inPos[0].BodyJson, Does.Contain(((Guid)order["Id"]!).ToString("N")));
    }

    [Test]
    public async Task T92_Schedule_form_days_times_cron_in_the_clients_time_zone()
    {
        await SignInAsync();
        await Page.GotoAsync($"/Clients/EditWorkflow/{World.SchedFormJob}");
        await Expect(Page.Locator("[data-testid=schedule-group]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=schedule-tz]")).ToHaveTextAsync("Asia/Dubai");
        await Expect(Page.Locator("[data-testid=schedule-times]")).ToHaveValueAsync("07:00");
        await Expect(Page.Locator("[data-testid=schedule-next]")).ToContainTextAsync("disabled");

        // bad time, bad cron
        await Page.FillAsync("[data-testid=schedule-times]", "25:00");
        await Page.ClickAsync("[data-testid=save-workflow]");
        await Expect(Page.Locator("[data-testid=error-ScheduleTimes]")).ToContainTextAsync("is not a time");

        await Page.FillAsync("[data-testid=schedule-times]", "");
        await Page.ClickAsync("#scheduleGroup summary");
        await Page.FillAsync("[data-testid=schedule-cron]", "61 * * * *");
        await Page.ClickAsync("[data-testid=save-workflow]");
        await Expect(Page.Locator("[data-testid=error-ScheduleCron]")).ToContainTextAsync("61");

        // Mon-Fri at 08:15 and 16:00, enabled
        await Page.FillAsync("[data-testid=schedule-cron]", "");
        for (var d = 0; d < 7; d++)
        {
            var box = Page.Locator($"[data-testid=schedule-day-{d}]");
            if (d is 0 or 6) await box.UncheckAsync(); else await box.CheckAsync();
        }
        await Page.FillAsync("[data-testid=schedule-times]", "8:15, 16:00");
        await Page.CheckAsync("#IsEnabled");
        await Page.ClickAsync("[data-testid=save-workflow]");

        await Expect(Page).ToHaveURLAsync(new Regex($"/Clients/Details/{World.SchedForm.Id}", RegexOptions.IgnoreCase));
        var row = Page.Locator("[data-testid=workflow-schedule]").First;
        await Expect(row).ToContainTextAsync("Mon-Fri at 08:15 and 16:00");
        await Expect(row).ToContainTextAsync("Asia/Dubai");

        var job = await JobWhenAsync(World.SchedFormJob, j => j["NextRunAtUtc"] is DateTime, "next run worked out");
        var next = (DateTime)job["NextRunAtUtc"]!;
        Assert.That((next.Hour, next.Minute), Is.AnyOf((4, 15), (12, 0)), "08:15 / 16:00 Dubai = 04:15 / 12:00 UTC");
        Assert.That(next.DayOfWeek, Is.Not.EqualTo(DayOfWeek.Saturday).And.Not.EqualTo(DayOfWeek.Sunday));
        await Expect(Page.Locator("[data-testid=workflow-next-run]").First).ToContainTextAsync(new Regex("08:15|16:00"));

        // A new workflow: picking the low-stock handler picks "Scheduled" and shows the box in the client's zone.
        await Page.GotoAsync($"/Clients/CreateWorkflow?clientConfigId={World.SchedForm.Id}");
        await Page.SelectOptionAsync("#HandlerKey", "low-stock-alert");
        await Expect(Page.Locator("#TriggerType")).ToHaveValueAsync("2");
        await Expect(Page.Locator("[data-testid=schedule-group]")).ToBeVisibleAsync();
        await Page.SelectOptionAsync("#TriggerType", "1");
        await Expect(Page.Locator("[data-testid=schedule-group]")).ToBeHiddenAsync();
        await Page.SelectOptionAsync("#TriggerType", "2");
        await Page.FillAsync("#Name", "Dubai weekday cron");
        await Page.ClickAsync("#scheduleGroup summary");
        await Page.FillAsync("[data-testid=schedule-cron]", "0 9 * * 1-5");
        await Page.ClickAsync("[data-testid=save-workflow]");

        var created = await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT TriggerConfigJson, NextRunAtUtc FROM dbo.WorkflowDefinitions WHERE Name = 'Dubai weekday cron'"),
            r => r.Count == 1 && r[0]["NextRunAtUtc"] is DateTime, 15000, "cron workflow saved");
        Assert.That(created[0]["TriggerConfigJson"] as string, Does.Contain("0 9 * * 1-5"));
        Assert.That(((DateTime)created[0]["NextRunAtUtc"]!).Hour, Is.EqualTo(5), "09:00 Dubai = 05:00 UTC");
    }
}
