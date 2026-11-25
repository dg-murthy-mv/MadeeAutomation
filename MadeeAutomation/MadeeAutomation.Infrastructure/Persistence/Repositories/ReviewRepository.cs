using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

public class ReviewRepository : IReviewRepository
{
    private readonly AutomationDbContext _db;

    public ReviewRepository(AutomationDbContext db) => _db = db;

    public Task SaveLinksAsync(Guid clientConfigId, string? googleUrl, string? facebookUrl, bool enabled, CancellationToken cancellationToken = default) =>
        _db.ClientConfigs
            .Where(c => c.Id == clientConfigId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.GoogleReviewUrl, googleUrl)
                .SetProperty(c => c.FacebookReviewUrl, facebookUrl)
                .SetProperty(c => c.ReviewRequestsEnabled, enabled)
                .SetProperty(c => c.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);

    public Task SetAskedAsync(Guid clientConfigId, Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default) =>
        _db.Bookings
            .Where(b => b.Id == bookingId && b.ClientConfigId == clientConfigId && b.ReviewAskedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReviewAskedAtUtc, utcNow), cancellationToken);

    public async Task AddClickAsync(ReviewClick click, CancellationToken cancellationToken = default)
    {
        _db.ReviewClicks.Add(click);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid?> GetBookingClientAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        await _db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => (Guid?)b.ClientConfigId)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<List<Booking>> GetRatedAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking()
            .Where(b => b.ClientConfigId == clientConfigId && b.FeedbackRating != null &&
                        b.FeedbackAtUtc >= fromUtc && b.FeedbackAtUtc < toUtc)
            .OrderByDescending(b => b.FeedbackAtUtc)
            .ToListAsync(cancellationToken);

    public Task<int> CountAskedAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking()
            .CountAsync(b => b.ClientConfigId == clientConfigId && b.ReviewAskedAtUtc >= fromUtc && b.ReviewAskedAtUtc < toUtc, cancellationToken);

    public Task<List<ReviewClick>> GetClicksAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        _db.ReviewClicks.AsNoTracking()
            .Where(c => c.ClientConfigId == clientConfigId && c.ClickedAtUtc >= fromUtc && c.ClickedAtUtc < toUtc)
            .ToListAsync(cancellationToken);
}
