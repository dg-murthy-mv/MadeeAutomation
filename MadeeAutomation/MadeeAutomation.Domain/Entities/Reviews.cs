namespace MadeeAutomation.Domain.Entities;

/// <summary>Where a review link points.</summary>
public static class ReviewSites
{
    public const string Google = "google";
    public const string Facebook = "facebook";

    /// <summary>The letter used in the short link (/r/g/... or /r/f/...).</summary>
    public static string? FromLetter(string? letter) => letter?.ToLowerInvariant() switch
    {
        "g" => Google,
        "f" => Facebook,
        _ => null
    };

    public static string Letter(string site) => site == Facebook ? "f" : "g";
}

/// <summary>
/// A customer opened a review link sent after their visit (the link goes through our WebApi, which
/// records the click and forwards them to Google or Facebook). Link previews made by WhatsApp /
/// Messenger / LINE are not counted.
/// </summary>
public class ReviewClick
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }
    public Guid? BookingId { get; set; }

    /// <summary>ReviewSites.Google or ReviewSites.Facebook.</summary>
    public string Site { get; set; } = ReviewSites.Google;

    public DateTime ClickedAtUtc { get; set; } = DateTime.UtcNow;
}
