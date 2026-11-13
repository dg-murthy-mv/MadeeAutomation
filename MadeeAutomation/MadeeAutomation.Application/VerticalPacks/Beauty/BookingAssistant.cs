using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Application.VerticalPacks.Messaging;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Beauty;

/// <summary>What the AI made of a typed message to a spa / salon.</summary>
public class BookingAnalysis
{
    /// <summary>greeting | services | book | my_bookings | question | other</summary>
    public string Intent { get; set; } = "other";
    public string? Language { get; set; }
    public string Reply { get; set; } = string.Empty;
    public bool CanAnswer { get; set; }
    public bool NeedsHuman { get; set; }

    /// <summary>The service they named, as written in SERVICES (null = none named).</summary>
    public string? Service { get; set; }

    /// <summary>The day they asked for, in the client's time zone.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>The time of day they asked for ("15:00").</summary>
    public TimeOnly? Time { get; set; }
}

/// <summary>
/// One AI call per TYPED message to a beauty / wellness business: what does the customer want, which
/// service and day did they mention, and a short reply in their own language - answered only from the
/// business's own information. Taps on buttons never come here (they are answered by our code).
/// The AI never makes the booking itself: it only points the chat to the right step.
/// </summary>
public class BookingAssistant
{
    private readonly IClaudeService _claude;
    private readonly TimeProvider _clock;

    public BookingAssistant(IClaudeService claude, TimeProvider clock)
    {
        _claude = claude;
        _clock = clock;
    }

