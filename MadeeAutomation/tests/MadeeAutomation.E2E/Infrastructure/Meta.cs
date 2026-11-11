using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>
/// Builds and signs WhatsApp Cloud API / Messenger webhook calls the way Meta sends them
/// (X-Hub-Signature-256 = "sha256=" + hex HMAC-SHA256 of the body with the App Secret).
/// </summary>
public static class Meta
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    public static async Task<WebhookResponse> PostAsync(string path, string body, string? secret, string? signatureOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GlobalSetup.Settings.WebApiUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var signature = signatureOverride ?? (secret is null ? null : Sign(secret, body));
        if (signature is not null) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await Http.SendAsync(request);
        return new WebhookResponse(response.StatusCode, await response.Content.ReadAsStringAsync(), sw.ElapsedMilliseconds);
    }

    public static async Task<(HttpStatusCode Status, string Body)> GetAsync(string pathAndQuery)
    {
        using var response = await Http.GetAsync(GlobalSetup.Settings.WebApiUrl + pathAndQuery);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static string NewWhatsAppNumber(string prefix = "6391") => prefix + Random.Shared.NextInt64(10_000_000, 99_999_999);

    /// <summary>A WhatsApp webhook body with one message from `from` to the business number `phoneNumberId`.</summary>
    public static string WhatsAppBody(string from, string name, string phoneNumberId, object message, string? messageId = null)
    {
        var msg = JsonSerializer.SerializeToNode(message)!.AsObject();
        msg["from"] = from;
        msg["id"] = messageId ?? "wamid." + Guid.NewGuid().ToString("N");
        msg["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "WABA",
                ["changes"] = new JsonArray(new JsonObject
                {
                    ["field"] = "messages",
                    ["value"] = new JsonObject
                    {
                        ["messaging_product"] = "whatsapp",
                        ["metadata"] = new JsonObject { ["display_phone_number"] = "6680000000", ["phone_number_id"] = phoneNumberId },
                        ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = name }, ["wa_id"] = from }),
                        ["messages"] = new JsonArray(msg)
                    }
                })
            })
        };
        return body.ToJsonString();
    }

    public static object WaText(string text) => new { type = "text", text = new { body = text } };
    public static object WaTap(string id, bool list = false) => list
        ? new { type = "interactive", interactive = new { type = "list_reply", list_reply = new { id, title = "x" } } }
        : (object)new { type = "interactive", interactive = new { type = "button_reply", button_reply = new { id, title = "x" } } };

    /// <summary>A Messenger webhook body with one event from `psid`.</summary>
    public static string MessengerBody(string psid, object evt)
    {
        var e = JsonSerializer.SerializeToNode(evt)!.AsObject();
        e["sender"] = new JsonObject { ["id"] = psid };
        e["recipient"] = new JsonObject { ["id"] = "PAGE" };
        e["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["object"] = "page",
            ["entry"] = new JsonArray(new JsonObject { ["id"] = "PAGE", ["messaging"] = new JsonArray(e) })
        }.ToJsonString();
    }

    public static object FbText(string text) => new { message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text } };
    public static object FbQuickReply(string payload) => new { message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text = "x", quick_reply = new { payload } } };
    public static object FbPostback(string payload) => new { postback = new { mid = "m_" + Guid.NewGuid().ToString("N"), title = "x", payload } };

    // ---- reading what we sent ----

    /// <summary>(title, id, description) of every WhatsApp reply button / list row in these calls.</summary>
    public static List<(string Title, string Id, string? Description)> WaChoices(IEnumerable<MetaCall> calls)
    {
        var list = new List<(string, string, string?)>();
        foreach (var c in calls)
        {
            var i = JsonNode.Parse(c.Body)?["interactive"];
            if (i is null) continue;
            if (i["type"]!.GetValue<string>() == "button")
                list.AddRange(i["action"]!["buttons"]!.AsArray().Select(b => (b!["reply"]!["title"]!.GetValue<string>(), b["reply"]!["id"]!.GetValue<string>(), (string?)null)));
            else
                foreach (var s in i["action"]!["sections"]!.AsArray())
                    list.AddRange(s!["rows"]!.AsArray().Select(r => (r!["title"]!.GetValue<string>(), r["id"]!.GetValue<string>(), r["description"]?.GetValue<string>())));
        }
        return list;
    }

    /// <summary>(title, payload) of Messenger quick replies in these calls.</summary>
    public static List<(string Title, string Payload)> FbQuickReplies(IEnumerable<MetaCall> calls) =>
        calls.SelectMany(c => JsonNode.Parse(c.Body)?["message"]?["quick_replies"]?.AsArray() ?? new JsonArray())
            .Select(q => (q!["title"]!.GetValue<string>(), q["payload"]!.GetValue<string>())).ToList();

    /// <summary>Card titles with their button payloads in Messenger generic templates.</summary>
    public static List<(string Title, List<string> Payloads)> FbCards(IEnumerable<MetaCall> calls) =>
        calls.SelectMany(c => JsonNode.Parse(c.Body)?["message"]?["attachment"]?["payload"]?["elements"]?.AsArray() ?? new JsonArray())
            .Select(e => (e!["title"]!.GetValue<string>(),
                (e["buttons"]?.AsArray() ?? new JsonArray()).Select(b => b!["payload"]!.GetValue<string>()).ToList())).ToList();
}
