using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Sends outbound messages on LINE, WhatsApp and Messenger and REPORTS whether it worked
/// (the old version only logged failures, so failed sends showed as "Succeeded").
///
/// Customer-facing sends use each client's OWN credentials (IClientSettingsService).
/// When a client has no credentials for the channel, the send runs in "mock" mode: it is only
/// logged and counts as success - handy while setting a client up.
///
/// LINE: replies with the reply token first (free); if that fails (token expired/used),
/// falls back to a push message (counts against the LINE plan's monthly quota).
///
/// Config: Connectors:Line:ApiBaseUrl (default https://api.line.me/),
///         Connectors:Meta:GraphBaseUrl (default https://graph.facebook.com/),
///         Connectors:Meta:GraphApiVersion (default v23.0 - check Meta's changelog for the current version).
/// </summary>
public class MessagingConnector : IMessagingConnector
{
    private const int LineMaxTextLength = 5000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IClientSettingsService _settings;
    private readonly ILogger<MessagingConnector> _logger;

    public MessagingConnector(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        IClientSettingsService settings, ILogger<MessagingConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    private Uri LineBase => BaseUri(_configuration["Connectors:Line:ApiBaseUrl"], "https://api.line.me/");
    private Uri GraphBase => BaseUri(_configuration["Connectors:Meta:GraphBaseUrl"], "https://graph.facebook.com/");
    private string GraphVersion => _configuration["Connectors:Meta:GraphApiVersion"] is { Length: > 0 } v ? v.Trim('/') : "v23.0";

    private static Uri BaseUri(string? configured, string fallback)
    {
        var url = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        return new Uri(url.EndsWith('/') ? url : url + "/");
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("SET_", StringComparison.OrdinalIgnoreCase);

    // ---------------- Customer-facing sends ----------------

    public async Task<MessageSendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            return message.Channel switch
            {
                Channels.Line => await SendLineAsync(message, cancellationToken),
                Channels.WhatsApp => await SendWhatsAppAsync(message, cancellationToken),
                Channels.Messenger => await SendMessengerAsync(message, cancellationToken),
                _ => MessageSendResult.Fail("send", $"Unknown channel '{message.Channel}'.")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Channel} send failed for client {ClientConfigId}", message.Channel, message.ClientConfigId);
            return MessageSendResult.Fail("send", ex.Message);
        }
    }

    private async Task<MessageSendResult> SendLineAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(message.ClientConfigId, cancellationToken);
        var text = message.Text.Length > LineMaxTextLength ? message.Text[..LineMaxTextLength] : message.Text;

        if (!settings.HasLineToken)
        {
            _logger.LogWarning("MOCK (LINE) - client {ClientConfigId} has no LINE channel access token. Would have sent to {To}: \"{Text}\"",
                message.ClientConfigId, message.To, text);
            return MessageSendResult.Ok("mock");
        }

        var hasRich = message.Rich is { IsEmpty: false };
        var result = await SendLineMessagesAsync(settings.LineChannelAccessToken!, message, LineMessageBuilder.Build(text, message.Rich), cancellationToken);

        // Safety net: if LINE refuses the buttons / cards (a 400), the customer still gets the words.
        if (!result.Success && hasRich && result.Error is { } error && error.Contains("400 "))
        {
            _logger.LogWarning("LINE refused buttons/cards for client {ClientConfigId} ({Error}) - sending plain text instead", message.ClientConfigId, error);
            var plain = LineMessageBuilder.PlainText(text, message.Rich);
            var retry = await SendLineMessagesAsync(settings.LineChannelAccessToken!, message, LineMessageBuilder.Build(plain, null), cancellationToken);
            if (retry.Success) return MessageSendResult.Ok(retry.Mode + ", text only - LINE refused the buttons");
        }

