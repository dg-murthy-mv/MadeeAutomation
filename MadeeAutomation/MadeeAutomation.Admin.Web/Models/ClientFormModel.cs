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
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "THB";

    public VerticalPack Vertical { get; set; } = VerticalPack.FnBRetail;
    public ClientStatus Status { get; set; } = ClientStatus.Trial;

    [Display(Name = "Merkai CRM integration enabled")] public bool MerkaiIntegrationEnabled { get; set; }
    [Display(Name = "POSFirst integration enabled")] public bool PosFirstIntegrationEnabled { get; set; }

    [Display(Name = "Knowledge base (FAQ)")] public string? KnowledgeBaseText { get; set; }
    [Display(Name = "Menu / products")] public string? MenuText { get; set; }

    // ---- Credentials (Edit only). Blank secret = keep the saved one. ----
    [Display(Name = "LINE channel secret")] public string? LineChannelSecret { get; set; }
    [Display(Name = "LINE channel access token")] public string? LineChannelAccessToken { get; set; }
    [Display(Name = "WhatsApp phone number ID")] public string? WhatsAppPhoneNumberId { get; set; }
    [Display(Name = "WhatsApp access token")] public string? WhatsAppAccessToken { get; set; }
    [Display(Name = "Messenger page access token")] public string? MessengerPageAccessToken { get; set; }
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
        nameof(MessengerPageAccessToken), nameof(PosFirstApiKey), nameof(MerkaiApiKey)
    };

    public static readonly (string Value, string Label)[] Countries =
    {
        ("TH", "Thailand"), ("IN", "India"), ("PH", "Philippines"), ("AE", "UAE")
    };

    public static readonly string[] TimeZones =
    {
        "Asia/Bangkok", "Asia/Kolkata", "Asia/Manila", "Asia/Dubai", "UTC"
    };
}
