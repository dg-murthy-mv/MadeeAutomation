using MadeeAutomation.Application.Abstractions;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Turns our text + buttons + cards into WhatsApp Cloud API and Messenger Send API messages.
///
/// WhatsApp (one "interactive" part per message):
///   - up to 3 buttons      -> reply buttons under the message
///   - 4 to 10 buttons      -> a list ("Choose" opens it)
///   - menu item cards      -> a list of items (tap = add 1 to the cart), then the other buttons
///   - the date/time picker -> not available on WhatsApp; the customer types the time instead
///
/// Messenger:
///   - buttons -> quick replies (max 13) under the last message
///   - cards   -> a swipe-through "generic template" (max 10), after the text
///
/// Meta's size limits are applied here so a long item name can never make a send fail.
/// Taps come back as the button's Data (the same "a=add&amp;i=..." data LINE uses), so the chat logic is shared.
/// </summary>
public static class MetaMessageBuilder
{
    // WhatsApp limits (Cloud API "interactive" messages)
    public const int WaMaxText = 4096;
    private const int WaMaxButtonBody = 1024;
    private const int WaMaxReplyButtons = 3;
    private const int WaMaxRows = 10;
    private const int WaButtonTitle = 20;
    private const int WaRowTitle = 24;
    private const int WaRowDescription = 72;
    private const int WaRowId = 200;
    private const int WaButtonId = 256;

    // Messenger limits (Send API)
    public const int FbMaxText = 2000;
    private const int FbMaxQuickReplies = 13;
    private const int FbMaxCards = 10;
    private const int FbTitle = 80;
    private const int FbButtonTitle = 20;
    private const int FbQuickReplyTitle = 20;
    private const int FbPayload = 1000;

    public const string ListButtonLabel = "Choose";
    public const string MoreOptionsText = "More options:";

    private static string Cut(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static string CutHard(string value, int max) => value.Length <= max ? value : value[..max];

    private static List<ChatButton> Tappable(IEnumerable<ChatButton> buttons) =>
        buttons.Where(b => !b.PickDateTime && !string.IsNullOrWhiteSpace(b.Data) && !string.IsNullOrWhiteSpace(b.Label))
            .GroupBy(b => b.Data).Select(g => g.First())   // WhatsApp refuses two buttons with the same id
            .ToList();

    private static string CardLine(ChatCard c) =>
        $"- {c.Title}" + (string.IsNullOrWhiteSpace(c.Subtitle) ? "" : $": {c.Subtitle}") + (string.IsNullOrWhiteSpace(c.Note) ? "" : $" ({c.Note})");

    // =====================================================================
    // WhatsApp
    // =====================================================================

    /// <summary>The message bodies to POST to /{phone-number-id}/messages, in order.</summary>
    public static List<object> WhatsApp(string to, string text, RichContent? rich)
    {
        var parts = new List<object>();
        text = CutHard(text, WaMaxText);
        if (rich is null || rich.IsEmpty)
        {
            parts.Add(WaText(to, text));
            return parts;
        }

        var buttons = Tappable(rich.QuickReplies);

        // ---- menu items: a list, one row per item; tapping a row adds one to the cart ----
        var tappableCards = rich.Cards.Where(c => Tappable(c.Buttons).Count > 0).ToList();
        if (tappableCards.Count > 0)
        {
            var inList = tappableCards.Take(WaMaxRows).ToList();
            var notInList = rich.Cards.Where(c => !inList.Contains(c)).ToList();   // sold out, or past the 10-row limit
            var body = notInList.Count == 0 ? text : text + "\n" + string.Join("\n", notInList.Select(CardLine));
            var rows = inList.Select(c =>
            {
                var first = Tappable(c.Buttons)[0];
                var description = string.Join(" · ", new[] { c.Subtitle, c.Note, c.Title.Length > WaRowTitle ? c.Title : null }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                return Row(first.Data, c.Title, description);
            }).ToList();

            parts.Add(WaList(to, CutHard(body, WaMaxText), rows));
            if (buttons.Count > 0) parts.Add(WaButtons(to, MoreOptionsText, buttons));
            return parts;
        }

        // ---- only cards nobody can tap (all sold out): words, then the buttons ----
        if (rich.Cards.Count > 0) text = CutHard(text + "\n" + string.Join("\n", rich.Cards.Select(CardLine)), WaMaxText);
        if (buttons.Count == 0)
        {
            parts.Add(WaText(to, text));
            return parts;
        }

        // Reply-button messages allow a shorter body; a long text goes first on its own.
        if (buttons.Count <= WaMaxReplyButtons && text.Length > WaMaxButtonBody)
        {
            parts.Add(WaText(to, text));
            parts.Add(WaButtons(to, MoreOptionsText, buttons));
            return parts;
        }

        parts.Add(WaButtons(to, text, buttons));
        return parts;
    }

    private static object Row(string data, string title, string? description)
    {
        var row = new Dictionary<string, object?> { ["id"] = CutHard(data, WaRowId), ["title"] = Cut(title, WaRowTitle) };
        if (!string.IsNullOrWhiteSpace(description)) row["description"] = Cut(description, WaRowDescription);
        return row;
    }

    /// <summary>Up to 3 -> reply buttons; more -> a list (first 10).</summary>
    private static object WaButtons(string to, string body, List<ChatButton> buttons)
    {
        if (buttons.Count <= WaMaxReplyButtons)
        {
            return new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = CutHard(body, WaMaxButtonBody) },
                    action = new
                    {
                        buttons = buttons.Select(b => new
                        {
                            type = "reply",
                            reply = new { id = CutHard(b.Data, WaButtonId), title = Cut(b.Label, WaButtonTitle) }
                        }).ToArray()
                    }
                }
            };
        }

