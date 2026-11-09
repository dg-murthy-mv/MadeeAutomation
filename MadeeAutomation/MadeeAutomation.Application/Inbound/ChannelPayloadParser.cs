using System.Text.Json;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Inbound;

/// <summary>One customer text message pulled out of a channel's webhook body.</summary>
public class ParsedInboundMessage
{
    public string ExternalEventId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ReplyToken { get; set; }
}

/// <summary>Result of parsing a webhook body: the text messages, plus a count of events we ignored.</summary>
public class ParsedWebhook
{
    public List<ParsedInboundMessage> Messages { get; } = new();
    public int IgnoredEvents { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Turns LINE / WhatsApp / Messenger webhook bodies into ParsedInboundMessages.
/// Reads EVERY event in the body (platforms batch several events into one call),
/// and skips anything that is not a customer text message (stickers, read receipts, echoes...).
/// </summary>
public static class ChannelPayloadParser
{
    public static ParsedWebhook Parse(string channel, string rawBody) => channel switch
    {
        Channels.Line => ParseLine(rawBody),
        Channels.WhatsApp => ParseWhatsApp(rawBody),
        Channels.Messenger => ParseMessenger(rawBody),
        _ => new ParsedWebhook { Error = $"Unknown channel '{channel}'." }
    };

    private static JsonDocument? TryParse(string rawBody, ParsedWebhook result)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
        }
        catch (JsonException ex)
        {
            result.Error = "Body is not valid JSON: " + ex.Message;
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static JsonElement Obj(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    /// <summary>
    /// LINE: { "destination": "...", "events": [ { "type": "message", "mode": "active",
    ///   "webhookEventId": "...", "replyToken": "...", "source": { "userId": "...", "groupId"?: "..." },
    ///   "message": { "id": "...", "type": "text", "text": "..." } } ] }
    /// The LINE console "Verify" button sends an empty events array - that's a valid, empty result.
    /// </summary>
    public static ParsedWebhook ParseLine(string rawBody)
    {
        var result = new ParsedWebhook();
        using var doc = TryParse(rawBody, result);
        if (doc is null) return result;

        foreach (var evt in Arr(doc.RootElement, "events"))
        {
            var message = Obj(evt, "message");
            var mode = Str(evt, "mode");
            if (Str(evt, "type") != "message" || Str(message, "type") != "text" || mode == "standby")
            {
                result.IgnoredEvents++;
                continue;
            }

            var source = Obj(evt, "source");
            // Reply to the conversation the message came from: group/room first, otherwise the user.
            var target = Str(source, "groupId") ?? Str(source, "roomId") ?? Str(source, "userId");
            var text = Str(message, "text");
            var externalId = Str(evt, "webhookEventId") ?? Str(message, "id");

            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(externalId))
            {
                result.IgnoredEvents++;
                continue;
            }

            result.Messages.Add(new ParsedInboundMessage
            {
                ExternalEventId = externalId,
                CustomerId = target,
                Text = text,
                ReplyToken = Str(evt, "replyToken")
            });
        }

        return result;
    }

    /// <summary>WhatsApp Cloud API: entry[].changes[].value.messages[] where type == "text".</summary>
    public static ParsedWebhook ParseWhatsApp(string rawBody)
    {
        var result = new ParsedWebhook();
        using var doc = TryParse(rawBody, result);
        if (doc is null) return result;

        foreach (var entry in Arr(doc.RootElement, "entry"))
        foreach (var change in Arr(entry, "changes"))
        {
            var value = Obj(change, "value");
            foreach (var msg in Arr(value, "messages"))
            {
                var text = Str(Obj(msg, "text"), "body");
                var from = Str(msg, "from");
                var id = Str(msg, "id");
                if (Str(msg, "type") != "text" || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(id))
                {
                    result.IgnoredEvents++;
                    continue;
                }
                result.Messages.Add(new ParsedInboundMessage { ExternalEventId = id, CustomerId = from, Text = text });
            }
            // status updates (sent/delivered/read) have no "messages" - nothing to do.
        }

        return result;
    }

    /// <summary>Messenger: entry[].messaging[] with message.text; echoes of our own messages are skipped.</summary>
    public static ParsedWebhook ParseMessenger(string rawBody)
    {
        var result = new ParsedWebhook();
        using var doc = TryParse(rawBody, result);
        if (doc is null) return result;

        foreach (var entry in Arr(doc.RootElement, "entry"))
        foreach (var evt in Arr(entry, "messaging"))
        {
            var message = Obj(evt, "message");
            var isEcho = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("is_echo", out var e) && e.ValueKind == JsonValueKind.True;
            var text = Str(message, "text");
            var psid = Str(Obj(evt, "sender"), "id");
            var mid = Str(message, "mid");
            if (isEcho || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(psid) || string.IsNullOrWhiteSpace(mid))
            {
                result.IgnoredEvents++;
                continue;
            }
            result.Messages.Add(new ParsedInboundMessage { ExternalEventId = mid, CustomerId = psid, Text = text });
        }

        return result;
    }
}
