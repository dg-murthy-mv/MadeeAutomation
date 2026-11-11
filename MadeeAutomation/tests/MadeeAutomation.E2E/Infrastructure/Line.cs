using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MadeeAutomation.E2E.Infrastructure;

public record WebhookResponse(HttpStatusCode Status, string Body, long ElapsedMs)
{
    public JsonElement Json => JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "{}" : Body).RootElement;
    public int Int(string name) => Json.TryGetProperty(name, out var v) ? v.GetInt32() : -1;
}

/// <summary>Builds LINE webhook bodies, signs them like LINE does, and posts them to the WebApi.</summary>
public static class Line
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A fresh user id per test so recorded calls never mix between tests.</summary>
    public static string NewUser(string label) => $"U{label}{Guid.NewGuid():N}"[..33];

    /// <summary>One LINE text-message event. The reply token embeds the user id so recorded replies can be matched.</summary>
    public static object TextEvent(string userId, string text, string? eventId = null, string? replyToken = null) => new
    {
        type = "message",
        mode = "active",
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        webhookEventId = eventId ?? "E" + Guid.NewGuid().ToString("N"),
        deliveryContext = new { isRedelivery = false },
        replyToken = replyToken ?? "rt-" + userId,
        source = new { type = "user", userId },
        message = new { id = Guid.NewGuid().ToString("N")[..12], type = "text", text, quoteToken = "q" }
    };

    /// <summary>The customer tapped a button we sent. picked = what they chose in the date-and-time picker.</summary>
    public static object TapEvent(string userId, string data, string? picked = null) => new
    {
        type = "postback",
        mode = "active",
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        webhookEventId = "E" + Guid.NewGuid().ToString("N"),
        replyToken = "rt-" + userId,
        source = new { type = "user", userId },
        postback = picked is null ? (object)new { data } : new { data, @params = new { datetime = picked } }
    };

    /// <summary>The customer added the account as a friend.</summary>
    public static object FollowEvent(string userId) => new
    {
        type = "follow",
        mode = "active",
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        webhookEventId = "E" + Guid.NewGuid().ToString("N"),
        replyToken = "rt-" + userId,
        source = new { type = "user", userId }
    };

    public static object StickerEvent(string userId) => new
    {
        type = "message",
        mode = "active",
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        webhookEventId = "E" + Guid.NewGuid().ToString("N"),
        replyToken = "rt-" + userId,
        source = new { type = "user", userId },
        message = new { id = "s1", type = "sticker", packageId = "446", stickerId = "1988" }
    };

    public static string Body(params object[] events) =>
        JsonSerializer.Serialize(new { destination = "Ubotdestination", events });

    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    /// <summary>POSTs a body to a webhook path. signatureSecret null = send no signature header.</summary>
    public static async Task<WebhookResponse> PostAsync(string path, string body, string? signatureSecret, string? signatureOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MadeeAutomation.E2E.GlobalSetup.Settings.WebApiUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var signature = signatureOverride ?? (signatureSecret is null ? null : Sign(signatureSecret, body));
        if (signature is not null) request.Headers.TryAddWithoutValidation("X-Line-Signature", signature);

        var sw = Stopwatch.StartNew();
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new WebhookResponse(response.StatusCode, text, sw.ElapsedMilliseconds);
    }

    /// <summary>Signs with the client's secret and posts to /api/webhooks/line/{code}.</summary>
    public static Task<WebhookResponse> SendAsync(SeededClient client, params object[] events)
    {
        var body = Body(events);
        return PostAsync($"/api/webhooks/line/{client.Code}", body, client.LineSecret);
    }
}

/// <summary>Polling helper - waits for something the background worker does.</summary>
public static class Eventually
{
    public static async Task<T> GetAsync<T>(Func<Task<T>> read, Func<T, bool> done, int timeoutMs = 15000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value = await read();
        while (!done(value))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what ?? "condition"}.");
            await Task.Delay(250);
            value = await read();
        }
        return value;
    }

    public static Task<T> GetAsync<T>(Func<T> read, Func<T, bool> done, int timeoutMs = 15000, string? what = null) =>
        GetAsync(() => Task.FromResult(read()), done, timeoutMs, what);
}
