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
    public string ClientCode { get; set; } = string.Empty; // short slug, e.g. "madeebakers" - part of webhook URLs

    public string Country { get; set; } = string.Empty;    // TH, IN, PH, AE
    public string TimeZoneId { get; set; } = "Asia/Bangkok";
    public string Currency { get; set; } = "THB";

    public VerticalPack Vertical { get; set; }
    public ClientStatus Status { get; set; } = ClientStatus.Trial;

    /// <summary>
    /// This client's own channel/API credentials (LINE, WhatsApp, Messenger, POSFirst, Merkai).
    /// Stored ENCRYPTED ("enc:v1:..." - see SettingsCipher). Never read this column directly:
    /// go through IClientSettingsService, which decrypts it into ClientIntegrationSettings.
    /// Older rows may still hold plain JSON; they are read as-is and encrypted on the next save.
    /// </summary>
    public string IntegrationSettingsJson { get; set; } = "{}";

    /// <summary>FAQ / business information the AI may answer from (opening hours, delivery area, prices...).</summary>
    public string? KnowledgeBaseText { get; set; }

    /// <summary>Menu / product list the order parser matches customer messages against.</summary>
    public string? MenuText { get; set; }

    /// <summary>
    /// Sent to the customer when the AI step fails (Claude down, bad answer, POSFirst error), so nobody
    /// is left without a reply. Null = the English default. A client may write its own in any language.
    /// </summary>
    public string? FallbackReplyText { get; set; }

    /// <summary>Where staff alerts for this client go (hand-overs, failures). Both optional.</summary>
    public string? StaffAlertEmail { get; set; }

    /// <summary>A staff member's LINE user id - alerts are pushed to them through this client's LINE account.</summary>
    public string? StaffAlertLineUserId { get; set; }

    public bool MerkaiIntegrationEnabled { get; set; }
    public bool PosFirstIntegrationEnabled { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<WorkflowDefinition> Workflows { get; set; } = new List<WorkflowDefinition>();

    /// <summary>Paused and cancelled clients never run workflows.</summary>
    public bool CanRunWorkflows => Status is ClientStatus.Trial or ClientStatus.Active;

    public const string DefaultFallbackEnglish = "Thanks for your message! A member of our team will reply shortly.";

    /// <summary>The fallback text actually used: the client's own, or the English default (our side is English-only).</summary>
    public string EffectiveFallbackReply =>
        !string.IsNullOrWhiteSpace(FallbackReplyText) ? FallbackReplyText.Trim() : DefaultFallbackEnglish;
}
