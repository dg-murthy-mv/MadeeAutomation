using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class ExecutionLogRepository : IExecutionLogRepository
{
    private readonly AutomationDbContext _db;

    public ExecutionLogRepository(AutomationDbContext db) => _db = db;

    public async Task<ExecutionLog> StartAsync(WorkflowDefinition workflow, TriggerContext context, CancellationToken cancellationToken = default)
    {
        var log = new ExecutionLog
        {
            WorkflowDefinitionId = workflow.Id,
            ClientConfigId = workflow.ClientConfigId,
            InboundEventId = context.InboundEventId,
            Channel = context.Channel,
            CustomerId = context.CustomerId,
            Status = ExecutionStatus.Running,
            TriggerPayloadJson = context.PayloadJson,
            StartedAtUtc = DateTime.UtcNow
        };

        _db.ExecutionLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task CompleteAsync(Guid executionLogId, ExecutionStatus status, string? summary, string? error, string? actionsJson,
        CancellationToken cancellationToken = default)
    {
        var log = await _db.ExecutionLogs.FirstOrDefaultAsync(e => e.Id == executionLogId, cancellationToken);
        if (log is null) return;

        log.Status = status;
        log.ResultSummary = summary;
        log.ErrorMessage = error;
        log.ActionsJson = actionsJson;
        log.CompletedAtUtc = DateTime.UtcNow;
        log.DurationMs = (long)(log.CompletedAtUtc.Value - log.StartedAtUtc).TotalMilliseconds;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ExecutionLog>> GetRecentAsync(Guid clientConfigId, int take = 50, CancellationToken cancellationToken = default) =>
        await _db.ExecutionLogs
            .AsNoTracking()
            .Where(e => e.ClientConfigId == clientConfigId)
            .OrderByDescending(e => e.StartedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<ExecutionLog?> GetByIdAsync(Guid executionLogId, CancellationToken cancellationToken = default) =>
        await _db.ExecutionLogs
            .AsNoTracking()
            .Include(e => e.WorkflowDefinition)
            .FirstOrDefaultAsync(e => e.Id == executionLogId, cancellationToken);

    public async Task<PagedResult<ExecutionLog>> GetPagedAsync(
        Guid clientConfigId, int page, int pageSize, string? statusFilter = null, string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        var query = _db.ExecutionLogs.AsNoTracking().Where(e => e.ClientConfigId == clientConfigId);

        if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<ExecutionStatus>(statusFilter, true, out var statusEnum))
        {
            query = query.Where(e => e.Status == statusEnum);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(e =>
                (e.ResultSummary != null && e.ResultSummary.Contains(search)) ||
                (e.ErrorMessage != null && e.ErrorMessage.Contains(search)) ||
                (e.CustomerId != null && e.CustomerId.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Include(e => e.WorkflowDefinition)
            .OrderByDescending(e => e.StartedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ExecutionLog> { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }
}
