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
}
