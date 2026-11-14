using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>One call the apps made to the fake LINE API.</summary>
public record LineCall(string Kind, string? To, string? ReplyToken, string Text, string Authorization, int StatusReturned, DateTime AtUtc, string Body = "", string RetryKey = "")
{
    /// <summary>The labels of the quick-reply buttons on this message, in order.</summary>
    public List<string> Buttons => Actions("quickReply").Select(a => a.Label).ToList();

    /// <summary>The data a quick-reply button sends back when tapped (by its label).</summary>
    public string DataOf(string label) => Actions("quickReply").First(a => a.Label == label).Data;

    /// <summary>The item cards: title, price line, note and the data of each button.</summary>
    public List<(string Title, string? Price, string? Note, List<string> ButtonData)> Cards
    {
        get
        {
            var cards = new List<(string, string?, string?, List<string>)>();
            var messages = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(Body) ? "{}" : Body)?["messages"]?.AsArray();
            foreach (var message in messages ?? new System.Text.Json.Nodes.JsonArray())
            {
                if (message?["type"]?.GetValue<string>() != "flex") continue;
                foreach (var bubble in message["contents"]?["contents"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                {
                    var texts = bubble?["body"]?["contents"]?.AsArray().Select(t => t?["text"]?.GetValue<string>()).ToList() ?? new();
                    var isNote = bubble?["body"]?["contents"]?.AsArray().Select(t => t?["color"]?.GetValue<string>() == "#C0392B").ToList() ?? new();
                    string? price = null, note = null;
                    for (var i = 1; i < texts.Count; i++) { if (isNote[i]) note = texts[i]; else price = texts[i]; }
                    var data = bubble?["footer"]?["contents"]?.AsArray().Select(b => b?["action"]?["data"]?.GetValue<string>() ?? "").ToList() ?? new();
                    cards.Add((texts.FirstOrDefault() ?? "", price, note, data));
                }
            }
            return cards;
        }
    }

    private List<(string Label, string Data, string Type)> Actions(string property)
    {
        var result = new List<(string, string, string)>();
        var messages = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(Body) ? "{}" : Body)?["messages"]?.AsArray();
        foreach (var message in messages ?? new System.Text.Json.Nodes.JsonArray())
            foreach (var item in message?[property]?["items"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                result.Add((item?["action"]?["label"]?.GetValue<string>() ?? "", item?["action"]?["data"]?.GetValue<string>() ?? "", item?["action"]?["type"]?.GetValue<string>() ?? ""));
        return result;
    }

    public bool HasDateTimePicker => Actions("quickReply").Any(a => a.Type == "datetimepicker");
}

/// <summary>One call the apps made to the fake Claude API.</summary>
/// <summary>One call to the fake Meta Graph API (WhatsApp Cloud API or Messenger Send API).</summary>
public record MetaCall(string Channel, string To, string Body, string Authorization, int StatusReturned, DateTime AtUtc)
{
    /// <summary>"text", "button", "list" (WhatsApp interactive type) or "text" / "template" for Messenger.</summary>
    public string Type
    {
        get
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Body);
            if (Channel == "whatsapp")
                return node?["type"]?.GetValue<string>() == "interactive" ? node["interactive"]?["type"]?.GetValue<string>() ?? "?" : "text";
            return node?["message"]?["attachment"] is not null ? "template" : "text";
        }
    }

    /// <summary>The words of the message (WhatsApp text body / interactive body; Messenger text).</summary>
    public string Text
    {
        get
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Body);
            return (Channel == "whatsapp"
                ? node?["text"]?["body"] ?? node?["interactive"]?["body"]?["text"]
                : node?["message"]?["text"])?.GetValue<string>() ?? "";
        }
    }
}

public record ClaudeCall(string System, string UserMessage, string ApiKey, DateTime AtUtc);

/// <summary>One order the apps created in the fake POSFirst.</summary>
public record PosOrder(string OrderId, string CustomerId, string BodyJson, string ApiKey, DateTime AtUtc, string IdempotencyKey = "");

