using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class InboundEventRepository : IInboundEventRepository
{
    private readonly AutomationDbContext _db;

    public InboundEventRepository(AutomationDbContext db) => _db = db;

    public async Task<bool> TryAddAsync(InboundEvent inboundEvent, CancellationToken cancellationToken = default)
    {
        var exists = await _db.InboundEvents.AnyAsync(e =>
            e.ClientConfigId == inboundEvent.ClientConfigId &&
            e.Channel == inboundEvent.Channel &&
            e.ExternalEventId == inboundEvent.ExternalEventId, cancellationToken);
        if (exists) return false;

        _db.InboundEvents.Add(inboundEvent);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Two deliveries of the same event arrived at the same moment - the unique index kept one.
            _db.Entry(inboundEvent).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<List<Guid>> GetPendingIdsAsync(int take, CancellationToken cancellationToken = default) =>
        await _db.InboundEvents
            .AsNoTracking()
            .Where(e => e.Status == InboundEventStatus.Pending)
            .OrderBy(e => e.ReceivedAtUtc)
            .Select(e => e.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimAsync(Guid inboundEventId, CancellationToken cancellationToken = default)
    {
        var updated = await _db.InboundEvents
            .Where(e => e.Id == inboundEventId && e.Status == InboundEventStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, InboundEventStatus.Processing)
                .SetProperty(e => e.Attempts, e => e.Attempts + 1), cancellationToken);
        return updated == 1;
    }

    public async Task<InboundEvent?> GetByIdAsync(Guid inboundEventId, CancellationToken cancellationToken = default) =>
        await _db.InboundEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == inboundEventId, cancellationToken);

    public async Task MarkAsync(Guid inboundEventId, InboundEventStatus status, string? error, Guid? executionLogId,
        CancellationToken cancellationToken = default)
    {
        var trimmedError = error is { Length: > 2000 } ? error[..2000] : error;
        var now = DateTime.UtcNow;
        await _db.InboundEvents
            .Where(e => e.Id == inboundEventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.LastError, trimmedError)
                .SetProperty(e => e.ExecutionLogId, executionLogId)
                .SetProperty(e => e.ProcessedAtUtc, now), cancellationToken);
    }

    public async Task<int> FailStuckAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var now = DateTime.UtcNow;
        return await _db.InboundEvents
            .Where(e => e.Status == InboundEventStatus.Processing && e.ReceivedAtUtc < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, InboundEventStatus.Failed)
                .SetProperty(e => e.LastError, "Interrupted (the app stopped while this event was running). Not re-run, to avoid duplicate orders.")
                .SetProperty(e => e.ProcessedAtUtc, now), cancellationToken);
    }
}
