namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One call to the AI (Claude): which client it was for, what it was for, how many tokens it used
/// and what that cost us in US dollars. Written for every successful call so MadeeVision can see the
/// AI cost of each client (Admin -> Reports). Clients never see this.
/// </summary>
public class AiUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null when the call was not made for one client (should be rare).</summary>
    public Guid? ClientConfigId { get; set; }

    /// <summary>What the call was for: the workflow's handler key ("customer-message", "booking-message"...)
    /// or "notice-translate" for a translated order/booking notice.</summary>
    public string Purpose { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }

    /// <summary>Worked out from Anthropic:PriceInputPerMillion / PriceOutputPerMillion when the call was made.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>An Admin "Test" run - counted separately so it doesn't look like customer traffic.</summary>
    public bool IsTest { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
