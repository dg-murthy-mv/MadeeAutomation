namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One item on a client's menu / product list. Prices are in the client's currency.
/// Order totals are calculated from these prices by our code - the AI never sets a price.
/// </summary>
public class MenuItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }            // "Bread", "Cakes", "Drinks" ...
    public decimal? Price { get; set; }              // null = price on request

    /// <summary>Other names customers use, comma-separated: "ครัวซองต์, croissants, plain croissant".</summary>
    public string? Aliases { get; set; }

    /// <summary>Off = sold out / not available: the bot says so and never orders it.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// The most the bot may take orders for in one day (e.g. 15 birthday cakes). Null = no limit.
    /// Counted from saved orders for that day, in the client's time zone. Cancelled orders don't count.
    /// </summary>
    public int? DailyLimit { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public IEnumerable<string> AliasList =>
        (Aliases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
