using MadeeAutomation.Domain.Billing;
using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>Plans, payments and plan notices. Queries stay simple; adding up happens in C#.</summary>
public interface ISubscriptionRepository
{
    /// <summary>
    /// Customer chats in a period: one customer writing or tapping on one (local) day = one chat.
    /// offsetMinutes = the client's UTC offset, so days are counted in the client's own time.
    /// </summary>
    Task<int> CountChatsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default);

    /// <summary>Every client that is not cancelled, with workflows - for the Billing page and the plan job.</summary>
    Task<List<ClientConfig>> GetClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the plan fields only (plan, cycle, end, agreed price, note).</summary>
    Task SetPlanAsync(Guid clientConfigId, PlanTier plan, BillingCycle? cycle, DateTime? endsAtUtc, decimal? price, string? note,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the payment, moves the client to its plan / cycle / new end, and makes a trial client Active.</summary>
    Task AddPaymentAsync(SubscriptionPayment payment, decimal? agreedPrice, CancellationToken cancellationToken = default);

    Task<List<SubscriptionPayment>> GetPaymentsAsync(Guid clientConfigId, CancellationToken cancellationToken = default);

    /// <summary>Payments recorded in a period, all clients (Billing page totals).</summary>
    Task<List<SubscriptionPayment>> GetPaymentsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a once-only notice. True = this caller should send it; false = it was already sent
    /// (by this or another WebApi copy).
    /// </summary>
    Task<bool> TryClaimNoticeAsync(Guid clientConfigId, string noticeKey, CancellationToken cancellationToken = default);

    /// <summary>Gives a claim back when the email could not be sent, so the next run tries again.</summary>
    Task ReleaseNoticeAsync(Guid clientConfigId, string noticeKey, CancellationToken cancellationToken = default);
}
