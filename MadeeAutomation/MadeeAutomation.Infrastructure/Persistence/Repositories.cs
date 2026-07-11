using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MadeeAutomation.Infrastructure.Persistence;

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
}

public class ExecutionLogRepository : IExecutionLogRepository
{
    private readonly AutomationDbContext _db;

    public ExecutionLogRepository(AutomationDbContext db) => _db = db;

    public async Task<ExecutionLog> StartAsync(WorkflowDefinition workflow, string triggerPayloadJson, CancellationToken cancellationToken = default)
    {
        var log = new ExecutionLog
        {
            WorkflowDefinitionId = workflow.Id,
            ClientConfigId = workflow.ClientConfigId,
            Status = ExecutionStatus.Running,
            TriggerPayloadJson = triggerPayloadJson,
            StartedAtUtc = DateTime.UtcNow
        };

        _db.ExecutionLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task CompleteAsync(Guid executionLogId, bool success, string? summary, string? error, CancellationToken cancellationToken = default)
    {
        var log = await _db.ExecutionLogs.FirstOrDefaultAsync(e => e.Id == executionLogId, cancellationToken);
        if (log is null) return;

        log.Status = success ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;
        log.ResultSummary = summary;
        log.ErrorMessage = error;
        log.CompletedAtUtc = DateTime.UtcNow;
        log.DurationMs = (long)(log.CompletedAtUtc.Value - log.StartedAtUtc).TotalMilliseconds;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ExecutionLog>> GetRecentAsync(Guid clientConfigId, int take = 50, CancellationToken cancellationToken = default) =>
        await _db.ExecutionLogs
            .Where(e => e.ClientConfigId == clientConfigId)
            .OrderByDescending(e => e.StartedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
}

public class CurrentClientService : ICurrentClientService
{
    private readonly AutomationDbContext _db;

    public CurrentClientService(AutomationDbContext db) => _db = db;

    public async Task<ClientConfig?> GetClientAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        await _db.ClientConfigs.FirstOrDefaultAsync(c => c.Id == clientConfigId, cancellationToken);

    public async Task<T?> GetIntegrationSettingAsync<T>(Guid clientConfigId, string key, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(clientConfigId, cancellationToken);
        if (client is null) return default;

        using var doc = System.Text.Json.JsonDocument.Parse(client.IntegrationSettingsJson);
        if (doc.RootElement.TryGetProperty(key, out var value))
        {
            return value.Deserialize<T>();
        }

        return default;
    }
}
