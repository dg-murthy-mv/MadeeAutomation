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

    /// <summary>"text", "tap" (Text = the button's data) or "follow".</summary>
    public string Kind { get; set; } = "text";

    /// <summary>The customer's name when the platform sends it (WhatsApp profile name).</summary>
    public string? CustomerName { get; set; }
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
        Channels.WhatsApp => ParseWhatsApp(rawBody, null),
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
            var type = Str(evt, "type");

            // A tapped button ("postback") or a new friend ("follow"): both carry a reply token.
            if ((type == "postback" || type == "follow") && mode != "standby")
            {
                var tapSource = Obj(evt, "source");
                var tapTarget = Str(tapSource, "groupId") ?? Str(tapSource, "roomId") ?? Str(tapSource, "userId");
                var tapId = Str(evt, "webhookEventId");
                var postback = Obj(evt, "postback");
                var data = type == "follow" ? "follow" : Str(postback, "data");
                if (string.IsNullOrWhiteSpace(tapTarget) || string.IsNullOrWhiteSpace(tapId) || string.IsNullOrWhiteSpace(data) || data.Length > 400)
                {
                    result.IgnoredEvents++;
                    continue;
                }

                // The date-and-time picker adds what the customer chose.
                var picked = Str(Obj(postback, "params"), "datetime");
                if (picked is not null) data += "&datetime=" + Uri.EscapeDataString(picked);

                result.Messages.Add(new ParsedInboundMessage
                {
                    ExternalEventId = tapId,
                    CustomerId = tapTarget,
                    Text = data,
                    ReplyToken = Str(evt, "replyToken"),
                    Kind = type == "follow" ? "follow" : "tap"
                });
                continue;
            }

            if (type != "message" || Str(message, "type") != "text" || mode == "standby")
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

    /// <summary>
    /// WhatsApp Cloud API: entry[].changes[].value.messages[].
    ///   type "text"        -> a typed message (text.body)
    ///   type "interactive" -> a tapped reply button (interactive.button_reply.id) or a picked list row
    ///                         (interactive.list_reply.id): Kind "tap", Text = the id we gave the button
    ///   type "button"      -> a quick-reply button on a template message (button.payload)
    /// The customer's profile name comes in value.contacts[] (matched on wa_id).
    /// expectedPhoneNumberId: when set, messages sent to a DIFFERENT WhatsApp number are ignored
    /// (stops one client's number being answered with another client's menu).
    /// </summary>
    public static ParsedWebhook ParseWhatsApp(string rawBody, string? expectedPhoneNumberId)
    {
        var result = new ParsedWebhook();
        using var doc = TryParse(rawBody, result);
        if (doc is null) return result;

        foreach (var entry in Arr(doc.RootElement, "entry"))
        foreach (var change in Arr(entry, "changes"))
        {
            var value = Obj(change, "value");
            var phoneNumberId = Str(Obj(value, "metadata"), "phone_number_id");
            var wrongNumber = !string.IsNullOrWhiteSpace(expectedPhoneNumberId) && phoneNumberId is not null
                              && phoneNumberId != expectedPhoneNumberId.Trim();

            var names = new Dictionary<string, string>();
            foreach (var contact in Arr(value, "contacts"))
            {
                var waId = Str(contact, "wa_id");
                var name = Str(Obj(contact, "profile"), "name");
                if (!string.IsNullOrWhiteSpace(waId) && !string.IsNullOrWhiteSpace(name)) names[waId] = name.Trim();
            }

            foreach (var msg in Arr(value, "messages"))
            {
                var from = Str(msg, "from");
                var id = Str(msg, "id");
                var type = Str(msg, "type");
                string? text = null;
                var kind = "text";

                if (type == "text")
                {
                    text = Str(Obj(msg, "text"), "body");
                }
                else if (type == "interactive")
                {
                    var interactive = Obj(msg, "interactive");
                    var picked = Str(interactive, "type") switch
                    {
                        "button_reply" => Obj(interactive, "button_reply"),
                        "list_reply" => Obj(interactive, "list_reply"),
                        _ => default
                    };
                    text = Str(picked, "id");
                    kind = "tap";
                }
                else if (type == "button")
                {
                    text = Str(Obj(msg, "button"), "payload");
                    kind = "tap";
                }

                if (wrongNumber || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(id)
                    || (kind == "tap" && text.Length > 400))
                {
                    result.IgnoredEvents++;
                    continue;
                }

                result.Messages.Add(new ParsedInboundMessage
                {
                    ExternalEventId = id,
                    CustomerId = from,
                    Text = text,
                    Kind = kind,
                    CustomerName = names.TryGetValue(from, out var n) ? n : null
                });
            }
            // status updates (sent/delivered/read) have no "messages" - nothing to do.
        }

        return result;
    }

    /// <summary>
    /// Messenger: entry[].messaging[].
    ///   message.text                  -> a typed message (echoes of our own messages are skipped)
    ///   message.quick_reply.payload   -> a tapped quick reply: Kind "tap", Text = the payload
    ///   postback.payload              -> a tapped card button: Kind "tap"
    /// </summary>
    public static ParsedWebhook ParseMessenger(string rawBody)
    {
        var result = new ParsedWebhook();
        using var doc = TryParse(rawBody, result);
        if (doc is null) return result;

        foreach (var entry in Arr(doc.RootElement, "entry"))
        foreach (var evt in Arr(entry, "messaging"))
        {
            var psid = Str(Obj(evt, "sender"), "id");
            var message = Obj(evt, "message");
            var postback = Obj(evt, "postback");

            if (postback.ValueKind == JsonValueKind.Object)
            {
                var payload = Str(postback, "payload");
                // Postbacks have a "mid" on current API versions; otherwise sender + timestamp is unique enough.
                var postbackId = Str(postback, "mid") ?? (evt.TryGetProperty("timestamp", out var ts) ? $"pb-{psid}-{ts}" : null);
                if (string.IsNullOrWhiteSpace(psid) || string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(postbackId) || payload.Length > 400)
                {
                    result.IgnoredEvents++;
                    continue;
                }
                result.Messages.Add(new ParsedInboundMessage { ExternalEventId = postbackId, CustomerId = psid, Text = payload, Kind = "tap" });
                continue;
            }

            var isEcho = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("is_echo", out var e) && e.ValueKind == JsonValueKind.True;
            var quickReply = Str(Obj(message, "quick_reply"), "payload");
            var text = quickReply ?? Str(message, "text");
            var mid = Str(message, "mid");
            if (isEcho || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(psid) || string.IsNullOrWhiteSpace(mid)
                || (quickReply is not null && quickReply.Length > 400))
            {
                result.IgnoredEvents++;
                continue;
            }
            result.Messages.Add(new ParsedInboundMessage { ExternalEventId = mid, CustomerId = psid, Text = text, Kind = quickReply is null ? "text" : "tap" });
        }

        return result;
    }
}
