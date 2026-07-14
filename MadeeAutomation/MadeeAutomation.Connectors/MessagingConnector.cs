using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

/// <summary>
/// Routes outbound messages to WhatsApp Business API or LINE Messaging API.
/// Customer-facing sends (SendTextMessageAsync) use each client's OWN credentials, looked
/// up from ClientConfig.IntegrationSettingsJson - never a shared/global account, since every
/// client has their own WhatsApp Business number or LINE Official Account.
/// Internal-ops sends (SendInternalNotificationAsync) use MadeeAutomation's own single
/// WhatsApp number (from appsettings/user-secrets) since that's your team's channel, not
/// any particular client's.
/// </summary>
public class MessagingConnector : IMessagingConnector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ICurrentClientService _currentClientService;
    private readonly ILogger<MessagingConnector> _logger;

    public MessagingConnector(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        ICurrentClientService currentClientService, ILogger<MessagingConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _currentClientService = currentClientService;
        _logger = logger;
    }

    // ---------- Customer-facing sends: each client's own credentials ----------

    public async Task SendTextMessageAsync(string channel, string toAddress, string message, Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        switch (channel.ToLowerInvariant())
        {
            case "whatsapp":
                await SendClientWhatsAppAsync(clientConfigId, toAddress, message, cancellationToken);
                break;
            case "line":
                await SendClientLineAsync(clientConfigId, toAddress, message, cancellationToken);
                break;
            default:
                _logger.LogWarning("Unknown messaging channel '{Channel}' - message not sent.", channel);
                break;
        }
    }

    private async Task<(string? phoneNumberId, string? accessToken)> GetClientWhatsAppSettingsAsync(Guid clientConfigId, CancellationToken cancellationToken)
    {
        var client = await _currentClientService.GetClientAsync(clientConfigId, cancellationToken);
        if (client is null) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(client.IntegrationSettingsJson);
            var phoneId = doc.RootElement.TryGetProperty("whatsAppPhoneNumberId", out var p) ? p.GetString() : null;
            var token = doc.RootElement.TryGetProperty("whatsAppAccessToken", out var t) ? t.GetString() : null;
            return (phoneId, token);
        }
        catch (JsonException) { return (null, null); }
    }

    private async Task<string?> GetClientLineTokenAsync(Guid clientConfigId, CancellationToken cancellationToken)
    {
        var client = await _currentClientService.GetClientAsync(clientConfigId, cancellationToken);
        if (client is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(client.IntegrationSettingsJson);
            return doc.RootElement.TryGetProperty("lineChannelAccessToken", out var t) ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task SendClientWhatsAppAsync(Guid clientConfigId, string toAddress, string message, CancellationToken cancellationToken)
    {
        var (phoneNumberId, accessToken) = await GetClientWhatsAppSettingsAsync(clientConfigId, cancellationToken);

        if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning(
                "MessagingConnector MOCK MODE (WhatsApp) - client {ClientConfigId} has no whatsAppPhoneNumberId/AccessToken configured. " +
                "Would have sent to {ToAddress}: \"{Message}\"", clientConfigId, toAddress, message);
            return;
        }

        var client = _httpClientFactory.CreateClient("WhatsAppApi");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new { messaging_product = "whatsapp", to = toAddress, type = "text", text = new { body = message } };
        var json = JsonSerializer.Serialize(payload);
        var response = await client.PostAsync($"https://graph.facebook.com/v19.0/{phoneNumberId}/messages",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("WhatsApp send failed for client {ClientConfigId}: {StatusCode} {Body}", clientConfigId, response.StatusCode, body);
        }
    }

    private async Task SendClientLineAsync(Guid clientConfigId, string toAddress, string message, CancellationToken cancellationToken)
    {
        var token = await GetClientLineTokenAsync(clientConfigId, cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning(
                "MessagingConnector MOCK MODE (LINE) - client {ClientConfigId} has no lineChannelAccessToken configured. " +
                "Would have sent to {ToAddress}: \"{Message}\"", clientConfigId, toAddress, message);
            return;
        }

        var client = _httpClientFactory.CreateClient("LineApi");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var payload = new { to = toAddress, messages = new[] { new { type = "text", text = message } } };
        var json = JsonSerializer.Serialize(payload);
        var response = await client.PostAsync("https://api.line.me/v2/bot/message/push",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("LINE send failed for client {ClientConfigId}: {StatusCode} {Body}", clientConfigId, response.StatusCode, body);
        }
    }

    // ---------- Internal ops notifications: MadeeAutomation's own single channel ----------

    public async Task SendInternalNotificationAsync(string message, CancellationToken cancellationToken = default)
    {
        var phoneNumberId = _configuration["Connectors:WhatsApp:PhoneNumberId"];
        var accessToken = _configuration["Connectors:WhatsApp:AccessToken"];
        var notifyTo = _configuration["Connectors:WhatsApp:InternalNotifyTo"] ?? "internal-ops-group";

        if (string.IsNullOrWhiteSpace(phoneNumberId) || phoneNumberId == "SET_ME" ||
            string.IsNullOrWhiteSpace(accessToken) || accessToken == "SET_VIA_USER_SECRETS_OR_ENV_VAR")
        {
            _logger.LogWarning(
                "MessagingConnector MOCK MODE (internal ops) - no internal WhatsApp credentials configured in appsettings/user-secrets. " +
                "Would have sent to {NotifyTo}: \"{Message}\"", notifyTo, message);
            return;
        }

        var client = _httpClientFactory.CreateClient("WhatsAppApi");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new { messaging_product = "whatsapp", to = notifyTo, type = "text", text = new { body = message } };
        var json = JsonSerializer.Serialize(payload);
        var response = await client.PostAsync($"https://graph.facebook.com/v19.0/{phoneNumberId}/messages",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Internal notification send failed: {StatusCode} {Body}", response.StatusCode, body);
        }
    }
}