        return result;
    }

    private async Task<MessageSendResult> SendLineMessagesAsync(string accessToken, OutboundMessage message, object[] messages, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("LineApi");
        string? replyError = null;

        if (!string.IsNullOrWhiteSpace(message.ReplyToken))
        {
            using var reply = new HttpRequestMessage(HttpMethod.Post, new Uri(LineBase, "v2/bot/message/reply"))
            {
                Content = Json(new { replyToken = message.ReplyToken, messages })
            };
            reply.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var replyResponse = await client.SendAsync(reply, cancellationToken);
            if (replyResponse.IsSuccessStatusCode) return MessageSendResult.Ok("reply");

            replyError = $"{(int)replyResponse.StatusCode} {await replyResponse.Content.ReadAsStringAsync(cancellationToken)}";
            _logger.LogWarning("LINE reply failed for client {ClientConfigId} ({Error}) - falling back to push", message.ClientConfigId, replyError);
        }

        if (string.IsNullOrWhiteSpace(message.To))
            return MessageSendResult.Fail("reply", $"Reply failed ({replyError}) and there is no user id to push to.");

        // The same retry key on every try: LINE delivers the message once even if we have to repeat it.
        var retryKey = Guid.NewGuid().ToString();
        using var pushResponse = await HttpRetry.SendAsync(ct =>
        {
            var push = new HttpRequestMessage(HttpMethod.Post, new Uri(LineBase, "v2/bot/message/push"))
            {
                Content = Json(new { to = message.To, messages })
            };
            push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            push.Headers.Add("X-Line-Retry-Key", retryKey);
            return client.SendAsync(push, ct);
        }, "LINE push", _logger, cancellationToken, safeToRepeat: true);

        // 409 = LINE already accepted this retry key on an earlier try: it was delivered.
        if ((int)pushResponse.StatusCode == 409) return MessageSendResult.Ok(replyError is null ? "push" : "push (reply token failed)");
        if (pushResponse.IsSuccessStatusCode) return MessageSendResult.Ok(replyError is null ? "push" : "push (reply token failed)");

        var pushError = $"{(int)pushResponse.StatusCode} {await pushResponse.Content.ReadAsStringAsync(cancellationToken)}";
        _logger.LogError("LINE push failed for client {ClientConfigId}: {Error}", message.ClientConfigId, pushError);
        return MessageSendResult.Fail("push", replyError is null ? pushError : $"reply: {replyError}; push: {pushError}");
    }

    private async Task<MessageSendResult> SendWhatsAppAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        // Buttons / cards are LINE-only for now: other channels get the same content as words.
        if (message.Rich is not null) message.Text = LineMessageBuilder.PlainText(message.Text, message.Rich);
        var settings = await _settings.GetAsync(message.ClientConfigId, cancellationToken);
        if (!settings.HasWhatsApp)
        {
            _logger.LogWarning("MOCK (WhatsApp) - client {ClientConfigId} has no WhatsApp phone number id/token. Would have sent to {To}: \"{Text}\"",
                message.ClientConfigId, message.To, message.Text);
            return MessageSendResult.Ok("mock");
        }

        return await PostGraphAsync($"{settings.WhatsAppPhoneNumberId}/messages", settings.WhatsAppAccessToken!,
            new { messaging_product = "whatsapp", to = message.To, type = "text", text = new { body = message.Text } },
            "WhatsApp", cancellationToken);
    }

    private async Task<MessageSendResult> SendMessengerAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (message.Rich is not null) message.Text = LineMessageBuilder.PlainText(message.Text, message.Rich);
        var settings = await _settings.GetAsync(message.ClientConfigId, cancellationToken);
        if (!settings.HasMessenger)
        {
            _logger.LogWarning("MOCK (Messenger) - client {ClientConfigId} has no page access token. Would have sent to PSID {To}: \"{Text}\"",
                message.ClientConfigId, message.To, message.Text);
            return MessageSendResult.Ok("mock");
        }

        // Token goes in the Authorization header, NOT the URL - URLs end up in log files.
        return await PostGraphAsync("me/messages", settings.MessengerPageAccessToken!,
            new { recipient = new { id = message.To }, messaging_type = "RESPONSE", message = new { text = message.Text } },
            "Messenger", cancellationToken);
    }

    private async Task<MessageSendResult> PostGraphAsync(string path, string accessToken, object payload, string label,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(label == "Messenger" ? "MessengerApi" : "WhatsAppApi");
        // Meta has no duplicate protection: only retried when the message certainly did not arrive.
        using var response = await HttpRetry.SendAsync(ct =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(GraphBase, $"{GraphVersion}/{path}"))
            {
                Content = Json(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client.SendAsync(request, ct);
        }, $"{label} send", _logger, cancellationToken, safeToRepeat: false);
        if (response.IsSuccessStatusCode) return MessageSendResult.Ok("send");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("{Label} send failed: {StatusCode} {Body}", label, (int)response.StatusCode, body);
        return MessageSendResult.Fail("send", $"{(int)response.StatusCode} {body}");
    }

    // ---------------- Customer display name ----------------

    public async Task<string?> GetCustomerNameAsync(Guid clientConfigId, string channel, string customerId, CancellationToken cancellationToken = default)
    {
        // LINE only for now: WhatsApp sends the name inside the webhook, Messenger needs an extra permission.
        if (channel != Channels.Line || string.IsNullOrWhiteSpace(customerId)) return null;

        try
        {
            var settings = await _settings.GetAsync(clientConfigId, cancellationToken);
            if (!settings.HasLineToken) return null;

            var client = _httpClientFactory.CreateClient("LineApi");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LineBase, $"v2/bot/profile/{Uri.EscapeDataString(customerId)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.LineChannelAccessToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var name = doc.RootElement.TryGetProperty("displayName", out var n) ? n.GetString() : null;
            return string.IsNullOrWhiteSpace(name) ? null : (name.Length > 200 ? name[..200] : name).Trim();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the LINE display name of {CustomerId} for client {ClientConfigId}", customerId, clientConfigId);
            return null;
        }
    }

    // ---------------- Internal ops notifications (MadeeAutomation's own WhatsApp number) ----------------

    public async Task<MessageSendResult> SendInternalNotificationAsync(string message, CancellationToken cancellationToken = default)
    {
        var phoneNumberId = _configuration["Connectors:WhatsApp:PhoneNumberId"];
        var accessToken = _configuration["Connectors:WhatsApp:AccessToken"];
        var notifyTo = _configuration["Connectors:WhatsApp:InternalNotifyTo"];

        if (IsPlaceholder(phoneNumberId) || IsPlaceholder(accessToken) || IsPlaceholder(notifyTo))
        {
            _logger.LogWarning("MOCK (internal ops) - no internal WhatsApp credentials configured. Would have sent: \"{Message}\"", message);
            return MessageSendResult.Ok("mock");
        }

        try
        {
            return await PostGraphAsync($"{phoneNumberId}/messages", accessToken!,
                new { messaging_product = "whatsapp", to = notifyTo, type = "text", text = new { body = message } },
                "Internal WhatsApp", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return MessageSendResult.Fail("send", ex.Message);
        }
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
