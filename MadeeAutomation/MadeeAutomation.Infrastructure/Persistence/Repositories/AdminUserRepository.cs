using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class AdminUserRepository : IAdminUserRepository
{
    private readonly AutomationDbContext _db;

    public AdminUserRepository(AutomationDbContext db) => _db = db;

    private static string Normalize(string email) => (email ?? "").Trim().ToLowerInvariant();

    public Task<int> CountAsync(CancellationToken cancellationToken = default) => _db.AdminUsers.CountAsync(cancellationToken);

    public Task<AdminUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<AdminUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(email);
        return _db.AdminUsers.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
    }

    public Task<List<AdminUser>> ListAsync(CancellationToken cancellationToken = default) =>
        _db.AdminUsers.AsNoTracking().Where(u => u.ClientConfigId == null).OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);

    public Task<List<AdminUser>> ListForClientAsync(Guid clientConfigId, CancellationToken cancellationToken = default) =>
        _db.AdminUsers.AsNoTracking().Where(u => u.ClientConfigId == clientConfigId).OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);

    public async Task AddAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        user.Email = Normalize(user.Email);
        _db.AdminUsers.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        user.UpdatedAtUtc = DateTime.UtcNow;
        if (_db.Entry(user).State == EntityState.Detached) _db.AdminUsers.Update(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddResetTokenAsync(PasswordResetToken token, CancellationToken cancellationToken = default)
    {
        _db.PasswordResetTokens.Add(token);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PasswordResetToken?> GetResetTokenAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task UseAllResetTokensAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _db.PasswordResetTokens
            .Where(t => t.AdminUserId == adminUserId && t.UsedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), cancellationToken);
    }
}
