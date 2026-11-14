using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Admin.Web.Models;

/// <summary>Create / Edit client form. Secrets are write-only: the page never shows a saved secret.</summary>
public class ClientFormModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Client name")]
    public string ClientName { get; set; } = string.Empty;

    [Required, StringLength(50, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9][a-z0-9-]*$", ErrorMessage = "Use lower-case letters, numbers and dashes only (e.g. madeebakers).")]
    [Display(Name = "Client code")]
    public string ClientCode { get; set; } = string.Empty;

    [Required] public string Country { get; set; } = "TH";
    [Required, Display(Name = "Time zone")] public string TimeZoneId { get; set; } = "Asia/Bangkok";
    [Required] public string Currency { get; set; } = "THB";

    public VerticalPack Vertical { get; set; } = VerticalPack.FnBRetail;
    public ClientStatus Status { get; set; } = ClientStatus.Trial;

    [Display(Name = "Merkai CRM integration enabled")] public bool MerkaiIntegrationEnabled { get; set; }
    [Display(Name = "POSFirst integration enabled")] public bool PosFirstIntegrationEnabled { get; set; }

    [Display(Name = "Knowledge base (FAQ)")] public string? KnowledgeBaseText { get; set; }
    [Display(Name = "Menu / products")] public string? MenuText { get; set; }

    // ---- When something needs a person ----
    [StringLength(1000), Display(Name = "Fallback reply")]
    public string? FallbackReplyText { get; set; }

    // ---- Tax shown on orders ----
    [StringLength(20), Display(Name = "Tax name")]
    public string? TaxName { get; set; }

    [Range(0.01, 50, ErrorMessage = "Enter a rate between 0.01 and 50 (or leave blank for no tax).")]
    [Display(Name = "Tax rate (%)")]
    public decimal? TaxRatePercent { get; set; }

    [Display(Name = "Menu prices already include this tax")]
    public bool PricesIncludeTax { get; set; } = true;

    [Display(Name = "Show the menu with buttons in the chat (LINE)")]
    public bool MenuButtonsEnabled { get; set; } = true;

    [Display(Name = "Message the customer when an order is accepted or cancelled")]
    public bool NotifyCustomerOnOrderStatus { get; set; } = true;

    [Display(Name = "Email last month's report to the staff alert email (1st of each month, 8 AM)")]
    public bool MonthlyReportEnabled { get; set; } = true;

    [EmailAddress, StringLength(256), Display(Name = "Staff alert email")]
    public string? StaffAlertEmail { get; set; }

    [StringLength(100), RegularExpression("^U[0-9a-f]{32}$", ErrorMessage = "A LINE user id looks like U followed by 32 letters/numbers (copy it from Inbound events).")]
    [Display(Name = "Staff LINE user id")]
    public string? StaffAlertLineUserId { get; set; }

    // ---- Credentials (Edit only). Blank secret = keep the saved one. ----
    [Display(Name = "LINE channel secret")] public string? LineChannelSecret { get; set; }
    [Display(Name = "LINE channel access token")] public string? LineChannelAccessToken { get; set; }
    [Display(Name = "WhatsApp phone number ID")] public string? WhatsAppPhoneNumberId { get; set; }
    [Display(Name = "WhatsApp access token")] public string? WhatsAppAccessToken { get; set; }
    [Display(Name = "Messenger page access token")] public string? MessengerPageAccessToken { get; set; }
    [Display(Name = "Meta App Secret")] public string? MetaAppSecret { get; set; }
    [Display(Name = "Meta webhook verify token")] public string? MetaVerifyToken { get; set; }
    [Display(Name = "POSFirst base URL")] public string? PosFirstBaseUrl { get; set; }
    [Display(Name = "POSFirst API key")] public string? PosFirstApiKey { get; set; }
    [Display(Name = "Merkai base URL")] public string? MerkaiBaseUrl { get; set; }
    [Display(Name = "Merkai API key")] public string? MerkaiApiKey { get; set; }

    /// <summary>Names of secret fields the user ticked "Remove" for.</summary>
    public List<string> ClearSecrets { get; set; } = new();

    /// <summary>Masked hints of saved secrets ("••••1a2b"), keyed by field name. Display only.</summary>
    public Dictionary<string, string> SavedSecretHints { get; set; } = new();

    public bool IsEdit => Id.HasValue;

    public static readonly string[] SecretFields =
    {
        nameof(LineChannelSecret), nameof(LineChannelAccessToken), nameof(WhatsAppAccessToken),
        nameof(MessengerPageAccessToken), nameof(MetaAppSecret), nameof(MetaVerifyToken), nameof(PosFirstApiKey), nameof(MerkaiApiKey)
    };

}
