using System.Data;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class SubscriptionRepository : ISubscriptionRepository
{
    private readonly AutomationDbContext _db;

    public SubscriptionRepository(AutomationDbContext db) => _db = db;

    public Task<int> CountChatsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default) =>
        _db.InboundEvents.AsNoTracking()
            .Where(e => e.ClientConfigId == clientConfigId && e.ReceivedAtUtc >= fromUtc && e.ReceivedAtUtc < toUtc &&
                        e.CustomerId != null && (e.Kind == InboundKinds.Text || e.Kind == InboundKinds.Tap))
            .Select(e => new { e.CustomerId, Day = e.ReceivedAtUtc.AddMinutes(offsetMinutes).Date })
            .Distinct()
            .CountAsync(cancellationToken);

    public Task<List<ClientConfig>> GetClientsAsync(CancellationToken cancellationToken = default) =>
        _db.ClientConfigs.AsNoTracking()
            .Include(c => c.Workflows)
            .Where(c => c.Status != ClientStatus.Cancelled)
            .OrderBy(c => c.ClientName)
            .ToListAsync(cancellationToken);

    public Task SetPlanAsync(Guid clientConfigId, PlanTier plan, BillingCycle? cycle, DateTime? endsAtUtc, decimal? price, string? note,
        CancellationToken cancellationToken = default) =>
        _db.ClientConfigs
            .Where(c => c.Id == clientConfigId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Plan, plan)
                .SetProperty(c => c.BillingCycle, cycle)
                .SetProperty(c => c.PlanEndsAtUtc, endsAtUtc)
                .SetProperty(c => c.PlanPrice, price)
                .SetProperty(c => c.PlanNote, note)
                .SetProperty(c => c.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);

    public async Task AddPaymentAsync(SubscriptionPayment payment, decimal? agreedPrice, CancellationToken cancellationToken = default)
    {
        _db.SubscriptionPayments.Add(payment);
        await _db.SaveChangesAsync(cancellationToken);

        var plan = payment.Plan;
        BillingCycle? cycle = payment.Cycle;
        var end = payment.PeriodToUtc;
        await _db.ClientConfigs
            .Where(c => c.Id == payment.ClientConfigId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Plan, plan)
                .SetProperty(c => c.BillingCycle, cycle)
                .SetProperty(c => c.PlanEndsAtUtc, end)
                .SetProperty(c => c.PlanPrice, agreedPrice)
                .SetProperty(c => c.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);

        // A paying trial client becomes Active (a paused or cancelled client stays as staff set it).
        await _db.ClientConfigs
            .Where(c => c.Id == payment.ClientConfigId && c.Status == ClientStatus.Trial)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ClientStatus.Active), cancellationToken);
    }

    public Task<List<SubscriptionPayment>> GetPaymentsAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.ClientConfigId == clientConfigId)
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task<List<SubscriptionPayment>> GetPaymentsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.PaidOn >= from && p.PaidOn <= to)
            .OrderByDescending(p => p.PaidOn)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimNoticeAsync(Guid clientConfigId, string noticeKey, CancellationToken cancellationToken = default)
    {
        var key = noticeKey.Length > 200 ? noticeKey[..200] : noticeKey;
        var parameters = new[]
        {
            new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() },
            new SqlParameter("@client", SqlDbType.UniqueIdentifier) { Value = clientConfigId },
            new SqlParameter("@key", SqlDbType.NVarChar, 200) { Value = key },
            new SqlParameter("@at", SqlDbType.DateTime2) { Value = DateTime.UtcNow }
        };
        try
        {
            // The unique index on (ClientConfigId, NoticeKey) settles a race between two WebApi copies.
            return await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.SubscriptionNotices (Id, ClientConfigId, NoticeKey, CreatedAtUtc) " +
                "SELECT @id, @client, @key, @at " +
                "WHERE NOT EXISTS (SELECT 1 FROM dbo.SubscriptionNotices WHERE ClientConfigId = @client AND NoticeKey = @key)",
                parameters, cancellationToken) == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;
        }
    }

    public Task ReleaseNoticeAsync(Guid clientConfigId, string noticeKey, CancellationToken cancellationToken = default)
    {
        var key = noticeKey.Length > 200 ? noticeKey[..200] : noticeKey;
        return _db.SubscriptionNotices
            .Where(n => n.ClientConfigId == clientConfigId && n.NoticeKey == key)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
