using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class WorkflowRepository : IWorkflowRepository
{
    private readonly AutomationDbContext _db;

    public WorkflowRepository(AutomationDbContext db) => _db = db;

    public async Task<List<WorkflowDefinition>> GetActiveWorkflowsAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        await _db.WorkflowDefinitions
            .Where(w => w.ClientConfigId == clientConfigId && w.IsEnabled)
            .ToListAsync(cancellationToken);

    public async Task<WorkflowDefinition?> GetByIdAsync(Guid workflowDefinitionId, CancellationToken cancellationToken = default) =>
        await _db.WorkflowDefinitions
            .Include(w => w.ClientConfig)
            .FirstOrDefaultAsync(w => w.Id == workflowDefinitionId, cancellationToken);

    public async Task<List<WorkflowDefinition>> GetEnabledMessageWorkflowsAsync(Guid clientConfigId, string channel,
        CancellationToken cancellationToken = default)
    {
        var matches = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => w.ClientConfigId == clientConfigId
                        && w.IsEnabled
                        && w.TriggerType == TriggerType.MessageReceived
                        && (w.Channel == channel || w.Channel == null))
            .OrderBy(w => w.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // Exact channel match wins over an "any channel" workflow.
        return matches.OrderBy(w => w.Channel == null ? 1 : 0).ToList();
    }

    public async Task AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        workflow.UpdatedAtUtc = DateTime.UtcNow;
        if (_db.Entry(workflow).State == EntityState.Detached) _db.WorkflowDefinitions.Update(workflow);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetEnabledAsync(Guid workflowDefinitionId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var workflow = await _db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == workflowDefinitionId, cancellationToken);
        if (workflow is null) return false;
        workflow.IsEnabled = isEnabled;
        workflow.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<Guid>> GetDueScheduledIdsAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default) =>
        await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.IsEnabled && w.TriggerType == TriggerType.Scheduled && (w.NextRunAtUtc == null || w.NextRunAtUtc <= utcNow))
            .OrderBy(w => w.NextRunAtUtc)
            .Select(w => w.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimRunAsync(Guid workflowDefinitionId, DateTime? expectedNextUtc, DateTime? newNextUtc,
        CancellationToken cancellationToken = default)
    {
        // One UPDATE with the old value in the WHERE: if two copies of WebApi try at once, only one row changes.
        var query = _db.WorkflowDefinitions.Where(w => w.Id == workflowDefinitionId);
        query = expectedNextUtc is { } expected
            ? query.Where(w => w.NextRunAtUtc == expected)
            : query.Where(w => w.NextRunAtUtc == null);
        var changed = await query.ExecuteUpdateAsync(set => set.SetProperty(w => w.NextRunAtUtc, newNextUtc), cancellationToken);
        return changed == 1;
    }

    public async Task RecordRunAsync(Guid workflowDefinitionId, DateTime? lastRunAtUtc, string status, DateTime? nextRunAtUtc, int retryAttempt,
        CancellationToken cancellationToken = default)
    {
        status = status.Length > 300 ? status[..300] : status;
        if (lastRunAtUtc is { } ran)
            await _db.WorkflowDefinitions.Where(w => w.Id == workflowDefinitionId).ExecuteUpdateAsync(set => set
                .SetProperty(w => w.LastRunAtUtc, ran)
                .SetProperty(w => w.LastRunStatus, status)
                .SetProperty(w => w.NextRunAtUtc, nextRunAtUtc)
                .SetProperty(w => w.RetryAttempt, retryAttempt), cancellationToken);
        else
            await _db.WorkflowDefinitions.Where(w => w.Id == workflowDefinitionId).ExecuteUpdateAsync(set => set
                .SetProperty(w => w.LastRunStatus, status)
                .SetProperty(w => w.NextRunAtUtc, nextRunAtUtc)
                .SetProperty(w => w.RetryAttempt, retryAttempt), cancellationToken);
    }
}
