using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Settings;

namespace MadeeAutomation.Admin.Web.Models;

public class ClientDetailsModel
{
    public ClientConfig Client { get; set; } = null!;

    /// <summary>Null when the stored settings could not be decrypted (wrong/missing key).</summary>
    public ClientIntegrationSettings? Settings { get; set; }
    public string? SettingsError { get; set; }

    public string WebApiBaseUrl { get; set; } = string.Empty;

    public string LineWebhookUrl => $"{WebApiBaseUrl}/api/webhooks/line/{Client.ClientCode}";
    public string WhatsAppWebhookUrl => $"{WebApiBaseUrl}/api/webhooks/whatsapp/{Client.ClientCode}";
    public string MessengerWebhookUrl => $"{WebApiBaseUrl}/api/webhooks/messenger/{Client.ClientCode}";
    public string GenericWebhookUrl(Guid workflowId) => $"{WebApiBaseUrl}/api/webhooks/generic/{Client.ClientCode}/{workflowId}";
}
