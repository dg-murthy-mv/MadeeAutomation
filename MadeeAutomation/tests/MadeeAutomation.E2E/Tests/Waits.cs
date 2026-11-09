using MadeeAutomation.E2E.Infrastructure;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Database reads the tests wait on (the background worker writes these).</summary>
public static class Waits
{
    private static TestDatabase Db => GlobalSetup.Db;

    /// <summary>The finished ExecutionLog for this customer (CompletedAtUtc set).</summary>
    public static async Task<Dictionary<string, object?>> FinishedLogAsync(string customerId, int timeoutMs = 15000)
    {
        var rows = await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.ExecutionLogs WHERE CustomerId = @c AND CompletedAtUtc IS NOT NULL", ("@c", customerId)),
            r => r.Count > 0, timeoutMs, $"execution log for {customerId}");
        return rows[0];
    }

    public static Task<List<Dictionary<string, object?>>> LogsAsync(string customerId) =>
        Db.RowsAsync("SELECT * FROM dbo.ExecutionLogs WHERE CustomerId = @c", ("@c", customerId));

    /// <summary>The InboundEvent for this customer once the worker is done with it (not Pending/Processing).</summary>
    public static async Task<Dictionary<string, object?>> SettledEventAsync(string customerId, int timeoutMs = 15000)
    {
        var rows = await Eventually.GetAsync(
            () => Db.RowsAsync("SELECT * FROM dbo.InboundEvents WHERE CustomerId = @c AND Status NOT IN (1, 2)", ("@c", customerId)),
            r => r.Count > 0, timeoutMs, $"inbound event for {customerId}");
        return rows[0];
    }

    public static Task<List<Dictionary<string, object?>>> EventsAsync(string customerId) =>
        Db.RowsAsync("SELECT * FROM dbo.InboundEvents WHERE CustomerId = @c", ("@c", customerId));

    /// <summary>Waits until the fake LINE server has received at least `count` calls for this user.</summary>
    public static Task<IReadOnlyList<LineCall>> LineCallsAsync(string userId, int count = 1, int timeoutMs = 15000) =>
        Eventually.GetAsync(() => GlobalSetup.Fakes.LineCallsFor(userId), c => c.Count >= count, timeoutMs, $"{count} LINE call(s) for {userId}");

    /// <summary>Gives the worker time to (wrongly) do something, for "nothing happened" assertions.</summary>
    public static Task QuietPeriodAsync() => Task.Delay(2500);
}
