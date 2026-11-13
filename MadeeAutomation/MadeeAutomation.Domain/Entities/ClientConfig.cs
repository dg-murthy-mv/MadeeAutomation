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

    /// <summary>
    /// When staff accept or cancel an order, message the customer on the channel they ordered on.
    /// On by default. On LINE these are push messages, which count against the account's monthly quota.
    /// </summary>
    public bool NotifyCustomerOnOrderStatus { get; set; } = true;

    // ---- Tax shown on orders (blank = the business is not tax-registered: no tax lines) ----

    /// <summary>What the tax is called on an order: "GST", "VAT"...</summary>
    public string? TaxName { get; set; }

    /// <summary>The rate in percent, e.g. 5, 7, 12.</summary>
    public decimal? TaxRatePercent { get; set; }

    /// <summary>True (the default): menu prices already include the tax. False: the tax is added on top.</summary>
    public bool PricesIncludeTax { get; set; } = true;

    /// <summary>
    /// Show the menu with buttons in the chat (categories -> items -> cart -> order). On by default;
    /// it only does anything when the client has a structured menu, and for now only on LINE.
    /// </summary>
    public bool MenuButtonsEnabled { get; set; } = true;

    // ---- Appointments (Beauty & wellness clients) ----

    /// <summary>Start times are offered every N minutes (15, 30, 60).</summary>
    public int BookingSlotMinutes { get; set; } = 30;

    /// <summary>The earliest a customer can book from now, in minutes (e.g. 60 = not within the next hour).</summary>
    public int BookingMinNoticeMinutes { get; set; } = 60;

    /// <summary>How many days ahead customers can book (today counts as day 1).</summary>
    public int BookingDaysAhead { get; set; } = 14;

    /// <summary>The business offers home service (staff travel to the customer).</summary>
    public bool HomeServiceEnabled { get; set; }

    /// <summary>Added to the price of a home-service booking.</summary>
    public decimal? HomeServiceFee { get; set; }

    /// <summary>Travel time blocked before AND after a home-service booking.</summary>
    public int HomeServiceTravelMinutes { get; set; } = 30;

    /// <summary>Send the customer a reminder before a confirmed booking.</summary>
    public bool BookingRemindersEnabled { get; set; } = true;

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
