using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// Represents one client/tenant subscribed to MadeeAutomation services.
/// Mirrors the ICurrentTenantService pattern used in Merkai CRM.
/// </summary>
public class ClientConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ClientName { get; set; } = string.Empty;
    public string ClientCode { get; set; } = string.Empty; // short slug, e.g. "madeebakers"

    public string Country { get; set; } = string.Empty;    // TH, IN, PH, AE
    public string TimeZoneId { get; set; } = "Asia/Bangkok";
    public string Currency { get; set; } = "THB";

    public VerticalPack Vertical { get; set; }
    public ClientStatus Status { get; set; } = ClientStatus.Trial;

    // Integration secrets/config, stored as JSON (encrypt at rest in Infrastructure layer).
    // Recognized keys, all optional - a connector falls back to mock mode if its keys are missing:
    //   whatsAppPhoneNumberId, whatsAppAccessToken   - this client's own WhatsApp Business API credentials
    //   lineChannelAccessToken                        - this client's own LINE Official Account token
    //   posFirstBaseUrl, posFirstApiKey                - this client's own POSFirst instance
    //   merkaiBaseUrl, merkaiApiKey                    - this client's own Merkai CRM instance
    // Example: { "whatsAppPhoneNumberId": "123...", "whatsAppAccessToken": "EAAG...", "posFirstBaseUrl": "https://pos.client1.com", "posFirstApiKey": "..." }
    public string IntegrationSettingsJson { get; set; } = "{}";

    public bool MerkaiIntegrationEnabled { get; set; }
    public bool PosFirstIntegrationEnabled { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<WorkflowDefinition> Workflows { get; set; } = new List<WorkflowDefinition>();
}
