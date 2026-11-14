using System.Data;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

/// <summary>
/// What the reports read. Deliberately plain: filters, column lists and simple per-client counts -
/// the adding-up by day, hour and item happens in ReportBuilder, in each client's own time zone.
/// Everything is read without tracking.
/// </summary>
public class ReportRepository : IReportRepository
{
    private readonly AutomationDbContext _db;

    public ReportRepository(AutomationDbContext db) => _db = db;

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public async Task<List<MessageRow>> GetMessagesAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var rows = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.ClientConfigId == clientConfigId && e.ReceivedAtUtc >= fromUtc && e.ReceivedAtUtc < toUtc)
            .Select(e => new { e.Channel, e.CustomerId, e.Kind, e.ReceivedAtUtc })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new MessageRow(r.Channel, r.CustomerId, r.Kind, Utc(r.ReceivedAtUtc))).ToList();
    }

    public async Task<HashSet<string>> GetCustomerKeysBeforeAsync(Guid clientConfigId, DateTime beforeUtc, CancellationToken cancellationToken = default)
    {
        var rows = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.ClientConfigId == clientConfigId && e.ReceivedAtUtc < beforeUtc && e.CustomerId != null)
            .Select(e => new { e.Channel, e.CustomerId })
            .Distinct()
            .ToListAsync(cancellationToken);
        return rows.Select(r => r.Channel + ":" + r.CustomerId).ToHashSet();
    }

    public Task<List<Order>> GetOrdersAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        _db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.ClientConfigId == clientConfigId && o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc < toUtc)
            .OrderBy(o => o.CreatedAtUtc)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public Task<List<Booking>> GetBookingsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking()
            .Where(b => b.ClientConfigId == clientConfigId &&
                        ((b.StartUtc >= fromUtc && b.StartUtc < toUtc) || (b.CreatedAtUtc >= fromUtc && b.CreatedAtUtc < toUtc)))
            .OrderBy(b => b.StartUtc)
            .ToListAsync(cancellationToken);

    public async Task<List<RunRow>> GetRunsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var rows = await _db.ExecutionLogs.AsNoTracking()
            .Where(l => l.ClientConfigId == clientConfigId && l.StartedAtUtc >= fromUtc && l.StartedAtUtc < toUtc)
            .Select(l => new
            {
                l.Status,
                l.StartedAtUtc,
                // Only the runs that told someone something need their actions read.
                Actions = l.ActionsJson != null && l.ActionsJson.Contains("NotifyInternal") ? l.ActionsJson : null
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new RunRow(r.Status, Utc(r.StartedAtUtc), r.Actions)).ToList();
    }

    public async Task<List<ClientActivity>> GetClientActivityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, ClientActivity>();
        ClientActivity For(Guid id) => result.TryGetValue(id, out var a) ? a : result[id] = new ClientActivity { ClientConfigId = id };

        var messages = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.ReceivedAtUtc >= fromUtc && e.ReceivedAtUtc < toUtc && e.Kind != InboundKinds.Follow)
            .GroupBy(e => e.ClientConfigId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var m in messages) For(m.Key).Messages = m.Count;

        var customers = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.ReceivedAtUtc >= fromUtc && e.ReceivedAtUtc < toUtc && e.CustomerId != null && e.Kind != InboundKinds.Follow)
            .Select(e => new { e.ClientConfigId, e.Channel, e.CustomerId })
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var g in customers.GroupBy(c => c.ClientConfigId)) For(g.Key).Customers = g.Count();

        var last = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.Kind != InboundKinds.Follow)
            .GroupBy(e => e.ClientConfigId)
            .Select(g => new { g.Key, Last = g.Max(e => e.ReceivedAtUtc) })
            .ToListAsync(cancellationToken);
        foreach (var l in last) For(l.Key).LastMessageUtc = Utc(l.Last);

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc < toUtc && o.Status != OrderStatus.Cancelled)
            .GroupBy(o => o.ClientConfigId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var o in orders) For(o.Key).Orders = o.Count;

        var bookings = await _db.Bookings.AsNoTracking()
            .Where(b => b.CreatedAtUtc >= fromUtc && b.CreatedAtUtc < toUtc)
            .GroupBy(b => b.ClientConfigId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var b in bookings) For(b.Key).Bookings = b.Count;

        var runs = await _db.ExecutionLogs.AsNoTracking()
            .Where(l => l.StartedAtUtc >= fromUtc && l.StartedAtUtc < toUtc)
            .GroupBy(l => l.ClientConfigId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var r in runs) For(r.Key).Runs = r.Count;

        var failed = await _db.ExecutionLogs.AsNoTracking()
            .Where(l => l.StartedAtUtc >= fromUtc && l.StartedAtUtc < toUtc && l.Status == ExecutionStatus.Failed)
            .GroupBy(l => l.ClientConfigId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var f in failed) For(f.Key).FailedRuns = f.Count;

        var ai = await _db.AiUsage.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc && a.ClientConfigId != null)
            .GroupBy(a => a.ClientConfigId)
            .Select(g => new
            {
                g.Key,
                Count = g.Count(),
                Tokens = g.Sum(a => (long)a.InputTokens + a.OutputTokens + a.CacheReadTokens + a.CacheWriteTokens),
                Cost = g.Sum(a => a.CostUsd)
            })
            .ToListAsync(cancellationToken);
        foreach (var a in ai)
        {
            var row = For(a.Key!.Value);
            row.AiCalls = a.Count;
            row.AiTokens = a.Tokens;
            row.AiCostUsd = a.Cost;
        }

        return result.Values.ToList();
    }

    public async Task<Dictionary<DateOnly, int>> GetMessagesPerDayAsync(DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default)
    {
        var times = await _db.InboundEvents.AsNoTracking()
            .Where(e => e.ReceivedAtUtc >= fromUtc && e.ReceivedAtUtc < toUtc && e.Kind != InboundKinds.Follow)
            .Select(e => e.ReceivedAtUtc)
            .ToListAsync(cancellationToken);
        return times.GroupBy(t => DateOnly.FromDateTime(t.AddMinutes(offsetMinutes))).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<Dictionary<DateOnly, decimal>> GetAiCostPerDayAsync(DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AiUsage.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc)
            .Select(a => new { a.CreatedAtUtc, a.CostUsd })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => DateOnly.FromDateTime(r.CreatedAtUtc.AddMinutes(offsetMinutes))).ToDictionary(g => g.Key, g => g.Sum(r => r.CostUsd));
    }

    public async Task<List<AiPurposeTotal>> GetAiByPurposeAsync(Guid? clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AiUsage.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc && (clientConfigId == null || a.ClientConfigId == clientConfigId))
            .GroupBy(a => new { a.Purpose, a.IsTest })
            .Select(g => new
            {
                g.Key.Purpose,
                g.Key.IsTest,
                Count = g.Count(),
                Input = g.Sum(a => (long)a.InputTokens + a.CacheReadTokens + a.CacheWriteTokens),
                Output = g.Sum(a => (long)a.OutputTokens),
                Cost = g.Sum(a => a.CostUsd)
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new AiPurposeTotal(r.Purpose, r.IsTest, r.Count, r.Input, r.Output, r.Cost)).ToList();
    }

    public Task<List<ClientConfig>> GetMonthlyReportClientsAsync(CancellationToken cancellationToken = default) =>
        _db.ClientConfigs.AsNoTracking()
            .Include(c => c.Workflows)
            .Where(c => c.MonthlyReportEnabled && c.StaffAlertEmail != null && c.StaffAlertEmail != "" &&
                        (c.Status == ClientStatus.Active || c.Status == ClientStatus.Trial))
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimMonthlyReportAsync(Guid clientConfigId, string month, CancellationToken cancellationToken = default) =>
        await _db.ClientConfigs
            .Where(c => c.Id == clientConfigId && (c.MonthlyReportSentFor == null || c.MonthlyReportSentFor != month))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MonthlyReportSentFor, month), cancellationToken) == 1;

    public Task ReleaseMonthlyReportAsync(Guid clientConfigId, string month, string? previousMonth, CancellationToken cancellationToken = default) =>
        _db.ClientConfigs
            .Where(c => c.Id == clientConfigId && c.MonthlyReportSentFor == month)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MonthlyReportSentFor, previousMonth), cancellationToken);
}

