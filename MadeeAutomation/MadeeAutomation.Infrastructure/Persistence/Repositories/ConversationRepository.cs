using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

/// <summary>One row per client + channel + customer: their cart and chat state.</summary>
public class ConversationRepository : IConversationRepository
{
    private readonly AutomationDbContext _db;

    public ConversationRepository(AutomationDbContext db) => _db = db;

    public Task<Conversation?> GetAsync(Guid clientConfigId, string channel, string customerId, CancellationToken cancellationToken = default) =>
        _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientConfigId == clientConfigId && c.Channel == channel && c.CustomerId == customerId, cancellationToken);

    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        conversation.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.Conversations.FirstOrDefaultAsync(c =>
            c.ClientConfigId == conversation.ClientConfigId && c.Channel == conversation.Channel && c.CustomerId == conversation.CustomerId,
            cancellationToken);

        if (existing is null)
        {
            _db.Conversations.Add(conversation);
        }
        else
        {
            existing.CartJson = conversation.CartJson;
            existing.State = conversation.State;
            existing.Language = conversation.Language;
            existing.LastCategory = conversation.LastCategory;
            existing.UpdatedAtUtc = conversation.UpdatedAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
