using System.Text.Json;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// What we remember about one customer's chat between messages: their cart (built with the menu
/// buttons), whether we are waiting for their pick-up time, and the language they write in.
/// One row per client + channel + customer.
/// </summary>
public class Conversation
{
    /// <summary>A cart nobody touched for this long is treated as empty.</summary>
    public static readonly TimeSpan CartLifetime = TimeSpan.FromHours(12);

    public const string AwaitingTime = "awaiting_time";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>JSON list of { "i": menu item id, "q": quantity }.</summary>
    public string? CartJson { get; set; }

    /// <summary>"" or AwaitingTime (we asked "When do you want it?").</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The language the AI last saw the customer write in ("en", "th"...).</summary>
    public string? Language { get; set; }

    /// <summary>The category they looked at last, for the "Add more" button.</summary>
    public string? LastCategory { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<CartLine> ReadCart(DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(CartJson) || utcNow - UpdatedAtUtc > CartLifetime) return new();
        try
        {
            return (JsonSerializer.Deserialize<List<CartLine>>(CartJson) ?? new()).Where(l => l.Quantity > 0).ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void WriteCart(List<CartLine> lines) =>
        CartJson = lines.Count == 0 ? null : JsonSerializer.Serialize(lines);
}

public class CartLine
{
    [System.Text.Json.Serialization.JsonPropertyName("i")] public Guid MenuItemId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("q")] public int Quantity { get; set; }
}
