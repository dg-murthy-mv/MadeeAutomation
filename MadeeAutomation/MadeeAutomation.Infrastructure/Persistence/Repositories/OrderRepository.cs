using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

/// <summary>Orders the bot took. Every query is limited to one client.</summary>
public class OrderRepository : IOrderRepository
{
    private readonly AutomationDbContext _db;

    public OrderRepository(AutomationDbContext db) => _db = db;

    public async Task AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        var ownReference = string.IsNullOrWhiteSpace(order.Reference);
        foreach (var item in order.Items) item.OrderId = order.Id;

        // Two orders for the same client at the same moment could pick the same number;
        // the unique index refuses the second one and we simply take the next number.
        for (var attempt = 1; ; attempt++)
        {
            var last = await _db.Orders
                .Where(o => o.ClientConfigId == order.ClientConfigId)
                .MaxAsync(o => (int?)o.OrderNumber, cancellationToken) ?? 0;

            order.OrderNumber = last + 1;
            if (ownReference) order.Reference = Order.OwnReference(order.OrderNumber);

            try
            {
                _db.Orders.Add(order);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    public Task<Order?> GetAsync(Guid clientConfigId, Guid orderId, CancellationToken cancellationToken = default) =>
        _db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.ClientConfigId == clientConfigId, cancellationToken);

    public async Task<PagedResult<Order>> GetPagedAsync(Guid clientConfigId, IReadOnlyCollection<OrderStatus>? statuses, int page, int pageSize,
        string? search = null, DateOnly? orderDate = null, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        var query = _db.Orders.AsNoTracking().Where(o => o.ClientConfigId == clientConfigId);
        if (statuses is { Count: > 0 })
        {
            // At most two statuses are ever asked for - plain comparisons keep the SQL simple on every SQL Server version.
            var first = statuses.First();
            var second = statuses.Last();
            query = query.Where(o => o.Status == first || o.Status == second);
        }

        if (orderDate is { } day) query = query.Where(o => o.OrderDate == day);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(o =>
                o.Reference.Contains(term) ||
                (o.CustomerName != null && o.CustomerName.Contains(term)) ||
                (o.CustomerMessage != null && o.CustomerMessage.Contains(term)) ||
                o.Items.Any(i => i.Name.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        // Asking for a page past the end (e.g. after a filter) shows the last page instead of nothing.
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        if (page > lastPage) page = lastPage;

        var items = await query
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Order> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public Task<int> CountAsync(Guid clientConfigId, OrderStatus status, CancellationToken cancellationToken = default) =>
        _db.Orders.CountAsync(o => o.ClientConfigId == clientConfigId && o.Status == status, cancellationToken);

    public async Task<Dictionary<Guid, int>> GetOrderedQuantitiesAsync(Guid clientConfigId, DateOnly orderDate, CancellationToken cancellationToken = default)
    {
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.MenuItemId != null)
            .Join(_db.Orders.Where(o => o.ClientConfigId == clientConfigId && o.OrderDate == orderDate && o.Status != OrderStatus.Cancelled),
                i => i.OrderId, o => o.Id, (i, o) => new { MenuItemId = i.MenuItemId!.Value, i.Quantity })
            .GroupBy(x => x.MenuItemId)
            .Select(g => new { MenuItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.MenuItemId, r => r.Quantity);
    }

    public async Task SetCustomerNoticeAsync(Guid clientConfigId, Guid orderId, bool sent, string note, CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.ClientConfigId == clientConfigId, cancellationToken);
        if (order is null) return;

        order.CustomerNoticeSent = sent;
        order.CustomerNoticeNote = note.Length > 500 ? note[..500] : note;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetStatusAsync(Guid clientConfigId, Guid orderId, OrderStatus status, string changedBy, string? cancelReason = null,
        CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.ClientConfigId == clientConfigId, cancellationToken);
        if (order is null) return false;

        order.Status = status;
        order.StatusChangedBy = changedBy.Length > 200 ? changedBy[..200] : changedBy;
        order.StatusChangedAtUtc = DateTime.UtcNow;
        if (status == OrderStatus.Cancelled)
            order.CancelReason = string.IsNullOrWhiteSpace(cancelReason) ? null : cancelReason.Trim()[..Math.Min(cancelReason.Trim().Length, 300)];
        order.CustomerNoticeSent = null;   // the notice below (if any) is about THIS change
        order.CustomerNoticeNote = null;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
