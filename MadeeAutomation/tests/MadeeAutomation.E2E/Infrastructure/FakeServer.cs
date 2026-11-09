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
public record LineCall(string Kind, string? To, string? ReplyToken, string Text, string Authorization, int StatusReturned, DateTime AtUtc);

/// <summary>One call the apps made to the fake Claude API.</summary>
public record ClaudeCall(string System, string UserMessage, string ApiKey, DateTime AtUtc);

/// <summary>One order the apps created in the fake POSFirst.</summary>
public record PosOrder(string OrderId, string CustomerId, string BodyJson, string ApiKey, DateTime AtUtc);

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
            LineCalls.Enqueue(new LineCall("reply", null, token, text, auth, status, DateTime.UtcNow));

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
            LineCalls.Enqueue(new LineCall("push", to, null, FirstText(body), auth, status, DateTime.UtcNow));

            return status == 500
                ? Results.Json(new { message = "fake LINE failure" }, statusCode: 500)
                : Results.Json(new { sentMessages = new[] { new { id = "2" } } });
        });

        app.MapGet("/v2/bot/info", (HttpContext ctx) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            return auth.EndsWith("fail-token") || !auth.StartsWith("Bearer ")
                ? Results.Json(new { message = "Authentication failed." }, statusCode: 401)
                : Results.Json(new { userId = "Ubot", basicId = "@e2ebot", displayName = "E2E Bot", chatMode = "bot" });
        });

        // ---------------- Anthropic Messages API ----------------
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
                stop_reason = "end_turn"
            });
        });

        // ---------------- POSFirst ----------------
        app.MapPost("/api/orders", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            var orderId = $"POS-{Interlocked.Increment(ref _orderCounter):0000}";
            PosOrders.Enqueue(new PosOrder(orderId, body?["customerId"]?.GetValue<string>() ?? "",
                body?.ToJsonString() ?? "{}", ctx.Request.Headers["X-Api-Key"].ToString(), DateTime.UtcNow));
            return Results.Json(new { orderId });
        });

        app.MapGet("/api/inventory/low-stock", () => Results.Json(new { items = new[] { new { name = "Butter" } } }));

        await app.StartAsync();
        _app = app;
    }

    /// <summary>The fake "AI": decides the reply from the customer's text, like the real prompt asks.</summary>
    public static string ClaudeReplyFor(string system, string user)
    {
        var text = user.ToLowerInvariant();
        string Json(object o) => JsonSerializer.Serialize(o);

        if (text.Contains("[badjson]"))
            return "Sorry, I can't help with that.";

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
                intent = "order", language = "en",
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

        if (text.Contains("open") || text.Contains("hours"))
        {
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
