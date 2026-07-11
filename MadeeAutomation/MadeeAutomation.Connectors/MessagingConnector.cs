using System.Text;
using System.Text.Json;
using MadeeAutomation.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Connectors;

public class MessagingConnector : IMessagingConnector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MessagingConnector> _logger;

    public MessagingConnector(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MessagingConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendTextMessageAsync(string channel, string toAddress, string message, CancellationToken cancellationToken = default)
    {
        switch (channel.ToLowerInvariant())
        {
            case "whatsapp":
                await SendWhatsAppAsync(toAddress, message, cancellationToken);
                break;
            case "line":
                await SendLineAsync(toAddress, message, cancellationToken);
                break;
            default:
                _logger.LogWarning("Unknown messaging channel '{Channel}' - message not sent.", channel);
                break;
        }
    }

    private bool IsWhatsAppConfigured() =>
        !string.IsNullOrWhiteSpace(_configuration["Connectors:WhatsApp:AccessToken"]) &&
        _configuration["Connectors:WhatsApp:AccessToken"] != "SET_VIA_USER_SECRETS_OR_ENV_VAR" &&
        !string.IsNullOrWhiteSpace(_configuration["Connectors:WhatsApp:PhoneNumberId"]) &&
        _configuration["Connectors:WhatsApp:PhoneNumberId"] != "SET_ME";

    private bool IsLineConfigured() =>
        !string.IsNullOrWhiteSpace(_configuration["Connectors:Line:ChannelAccessToken"]) &&
        _configuration["Connectors:Line:ChannelAccessToken"] != "SET_VIA_USER_SECRETS_OR_ENV_VAR";

    private async Task SendWhatsAppAsync(string toAddress, string message, CancellationToken cancellationToken)
    {
        if (!IsWhatsAppConfigured())
        {
            _logger.LogWarning(
                "MessagingConnector running in MOCK MODE for WhatsApp (no real credentials configured). " +
                "Would have sent to {ToAddress}: \"{Message}\"", toAddress, message);
            return;
        }

        var client = _httpClientFactory.CreateClient("WhatsAppApi");
        var phoneNumberId = _configuration["Connectors:WhatsApp:PhoneNumberId"];
        var token = _configuration["Connectors:WhatsApp:AccessToken"];

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toAddress,
            type = "text",
            text = new { body = message }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await client.PostAsync(
            $"https://graph.facebook.com/v19.0/{phoneNumberId}/messages",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("WhatsApp send failed: {StatusCode} {Body}", response.StatusCode, body);
        }
    }

    private async Task SendLineAsync(string toAddress, string message, CancellationToken cancellationToken)
    {
        if (!IsLineConfigured())
        {
            _logger.LogWarning(
                "MessagingConnector running in MOCK MODE for LINE (no real credentials configured). " +
                "Would have sent to {ToAddress}: \"{Message}\"", toAddress, message);
            return;
        }

        var client = _httpClientFactory.CreateClient("LineApi");
        var token = _configuration["Connectors:Line:ChannelAccessToken"];

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            to = toAddress,
            messages = new[] { new { type = "text", text = message } }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await client.PostAsync(
            "https://api.line.me/v2/bot/message/push",
            new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("LINE send failed: {StatusCode} {Body}", response.StatusCode, body);
        }
    }
}