        var rows = buttons.Take(WaMaxRows)
            .Select(b => Row(b.Data, b.Label, b.Label.Length > WaRowTitle ? b.Label : null)).ToList();
        return WaList(to, body, rows);
    }

    private static object WaList(string to, string body, List<object> rows) => new
    {
        messaging_product = "whatsapp",
        recipient_type = "individual",
        to,
        type = "interactive",
        interactive = new
        {
            type = "list",
            body = new { text = CutHard(body, WaMaxText) },
            action = new
            {
                button = ListButtonLabel,
                sections = new[] { new { title = "Choose one", rows = rows.ToArray() } }
            }
        }
    };

    public static object WaText(string to, string text) => new
    {
        messaging_product = "whatsapp",
        recipient_type = "individual",
        to,
        type = "text",
        text = new { preview_url = false, body = CutHard(text, WaMaxText) }
    };

    // =====================================================================
    // Messenger
    // =====================================================================

    /// <summary>The "message" objects to send, in order (the caller adds recipient and messaging_type).</summary>
    public static List<Dictionary<string, object?>> Messenger(string text, RichContent? rich)
    {
        var parts = new List<Dictionary<string, object?>>();
        if (rich is null || rich.IsEmpty)
        {
            parts.Add(FbText(text));
            return parts;
        }

        var cards = rich.Cards.Take(FbMaxCards).ToList();
        var extra = rich.Cards.Skip(FbMaxCards).ToList();
        if (extra.Count > 0) text += "\n" + string.Join("\n", extra.Select(CardLine));
        parts.Add(FbText(text));

        if (cards.Count > 0)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["attachment"] = new
                {
                    type = "template",
                    payload = new
                    {
                        template_type = "generic",
                        elements = cards.Select(Element).ToArray()
                    }
                }
            });
        }

        // Quick replies show under the LAST message.
        var buttons = Tappable(rich.QuickReplies).Take(FbMaxQuickReplies).ToList();
        if (buttons.Count > 0)
        {
            parts[^1]["quick_replies"] = buttons.Select(b => new
            {
                content_type = "text",
                title = Cut(b.Label, FbQuickReplyTitle),
                payload = CutHard(b.Data, FbPayload)
            }).ToArray();
        }

        return parts;
    }

    private static object Element(ChatCard card)
    {
        var element = new Dictionary<string, object?> { ["title"] = Cut(card.Title, FbTitle) };
        var subtitle = string.Join(" · ", new[] { card.Subtitle, card.Note }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (subtitle.Length > 0) element["subtitle"] = Cut(subtitle, FbTitle);

        var buttons = Tappable(card.Buttons).Take(3).ToList();
        if (buttons.Count > 0)
            element["buttons"] = buttons.Select(b => new { type = "postback", title = Cut(b.Label, FbButtonTitle), payload = CutHard(b.Data, FbPayload) }).ToArray();
        return element;
    }

    public static Dictionary<string, object?> FbText(string text) => new() { ["text"] = CutHard(text, FbMaxText) };
}
