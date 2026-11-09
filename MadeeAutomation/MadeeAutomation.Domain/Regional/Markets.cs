namespace MadeeAutomation.Domain.Regional;

/// <summary>One country MadeeAutomation sells in, with the defaults a new client gets.</summary>
public record Market(string CountryCode, string CountryName, string Currency, string TimeZoneId, string Note);

/// <summary>
/// The four markets: Thailand, India, Philippines, UAE. Choosing a country in Admin fills in
/// its currency and time zone. Each client works in ONE currency (same rule as Merkai workspaces).
/// Time zone ids are IANA ids - .NET 8 understands them on Windows and Linux.
/// </summary>
public static class Markets
{
    public static readonly IReadOnlyList<Market> All = new List<Market>
    {
        new("TH", "Thailand",    "THB", "Asia/Bangkok", "Customers mostly use LINE"),
        new("IN", "India",       "INR", "Asia/Kolkata", "Customers mostly use WhatsApp"),
        new("PH", "Philippines", "PHP", "Asia/Manila",  "Customers mostly use Messenger and WhatsApp"),
        new("AE", "UAE",         "AED", "Asia/Dubai",   "Customers mostly use WhatsApp"),
    };

    public static readonly IReadOnlyList<string> Currencies = All.Select(m => m.Currency).ToList();

    public static readonly IReadOnlyList<string> TimeZoneIds = All.Select(m => m.TimeZoneId).Append("UTC").ToList();

    public static Market? Find(string? countryCode) =>
        All.FirstOrDefault(m => string.Equals(m.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Formats money the way each market writes it - without CultureInfo, so a missing culture on a
/// server can never break a page (the CultureNotFoundException problem Merkai had).
///   THB  ฿1,234.50      INR  ₹1,23,456.50 (Indian lakh/crore grouping)
///   PHP  ₱1,234.50      AED  AED 1,234.50
/// </summary>
public static class Money
{
    public static string Symbol(string? currency) => (currency ?? "").ToUpperInvariant() switch
    {
        "THB" => "฿",
        "INR" => "₹",
        "PHP" => "₱",
        "AED" => "AED ",
        var other => string.IsNullOrEmpty(other) ? "" : other + " "
    };

    public static string Format(decimal amount, string? currency)
    {
        var negative = amount < 0;
        var rounded = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var whole = decimal.Truncate(rounded);
        var cents = (int)((rounded - whole) * 100);

        var digits = whole.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var grouped = string.Equals(currency, "INR", StringComparison.OrdinalIgnoreCase)
            ? GroupIndian(digits)
            : GroupThousands(digits);

        return (negative ? "-" : "") + Symbol(currency) + grouped + "." + cents.ToString("00");
    }

    private static string GroupThousands(string digits)
    {
        var parts = new Stack<string>();
        for (var end = digits.Length; end > 0; end -= 3)
            parts.Push(digits.Substring(Math.Max(0, end - 3), Math.Min(3, end)));
        return string.Join(",", parts);
    }

    /// <summary>1234567 -> 12,34,567 (last 3 digits, then groups of 2).</summary>
    private static string GroupIndian(string digits)
    {
        if (digits.Length <= 3) return digits;
        var last3 = digits[^3..];
        var rest = digits[..^3];
        var parts = new Stack<string>();
        for (var end = rest.Length; end > 0; end -= 2)
            parts.Push(rest.Substring(Math.Max(0, end - 2), Math.Min(2, end)));
        return string.Join(",", parts) + "," + last3;
    }
}

/// <summary>Safe time zone lookup and conversions (all stored times are UTC).</summary>
public static class LocalTime
{
    public static TimeZoneInfo Find(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static DateTime ToLocal(DateTime utc, string? timeZoneId) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Find(timeZoneId));

    public static DateTimeOffset ToLocalOffset(DateTime utc, string? timeZoneId)
    {
        var tz = Find(timeZoneId);
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeFromUtc(u, tz), tz.GetUtcOffset(u));
    }

    /// <summary>A wall-clock time in the given zone (e.g. 2026-11-10 09:00 Bangkok) as a DateTimeOffset.</summary>
    public static DateTimeOffset FromLocal(DateTime localWallClock, string? timeZoneId)
    {
        var tz = Find(timeZoneId);
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified));
    }

    /// <summary>"UTC+07:00" style label.</summary>
    public static string OffsetLabel(string? timeZoneId, DateTime utcNow)
    {
        var offset = Find(timeZoneId).GetUtcOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm");
    }
}
