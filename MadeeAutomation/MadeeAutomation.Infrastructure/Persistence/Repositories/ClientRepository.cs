using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class ClientRepository : IClientRepository, ICurrentClientService
{
    private readonly AutomationDbContext _db;

    public ClientRepository(AutomationDbContext db) => _db = db;

    // ---- ICurrentClientService ----

    public async Task<ClientConfig?> GetClientAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        await _db.ClientConfigs.FirstOrDefaultAsync(c => c.Id == clientConfigId, cancellationToken);

    public async Task<ClientConfig?> GetByCodeAsync(string clientCode, CancellationToken cancellationToken = default)
    {
        var code = (clientCode ?? "").Trim().ToLowerInvariant();
        if (code.Length == 0) return null;
        return await _db.ClientConfigs.FirstOrDefaultAsync(c => c.ClientCode == code, cancellationToken);
    }

    // ---- IClientRepository (Admin) ----

    public async Task<PagedResult<ClientConfig>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        var query = _db.ClientConfigs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.ClientName.Contains(search) || c.ClientCode.Contains(search));

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(c => c.Workflows)
            .OrderBy(c => c.ClientName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ClientConfig> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<ClientConfig?> GetWithWorkflowsAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        await _db.ClientConfigs
            .Include(c => c.Workflows)
            .FirstOrDefaultAsync(c => c.Id == clientConfigId, cancellationToken);

    public async Task<bool> CodeExistsAsync(string clientCode, Guid? exceptClientId = null, CancellationToken cancellationToken = default)
    {
        var code = (clientCode ?? "").Trim().ToLowerInvariant();
        return await _db.ClientConfigs.AnyAsync(c => c.ClientCode == code && (exceptClientId == null || c.Id != exceptClientId), cancellationToken);
    }

    public async Task AddAsync(ClientConfig client, CancellationToken cancellationToken = default)
    {
        _db.ClientConfigs.Add(client);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ClientConfig client, CancellationToken cancellationToken = default)
    {
        client.UpdatedAtUtc = DateTime.UtcNow;
        if (_db.Entry(client).State == EntityState.Detached) _db.ClientConfigs.Update(client);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
