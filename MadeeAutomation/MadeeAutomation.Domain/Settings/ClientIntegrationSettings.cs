using System.Text.Json;
using System.Text.Json.Serialization;

namespace MadeeAutomation.Domain.Settings;

/// <summary>
/// Typed view of a client's own credentials. Serialised (camelCase, same key names the
/// earlier version used) into ClientConfig.IntegrationSettingsJson, then encrypted.
/// Every connector reads credentials through this class - no more hand-parsed JSON.
/// </summary>
public class ClientIntegrationSettings
{
    // LINE Official Account (Messaging API channel)
    [JsonPropertyName("lineChannelSecret")] public string? LineChannelSecret { get; set; }
    [JsonPropertyName("lineChannelAccessToken")] public string? LineChannelAccessToken { get; set; }

    // WhatsApp Business (Meta Cloud API)
    [JsonPropertyName("whatsAppPhoneNumberId")] public string? WhatsAppPhoneNumberId { get; set; }
    [JsonPropertyName("whatsAppAccessToken")] public string? WhatsAppAccessToken { get; set; }

    // Facebook Messenger
    [JsonPropertyName("messengerPageAccessToken")] public string? MessengerPageAccessToken { get; set; }

    // Meta app (shared by WhatsApp and Messenger): the App Secret signs every webhook call
    // (X-Hub-Signature-256); the verify token is what we type into Meta's "Verify and save".
    // Both are optional per client - WebApi's Connectors:Meta:AppSecret / VerifyToken are used otherwise.
    [JsonPropertyName("metaAppSecret")] public string? MetaAppSecret { get; set; }
    [JsonPropertyName("metaVerifyToken")] public string? MetaVerifyToken { get; set; }

    // POSFirst
    [JsonPropertyName("posFirstBaseUrl")] public string? PosFirstBaseUrl { get; set; }
    [JsonPropertyName("posFirstApiKey")] public string? PosFirstApiKey { get; set; }

    // Merkai CRM
    [JsonPropertyName("merkaiBaseUrl")] public string? MerkaiBaseUrl { get; set; }
    [JsonPropertyName("merkaiApiKey")] public string? MerkaiApiKey { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static ClientIntegrationSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ClientIntegrationSettings();
        try
        {
            return JsonSerializer.Deserialize<ClientIntegrationSettings>(json, JsonOptions) ?? new ClientIntegrationSettings();
        }
        catch (JsonException)
        {
            return new ClientIntegrationSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool HasLineSecret => !string.IsNullOrWhiteSpace(LineChannelSecret);
    public bool HasLineToken => !string.IsNullOrWhiteSpace(LineChannelAccessToken);
    public bool HasWhatsApp => !string.IsNullOrWhiteSpace(WhatsAppPhoneNumberId) && !string.IsNullOrWhiteSpace(WhatsAppAccessToken);
    public bool HasMessenger => !string.IsNullOrWhiteSpace(MessengerPageAccessToken);
    public bool HasMetaAppSecret => !string.IsNullOrWhiteSpace(MetaAppSecret);
    public bool HasPosFirst => !string.IsNullOrWhiteSpace(PosFirstBaseUrl);
    public bool HasMerkai => !string.IsNullOrWhiteSpace(MerkaiBaseUrl);

    /// <summary>"••••1a2b" style hint for the Admin screen - never shows the whole secret.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        var s = secret.Trim();
        return s.Length <= 8 ? "••••" : "••••" + s[^4..];
    }
}