    public async Task<BookingAnalysis?> AnalyzeAsync(BookingCatalog cat, BusinessStatus hours, string customerMessage, CancellationToken cancellationToken = default)
    {
        var system = BuildSystemPrompt(cat, hours, _clock.GetUtcNow().UtcDateTime);
        var parsed = JsonReply.TryParseObject(await _claude.CompleteJsonAsync(system, customerMessage, cancellationToken))
                     ?? JsonReply.TryParseObject(await _claude.CompleteJsonAsync(system, customerMessage, cancellationToken));
        if (parsed is null) return null;

        var root = parsed.Value;
        return new BookingAnalysis
        {
            Intent = (root.GetString("intent") ?? "other").Trim().ToLowerInvariant(),
            Language = root.GetString("language"),
            Reply = (root.GetString("reply") ?? "").Trim(),
            CanAnswer = root.GetBool("canAnswer"),
            NeedsHuman = root.GetBool("needsHuman"),
            Service = root.GetString("service") is { Length: > 0 } s ? s.Trim() : null,
            Date = DateOnly.TryParseExact(root.GetString("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Time = TimeOnly.TryParseExact(root.GetString("time"), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null
        };
    }

    /// <summary>Finds the service the AI named: exact name first, then the closest containing match.</summary>
    public static ServiceItem? MatchService(IReadOnlyList<ServiceItem> services, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        return services.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
               ?? services.Where(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(s => Math.Abs(s.Name.Length - n.Length)).FirstOrDefault();
    }

    public const string PromptMarker = "BOOKING ASSISTANT";

    public static string BuildSystemPrompt(BookingCatalog cat, BusinessStatus hours, DateTime utcNow)
    {
        var client = cat.Client;
        var localNow = LocalTime.ToLocal(utcNow, client.TimeZoneId);
        var currency = string.IsNullOrWhiteSpace(client.Currency) ? null : client.Currency.ToUpperInvariant();
        var sb = new StringBuilder();

        sb.AppendLine($"{PromptMarker}: you are the customer chat assistant for {client.ClientName}, a beauty / wellness business " +
                      "(massage, spa, nails, hair, facials). Customers write on LINE, WhatsApp or Messenger.");
        sb.AppendLine();
        sb.AppendLine("BUSINESS CLOCK:");
        sb.AppendLine($"Today is {localNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}, local time {localNow.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
                      $"(time zone {client.TimeZoneId}, {LocalTime.OffsetLabel(client.TimeZoneId, utcNow)}). Local date: {localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        if (currency is not null) sb.AppendLine($"All prices are in {currency}.");
        sb.AppendLine();
        sb.AppendLine("YOUR JOB: work out what the customer wants. The SYSTEM makes bookings with buttons (it shows services, staff, days and only the times that are really free).");
        sb.AppendLine("You NEVER confirm a booking or promise a time yourself - you cannot see who is free.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Reply in the SAME language the customer wrote in (Tagalog/Taglish -> the same, Thai -> Thai, English -> English). Short and friendly.");
        sb.AppendLine("- intent \"book\": they want to book / reserve / ask if a time is available (\"pwede ba 3pm bukas?\", \"available po kayo?\"). " +
                      "Put the service they named in \"service\" (use the SERVICES name exactly when it clearly matches, else null), " +
                      "the day in \"date\" (yyyy-MM-dd, worked out from the BUSINESS CLOCK; null if none) and the time in \"time\" (HH:mm, 24-hour; null if none). " +
                      "Reply with ONE short line like \"Sure! Let's find you a time.\" - do not list times.");
        sb.AppendLine("- intent \"services\": they ask what services you offer or the price list in general. Reply with ONE short line - the system shows the services as buttons.");
        sb.AppendLine("- intent \"my_bookings\": they ask about, want to cancel, or want to move a booking they already made. One short line.");
        sb.AppendLine("- intent \"greeting\": hello / good morning with nothing else. One short friendly line.");
        sb.AppendLine("- intent \"question\": anything else about the business. Answer ONLY from SERVICES, OPENING HOURS and BUSINESS INFORMATION. " +
                      "You may say a single service's price and duration exactly as written. If it is not covered, set canAnswer false and needsHuman true " +
                      "and say a staff member will reply soon.");
        sb.AppendLine("- needsHuman true for complaints, refunds, medical questions (pregnancy, injuries, skin conditions), group or event bookings, and anything you cannot handle.");
        sb.AppendLine("- Never invent prices, services, staff, promos or opening hours.");
        sb.AppendLine();

        if (hours.HoursConfigured)
        {
            sb.AppendLine("OPENING HOURS:");
            sb.AppendLine(hours.WeekText);
            if (hours.ClosedDatesText is not null) sb.AppendLine("Closed on: " + hours.ClosedDatesText);
            sb.AppendLine("RIGHT NOW: " + hours.StatusText);
            sb.AppendLine();
        }

        sb.AppendLine("SERVICES:");
        var services = cat.Bookable;
        if (services.Count == 0) sb.AppendLine("(none listed)");
        foreach (var group in services.GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "" : s.Category.Trim()))
        {
            if (group.Key.Length > 0) sb.AppendLine($"[{group.Key}]");
            foreach (var s in group)
            {
                var home = cat.HomeServiceFor(s) ? " (home service available)" : "";
                var desc = string.IsNullOrWhiteSpace(s.Description) ? "" : $" - {s.Description.Trim()}";
                sb.AppendLine($"- {s.Name}: {BookingDesk.ServiceLine(s, currency)}{home}{desc}");
            }
        }
        if (client.HomeServiceEnabled)
            sb.AppendLine(client.HomeServiceFee is { } fee
                ? $"Home service: we come to the customer; {Money.Format(fee, currency)} is added to the price."
                : "Home service: we come to the customer.");
        sb.AppendLine();
        sb.AppendLine("BUSINESS INFORMATION:");
        sb.AppendLine(string.IsNullOrWhiteSpace(client.KnowledgeBaseText) ? "(none provided)" : client.KnowledgeBaseText.Trim());
        sb.AppendLine();
        sb.AppendLine("Respond with ONE JSON object only, in exactly this shape:");
        sb.AppendLine("{ \"intent\": \"greeting\" | \"services\" | \"book\" | \"my_bookings\" | \"question\" | \"other\", \"language\": string, \"reply\": string, " +
                      "\"canAnswer\": boolean, \"needsHuman\": boolean, \"service\": string|null, \"date\": \"yyyy-MM-dd\"|null, \"time\": \"HH:mm\"|null }");
        return sb.ToString();
    }
}