/// <summary>One call to the fake POSFirst, including refused ones (to count retries).</summary>
public record PosAttempt(string Path, string ApiKey, string IdempotencyKey, int StatusReturned, DateTime AtUtc);

/// <summary>
/// A single in-process web server that plays LINE, Claude (Anthropic) and POSFirst for the apps under test,
/// so tests never send a real message or spend API credit. Everything it receives is recorded.
///
/// Behaviour is chosen by the DATA, so tests never interfere with each other:
///   LINE   - token "fail-token"            -> every call returns 500
///          - reply token starting "expired" -> reply returns 400 (the app should fall back to push)
///   Claude - customer text decides the answer (see ClaudeReplyFor); "[slow]" waits 3 s, "[badjson]" returns prose,
///            "[aidown]" answers 500 (as if Anthropic were down)
/// </summary>
public sealed class FakeServer : IAsyncDisposable
{
    private WebApplication? _app;
    private int _orderCounter;

    public ConcurrentQueue<LineCall> LineCalls { get; } = new();
    public ConcurrentQueue<ClaudeCall> ClaudeCalls { get; } = new();
    public ConcurrentQueue<MetaCall> MetaCalls { get; } = new();
    public IReadOnlyList<MetaCall> MetaCallsTo(string to) => MetaCalls.Where(c => c.To == to).ToList();
    public ConcurrentQueue<PosOrder> PosOrders { get; } = new();

    public async Task StartAsync(string url)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();

