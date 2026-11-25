using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>Review booster: the review links, who was asked, link clicks, and the ratings to show.</summary>
public interface IReviewRepository
{
    /// <summary>Saves the client's Google / Facebook review links and the on / off switch (only those columns).</summary>
    Task SaveLinksAsync(Guid clientConfigId, string? googleUrl, string? facebookUrl, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Marks the booking as "review links sent" (first time only).</summary>
    Task SetAskedAsync(Guid clientConfigId, Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default);

    Task AddClickAsync(ReviewClick click, CancellationToken cancellationToken = default);

    /// <summary>The booking's client, for a link click (null when the booking does not exist).</summary>
    Task<Guid?> GetBookingClientAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>Bookings rated in the period (FeedbackAtUtc), newest first.</summary>
    Task<List<Booking>> GetRatedAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>How many customers were sent the review links in the period.</summary>
    Task<int> CountAskedAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Link clicks in the period.</summary>
    Task<List<ReviewClick>> GetClicksAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}