/// <summary>
/// Saves AI usage with a plain INSERT, so it never touches (or saves) anything else the handler has
/// pending in the same database context. A failure is logged and swallowed.
/// </summary>
public class AiUsageRecorder : IAiUsageRecorder
{
    private readonly AutomationDbContext _db;
    private readonly ILogger<AiUsageRecorder> _logger;

    public AiUsageRecorder(AutomationDbContext db, ILogger<AiUsageRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAsync(AiUsageRecord r, CancellationToken cancellationToken = default)
    {
        try
        {
            var purpose = r.Purpose.Length > 50 ? r.Purpose[..50] : r.Purpose;
            var model = r.Model.Length > 100 ? r.Model[..100] : r.Model;
            var at = DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc);
            // Explicit parameters: a null client id goes in as SQL NULL.
            var parameters = new[]
            {
                new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = r.Id },
                new SqlParameter("@client", SqlDbType.UniqueIdentifier) { Value = (object?)r.ClientConfigId ?? DBNull.Value },
                new SqlParameter("@purpose", SqlDbType.NVarChar, 50) { Value = purpose },
                new SqlParameter("@model", SqlDbType.NVarChar, 100) { Value = model },
                new SqlParameter("@in", SqlDbType.Int) { Value = r.InputTokens },
                new SqlParameter("@out", SqlDbType.Int) { Value = r.OutputTokens },
                new SqlParameter("@cacheRead", SqlDbType.Int) { Value = r.CacheReadTokens },
                new SqlParameter("@cacheWrite", SqlDbType.Int) { Value = r.CacheWriteTokens },
                new SqlParameter("@cost", SqlDbType.Decimal) { Precision = 18, Scale = 6, Value = r.CostUsd },
                new SqlParameter("@test", SqlDbType.Bit) { Value = r.IsTest },
                new SqlParameter("@at", SqlDbType.DateTime2) { Value = at }
            };
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.AiUsage (Id, ClientConfigId, Purpose, Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, CostUsd, IsTest, CreatedAtUtc) " +
                "VALUES (@id, @client, @purpose, @model, @in, @out, @cacheRead, @cacheWrite, @cost, @test, @at)",
                parameters, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI usage could not be saved");
        }
    }
}
