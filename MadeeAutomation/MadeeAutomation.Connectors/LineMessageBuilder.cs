using MadeeAutomation.Application.Abstractions;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Turns our text + buttons + cards into LINE Messaging API message objects:
///   - quick replies  -> the row of buttons above the keyboard (max 13)
///   - cards          -> a Flex "carousel" the customer swipes through (max 12)
/// The text always goes first as a normal text message, so a customer on a very old LINE app
/// still gets the words. LINE's own size limits are applied here so a long name can't break a send.
/// </summary>
public static class LineMessageBuilder
{
    public const int MaxText = 5000;
    public const int MaxQuickReplies = 13;
    public const int MaxCards = 12;
    private const int MaxLabel = 20;
    private const int MaxData = 300;

    private static string Cut(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    public static object[] Build(string text, RichContent? rich)
    {
        text = text.Length > MaxText ? text[..MaxText] : text;
        if (rich is null || rich.IsEmpty) return new object[] { new { type = "text", text } };

        var quickReply = rich.QuickReplies.Count == 0
            ? null
            : new { items = rich.QuickReplies.Take(MaxQuickReplies).Select(b => new { type = "action", action = Action(b) }).ToArray() };

        if (rich.Cards.Count == 0)
            return new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text, ["quickReply"] = quickReply } };

        var flex = new Dictionary<string, object?>
        {
            ["type"] = "flex",
            ["altText"] = Cut(text, 400),
            ["contents"] = new { type = "carousel", contents = rich.Cards.Take(MaxCards).Select(Bubble).ToArray() }
        };
        if (quickReply is not null) flex["quickReply"] = quickReply;   // quick replies show under the LAST message

        return new object[] { new { type = "text", text }, flex };
    }

    private static object Action(ChatButton button)
    {
        var label = Cut(button.Label, MaxLabel);
        var data = button.Data.Length > MaxData ? button.Data[..MaxData] : button.Data;

        if (button.PickDateTime)
        {
            var picker = new Dictionary<string, object?> { ["type"] = "datetimepicker", ["label"] = label, ["data"] = data, ["mode"] = "datetime" };
            if (button.PickInitial is not null) picker["initial"] = button.PickInitial;
            if (button.PickMin is not null) picker["min"] = button.PickMin;
            if (button.PickMax is not null) picker["max"] = button.PickMax;
            return picker;
        }

        return new { type = "postback", label, data, displayText = Cut(button.DisplayText ?? button.Label, 300) };
    }

    private static object Bubble(ChatCard card)
    {
        var body = new List<object>
        {
            new { type = "text", text = Cut(card.Title, 100), weight = "bold", size = "md", wrap = true }
        };
        if (!string.IsNullOrWhiteSpace(card.Subtitle))
            body.Add(new { type = "text", text = Cut(card.Subtitle, 60), size = "sm", color = "#555555" });
        if (!string.IsNullOrWhiteSpace(card.Note))
            body.Add(new { type = "text", text = Cut(card.Note, 80), size = "xs", color = "#C0392B", wrap = true });

        var bubble = new Dictionary<string, object?>
        {
            ["type"] = "bubble",
            ["size"] = "kilo",
            ["body"] = new { type = "box", layout = "vertical", spacing = "sm", contents = body }
        };

        if (card.Buttons.Count > 0)
        {
            bubble["footer"] = new
            {
                type = "box",
                layout = "horizontal",
                spacing = "sm",
                contents = card.Buttons.Take(3).Select((b, i) => new
                {
                    type = "button",
                    style = i == 0 ? "primary" : "secondary",
                    height = "sm",
                    action = Action(b)
                }).ToArray()
            };
        }

        return bubble;
    }

    /// <summary>
    /// The same message as words only: used when a channel can't show buttons, and as the
    /// safety net if LINE refuses the buttons / cards.
    /// </summary>
    public static string PlainText(string text, RichContent? rich)
    {
        if (rich is null || rich.Cards.Count == 0) return text;

        var lines = rich.Cards.Select(c =>
            $"- {c.Title}" + (string.IsNullOrWhiteSpace(c.Subtitle) ? "" : $": {c.Subtitle}") + (string.IsNullOrWhiteSpace(c.Note) ? "" : $" ({c.Note})"));
        var all = text + "\n" + string.Join("\n", lines);
        return all.Length > MaxText ? all[..MaxText] : all;
    }
}