        // ---------------- LINE Messaging API ----------------
        app.MapPost("/v2/bot/message/reply", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = body?["replyToken"]?.GetValue<string>();
            var text = FirstText(body);

            var status = auth.EndsWith("fail-token") ? 500
                : token is not null && token.StartsWith("expired") ? 400
                : 200;
            // A customer id starting "Ubadrich" makes the fake refuse buttons / cards, like LINE does for a malformed message.
            var raw = body?.ToJsonString() ?? "";
            if (status == 200 && token is not null && token.Contains("Ubadrich") && (raw.Contains("quickReply") || raw.Contains("\"flex\""))) status = 400;
            LineCalls.Enqueue(new LineCall("reply", null, token, text, auth, status, DateTime.UtcNow, raw));

            return status switch
            {
                500 => Results.Json(new { message = "fake LINE failure" }, statusCode: 500),
                400 => Results.Json(new { message = "Invalid reply token" }, statusCode: 400),
                _ => Results.Json(new { sentMessages = new[] { new { id = "1" } } })
            };
        });

        app.MapPost("/v2/bot/message/push", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var auth = ctx.Request.Headers.Authorization.ToString();
            var to = body?["to"]?.GetValue<string>();
            var status = auth.EndsWith("fail-token") ? 500 : 200;
            var raw = body?.ToJsonString() ?? "";
            if (status == 200 && to is not null && to.StartsWith("Ubadrich") && (raw.Contains("quickReply") || raw.Contains("\"flex\""))) status = 400;
            // A customer id starting "Uflaky": LINE is briefly unavailable - the FIRST try of each retry key gets 503.
            var retryKey = ctx.Request.Headers["X-Line-Retry-Key"].ToString();
            if (status == 200 && to is not null && to.StartsWith("Uflaky") && LineRetryKeysSeen.TryAdd(retryKey, true)) status = 503;
            LineCalls.Enqueue(new LineCall("push", to, null, FirstText(body), auth, status, DateTime.UtcNow, raw, retryKey));
            if (status == 503) return Results.Json(new { message = "fake LINE briefly unavailable" }, statusCode: 503);

            return status == 400 ? Results.Json(new { message = "fake LINE: invalid message" }, statusCode: 400)
                : status == 500
                ? Results.Json(new { message = "fake LINE failure" }, statusCode: 500)
                : Results.Json(new { sentMessages = new[] { new { id = "2" } } });
        });

        // The customer's LINE profile (used for the name on orders). Users starting "Unoname" have no readable profile.
        app.MapGet("/v2/bot/profile/{userId}", (string userId, HttpContext ctx) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            return auth.EndsWith("fail-token") || !auth.StartsWith("Bearer ") || userId.StartsWith("Unoname")
                ? Results.Json(new { message = "Not found" }, statusCode: 404)
                : Results.Json(new { userId, displayName = CustomerNameFor(userId) });
        });

        app.MapGet("/v2/bot/info", (HttpContext ctx) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            return auth.EndsWith("fail-token") || !auth.StartsWith("Bearer ")
                ? Results.Json(new { message = "Authentication failed." }, statusCode: 401)
                : Results.Json(new { userId = "Ubot", basicId = "@e2ebot", displayName = "E2E Bot", chatMode = "bot" });
        });

        // ---------------- Anthropic Messages API ----------------
        // ---- Meta Graph API (WhatsApp Cloud API + Messenger Send API) ----
        // WhatsApp: a recipient starting "66400" gets a 400 for buttons / lists (tests the plain-text fallback).
        app.MapPost("/{version}/{phoneNumberId}/messages", async (string version, string phoneNumberId, HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var raw = body?.ToJsonString() ?? "{}";
            var to = body?["to"]?.GetValue<string>() ?? "";
            var status = to.StartsWith("66400") && raw.Contains("\"interactive\"") ? 400 : 200;
            MetaCalls.Enqueue(new MetaCall("whatsapp", to, raw, ctx.Request.Headers.Authorization.ToString(), status, DateTime.UtcNow));
            return status == 200
                ? Results.Json(new { messaging_product = "whatsapp", messages = new[] { new { id = "wamid.fake" + Guid.NewGuid().ToString("N") } } })
                : Results.Json(new { error = new { message = "(#131009) Parameter value is not valid", code = 131009 } }, statusCode: 400);
        });
        app.MapPost("/{version}/me/messages", async (string version, HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var to = body?["recipient"]?["id"]?.GetValue<string>() ?? "";
            MetaCalls.Enqueue(new MetaCall("messenger", to, body?.ToJsonString() ?? "{}", ctx.Request.Headers.Authorization.ToString(), 200, DateTime.UtcNow));
            return Results.Json(new { recipient_id = to, message_id = "m_fake" + Guid.NewGuid().ToString("N") });
        });
        // Who-am-I checks and the Messenger name lookup. A token containing "bad" is refused like an expired one.
        app.MapGet("/{version}/{id}", (string version, string id, HttpContext ctx) =>
        {
            if (ctx.Request.Headers.Authorization.ToString().Contains("bad"))
                return Results.Json(new { error = new { message = "Error validating access token", code = 190 } }, statusCode: 401);
            // A Page token without pages_read_engagement: Meta refuses to read the Page (messaging still works).
            if (id == "me" && ctx.Request.Headers.Authorization.ToString().Contains("noread"))
                return Results.Json(new { error = new { message = "(#100) This endpoint requires the 'pages_read_engagement' permission", code = 100 } }, statusCode: 400);
            if (id == "me") return Results.Json(new { name = "E2E Page", id = "1000" });
            if (ctx.Request.Query["fields"].ToString().Contains("display_phone_number"))
                return Results.Json(new { display_phone_number = "+66 80 000 0000", verified_name = "E2E WhatsApp Shop", id });
            return Results.Json(new { first_name = "Maria", last_name = "Santos", id });
        });

        app.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var system = body?["system"]?.GetValue<string>() ?? "";
            var user = body?["messages"]?[0]?["content"]?.GetValue<string>() ?? "";
            ClaudeCalls.Enqueue(new ClaudeCall(system, user, ctx.Request.Headers["x-api-key"].ToString(), DateTime.UtcNow));

            if (user.Contains("[slow]")) await Task.Delay(3000);
            if (user.Contains("[aidown]"))
                return Results.Json(new { type = "error", error = new { type = "api_error", message = "fake outage" } }, statusCode: 500);

            var text = ClaudeReplyFor(system, user);
            return Results.Json(new
            {
                id = "msg_fake",
                type = "message",
                role = "assistant",
                model = "fake",
                content = new[] { new { type = "text", text } },
                stop_reason = "end_turn",
                // Like Anthropic: tokens used, so the AI cost report has something to add up.
                // Fixed numbers so tests can check the cost: 1,000 in + 200 out at $3 / $15 per million = $0.006.
                usage = new { input_tokens = 1000, output_tokens = 200, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 }
            });
        });

        // ---------------- POSFirst ----------------
        // A POS whose API key starts "pos-down" answers 503 until the test calls RecoverPos(key).
        // "pos-500" always answers 500 (must NOT be retried: the order might have been created).
        app.MapPost("/api/orders", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
            var idem = ctx.Request.Headers["Idempotency-Key"].ToString();
            var status = apiKey.StartsWith("pos-down") && !PosRecovered.ContainsKey(apiKey) ? 503 : apiKey == "pos-500" ? 500 : 200;
            PosAttempts.Enqueue(new PosAttempt("/api/orders", apiKey, idem, status, DateTime.UtcNow));
            if (status != 200) return Results.Json(new { message = "fake POS is down" }, statusCode: status);

            var orderId = $"POS-{Interlocked.Increment(ref _orderCounter):0000}";
            PosOrders.Enqueue(new PosOrder(orderId, body?["customerId"]?.GetValue<string>() ?? "",
                body?.ToJsonString() ?? "{}", apiKey, DateTime.UtcNow, idem));
            return Results.Json(new { orderId });
        });

        // "lowstock-down" makes the low-stock check fail (a scheduled job that fails -> retries).
        app.MapGet("/api/inventory/low-stock", (HttpContext ctx) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
            var status = apiKey == "lowstock-down" ? 503 : 200;
            PosAttempts.Enqueue(new PosAttempt("/api/inventory/low-stock", apiKey, "", status, DateTime.UtcNow));
            return status == 200
                ? Results.Json(new { items = new[] { new { name = "Butter" } } })
                : Results.Json(new { message = "fake POS is down" }, statusCode: status);
        });

        await app.StartAsync();
        _app = app;
    }

    /// <summary>The display name the fake LINE gives a customer.</summary>
    public static string CustomerNameFor(string userId) => "Khun " + userId[^Math.Min(6, userId.Length)..];

    /// <summary>The fake "AI": decides the reply from the customer's text, like the real prompt asks.</summary>
    public static string ClaudeReplyFor(string system, string user)
    {
        var text = user.ToLowerInvariant();
        string Json(object o) => JsonSerializer.Serialize(o);

        if (text.Contains("[badjson]"))
            return "Sorry, I can't help with that.";

        // Round 006: translating an order notice. "[lang] same text" proves which language was asked for;
        // a cancel reason containing "notranslate" makes the fake answer nonsense (the app must then send English).
        if (system.StartsWith("You translate one short chat message"))
        {
            var lang = Regex.Match(system, @"into this language: ([^.]+)\.").Groups[1].Value;
            return text.Contains("notranslate") ? "???" : $"[{lang}] {user}";
        }

        // Round 008: the customer typed an answer to "When do you want it?".
        if (system.StartsWith("A shop asked its customer"))
        {
            var day = Regex.Match(system, @"Local date: (\d{4}-\d{2}-\d{2})").Groups[1].Value;
            var hour = Regex.Match(text, @"\b(\d{1,2})\s*pm\b");
            if (hour.Success) return Json(new { time = $"{day}T{int.Parse(hour.Groups[1].Value) % 12 + 12:00}:00", asap = false });
            return Json(new { time = (string?)null, asap = text.Contains("now") || text.Contains("asap") });
        }

        // Beauty & wellness: the booking assistant. Finds a service named in SERVICES, "tomorrow"/"bukas",
        // and a time like "3pm"; Taglish words ("pwede", "po", "bukas") -> language "tl".
        if (system.StartsWith("BOOKING ASSISTANT"))
        {
            var lang = Regex.IsMatch(text, @"\b(pwede|po|bukas|magkano|ba)\b") ? "tl" : "en";
            var today = DateOnly.ParseExact(Regex.Match(system, @"Local date: (\d{4}-\d{2}-\d{2})").Groups[1].Value, "yyyy-MM-dd");
            object Reply(string intent, string reply, bool canAnswer = true, bool needsHuman = false, string? service = null, string? date = null, string? time = null) =>
                new { intent, language = lang, reply, canAnswer, needsHuman, service, date, time };

            if (text.Contains("pregnant") || text.Contains("refund"))
                return Json(Reply("question", "A staff member will reply to you soon.", false, true));
            if (text.Contains("parking"))
                return Json(Reply("question", "Let me ask our team - they will reply soon.", false, true));
            if (text.Contains("my booking") || text.Contains("cancel my"))
                return Json(Reply("my_bookings", "Here are your bookings."));
            if (text.Contains("price") || text.Contains("services") || text.Contains("magkano lahat"))
                return Json(Reply("services", "Here are our services."));
            if (Regex.IsMatch(text, @"\b(book|pwede|available|reserve)\b"))
            {
                var names = Regex.Matches(system, @"^- ([^:\n]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
                var service = names.FirstOrDefault(n => text.Contains(n.ToLowerInvariant()));
                string? date = text.Contains("tomorrow") || text.Contains("bukas") ? today.AddDays(1).ToString("yyyy-MM-dd")
                    : text.Contains("today") || text.Contains("ngayon") ? today.ToString("yyyy-MM-dd") : null;
                var tm = Regex.Match(text, @"\b(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b");
                string? time = tm.Success
                    ? $"{int.Parse(tm.Groups[1].Value) % 12 + (tm.Groups[3].Value == "pm" ? 12 : 0):00}:{(tm.Groups[2].Success ? tm.Groups[2].Value : "00")}"
                    : null;
                return Json(Reply("book", lang == "tl" ? "Sige po! Hanapan natin kayo ng oras." : "Sure! Let's find you a time.", service: service, date: date, time: time));
            }
            if (text.Contains("open") || text.Contains("hours"))
                return Json(Reply("question", "We are open 10:00-21:00 every day."));
            if (Regex.IsMatch(text, @"\b(hello|hi|good morning|good afternoon)\b"))
                return Json(Reply("greeting", "Hi! Welcome to our spa."));
            return Json(Reply("other", "Thanks for your message!"));
        }

        // The language the customer wrote in (Thai letters -> "th"), as the real AI reports it.
        var language = Regex.IsMatch(user, @"[\u0E00-\u0E7F]") ? "th" : "en";

        var orderItem = text.Contains("croissant") || text.Contains("ครัวซองต์") ? "Croissant"
            : text.Contains("catering") ? "Catering package"
            : null;
        if (orderItem is not null)
        {
            var m = Regex.Match(text, @"\d+");
            var qty = m.Success ? int.Parse(m.Value) : 1;

            // Like the real prompt asks: resolve "tomorrow 9am" against the business clock in the system prompt.
            string? delivery = null;
            var localDate = Regex.Match(system, @"Local date: (\d{4}-\d{2}-\d{2})");
            if (text.Contains("tomorrow 9am") && localDate.Success)
                delivery = DateTime.ParseExact(localDate.Groups[1].Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    .AddDays(1).ToString("yyyy-MM-dd") + "T09:00";

            return Json(new
            {
                intent = "order", language,
                reply = $"Order confirmed: {qty} x {orderItem}. Your ref is {{ORDER_REF}}.",
                canAnswer = false, needsHuman = false,
                order = new
                {
                    items = new[] { new { name = orderItem, quantity = qty } },
                    requestedDeliveryTime = delivery,
                    requestedDeliveryText = delivery is null ? null : "tomorrow 9am",
                    customerNote = (string?)null
                }
            });
        }

        // Round 004: structured-menu items. The fake AI passes the customer's own words through
        // (e.g. "tea"), so our alias matching and sold-out checks are what's being tested.
        var cafeItems = Regex.Matches(text, @"(\d+)\s*(?:x\s*)?(chai|tea|samosa|paneer roll|cake|shawarma)\b");
        if (cafeItems.Count > 0)
        {
            var items = cafeItems.Select(x => new { name = x.Groups[2].Value, quantity = int.Parse(x.Groups[1].Value) }).ToArray();

            string? delivery = null;
            var localDate = Regex.Match(system, @"Local date: (\d{4}-\d{2}-\d{2})");
            if (text.Contains("tomorrow 9am") && localDate.Success)
                delivery = DateTime.ParseExact(localDate.Groups[1].Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    .AddDays(1).ToString("yyyy-MM-dd") + "T09:00";

            return Json(new
            {
                intent = "order", language,
                reply = "Order confirmed: " + string.Join(", ", items.Select(i => $"{i.quantity} x {i.name}")) + ". Your ref is {ORDER_REF}.",
                canAnswer = false, needsHuman = false,
                order = new
                {
                    items,
                    requestedDeliveryTime = delivery,
                    requestedDeliveryText = delivery is null ? null : "tomorrow 9am",
                    customerNote = text.Contains("less sugar") ? "less sugar" : null
                }
            });
        }

        // Round 008: "what do you sell?" - with menu buttons on, the real prompt asks for intent "menu".
        if (text.Contains("menu") || text.Contains("what do you sell") || user.Contains("เมนู"))
            return Json(new
            {
                intent = system.Contains("use intent \"menu\"") ? "menu" : "question",
                language,
                reply = language == "th" ? "ได้เลยค่ะ" : "Sure! Here is our menu.",
                canAnswer = true, needsHuman = false, order = (object?)null
            });

        // Answered only when the client's business information mentions parking (Round 005 portal test).
        if (text.Contains("parking"))
        {
            var line = Regex.Match(system, @"^.*parking.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var known = line.Success && system.IndexOf("BUSINESS INFORMATION:", StringComparison.Ordinal) < line.Index;
            return Json(new
            {
                intent = "question", language = "en",
                reply = known ? line.Value.Trim() : "A staff member will get back to you soon.",
                canAnswer = known, needsHuman = !known, order = (object?)null
            });
        }

        if (text.Contains("open") || text.Contains("hours"))
        {
            // Structured hours: answer from the "RIGHT NOW:" line our code puts in the prompt.
            var now = Regex.Match(system, @"RIGHT NOW: (.+)");
            if (now.Success && system.Contains("OPENING HOURS"))
                return Json(new { intent = "question", language = "en", reply = "We are " + now.Groups[1].Value.Trim(),
                    canAnswer = true, needsHuman = false, order = (object?)null });

            var known = system.Contains("Opening hours");
            return Json(new
            {
                intent = "question", language = "en",
                reply = known ? "We are open 7am-6pm every day." : "A staff member will get back to you soon.",
                canAnswer = known, needsHuman = !known, order = (object?)null
            });
        }

        if (text.Contains("refund"))
            return Json(new { intent = "other", language = "en", reply = "Sorry to hear that - a staff member will contact you shortly.",
                canAnswer = false, needsHuman = true, order = (object?)null });

        if (Regex.IsMatch(text, @"\b(hello|hi)\b"))
            return Json(new { intent = "greeting", language = "en", reply = "Hello! How can we help you today?",
                canAnswer = false, needsHuman = false, order = (object?)null });

        return Json(new { intent = "other", language = "en", reply = "Thanks for your message!",
            canAnswer = false, needsHuman = false, order = (object?)null });
    }

    public IReadOnlyList<LineCall> LineCallsFor(string userOrToken) =>
        LineCalls.Where(c => c.To == userOrToken || (c.ReplyToken?.Contains(userOrToken) ?? false)).ToList();

    public System.Collections.Concurrent.ConcurrentQueue<PosAttempt> PosAttempts { get; } = new();

    /// <summary>"pos-down..." keys that are up again.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> PosRecovered { get; } = new();
    public void RecoverPos(string apiKey) => PosRecovered[apiKey] = true;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> LineRetryKeysSeen = new();

    public IReadOnlyList<PosOrder> OrdersFor(string customerId) =>
        PosOrders.Where(o => o.CustomerId == customerId).ToList();

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync();
        try { return JsonNode.Parse(raw); } catch (JsonException) { return null; }
    }

    private static string FirstText(JsonNode? body) => body?["messages"]?[0]?["text"]?.GetValue<string>() ?? "";

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
