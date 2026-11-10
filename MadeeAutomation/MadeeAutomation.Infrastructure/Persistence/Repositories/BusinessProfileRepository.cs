using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

/// <summary>A client's menu, opening hours and closed dates.</summary>
public class BusinessProfileRepository : IBusinessProfileRepository
{
    private readonly AutomationDbContext _db;

    public BusinessProfileRepository(AutomationDbContext db) => _db = db;

    // ---- Menu ----

    public Task<List<MenuItem>> GetMenuAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        _db.MenuItems.AsNoTracking()
            .Where(m => m.ClientConfigId == clientConfigId)
            .OrderBy(m => m.Category).ThenBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);

    public Task<MenuItem?> GetMenuItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.MenuItems.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<bool> MenuNameExistsAsync(Guid clientConfigId, string name, Guid? exceptId = null, CancellationToken cancellationToken = default)
    {
        var n = (name ?? "").Trim();
        return _db.MenuItems.AnyAsync(m => m.ClientConfigId == clientConfigId && m.Name == n && (exceptId == null || m.Id != exceptId), cancellationToken);
    }

    public async Task AddMenuItemsAsync(IEnumerable<MenuItem> items, CancellationToken cancellationToken = default)
    {
        _db.MenuItems.AddRange(items);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMenuItemAsync(MenuItem item, CancellationToken cancellationToken = default)
    {
        item.UpdatedAtUtc = DateTime.UtcNow;
        if (_db.Entry(item).State == EntityState.Detached) _db.MenuItems.Update(item);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteMenuItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.MenuItems.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken);

    // ---- Opening hours ----

    public Task<List<OpeningHours>> GetHoursAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        _db.OpeningHours.AsNoTracking()
            .Where(h => h.ClientConfigId == clientConfigId)
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpenTime)
            .ToListAsync(cancellationToken);

    public async Task ReplaceHoursAsync(Guid clientConfigId, IReadOnlyCollection<OpeningHours> hours, CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.OpeningHours.Where(h => h.ClientConfigId == clientConfigId).ExecuteDeleteAsync(cancellationToken);
        foreach (var h in hours) h.ClientConfigId = clientConfigId;
        _db.OpeningHours.AddRange(hours);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // ---- Closed dates ----

    public Task<List<ClosedDate>> GetClosedDatesAsync(Guid clientConfigId, DateOnly fromDate, CancellationToken cancellationToken = default) =>
        _db.ClosedDates.AsNoTracking()
            .Where(c => c.ClientConfigId == clientConfigId && c.Date >= fromDate)
            .OrderBy(c => c.Date)
            .ToListAsync(cancellationToken);

    public async Task<bool> AddClosedDateAsync(ClosedDate closedDate, CancellationToken cancellationToken = default)
    {
        if (await _db.ClosedDates.AnyAsync(c => c.ClientConfigId == closedDate.ClientConfigId && c.Date == closedDate.Date, cancellationToken))
            return false;
        _db.ClosedDates.Add(closedDate);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task DeleteClosedDateAsync(Guid clientConfigId, Guid id, CancellationToken cancellationToken = default) =>
        await _db.ClosedDates.Where(c => c.Id == id && c.ClientConfigId == clientConfigId).ExecuteDeleteAsync(cancellationToken);
}
