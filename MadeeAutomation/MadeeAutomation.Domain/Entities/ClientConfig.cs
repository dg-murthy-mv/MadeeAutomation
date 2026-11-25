using MadeeAutomation.Domain.Billing;
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

    // ---- Appointments: extra messages (clinics turn these on; spas may too) ----

    /// <summary>A second reminder 2 hours before the appointment (the first is the day before).</summary>
    public bool BookingSecondReminderEnabled { get; set; }

    /// <summary>Ask the customer for 1-5 stars after a completed visit.</summary>
    public bool BookingFeedbackEnabled { get; set; }

    /// <summary>How long after the visit is marked Completed the feedback question goes out.</summary>
    public int BookingFeedbackDelayHours { get; set; } = 2;

    /// <summary>Customers can join a waitlist when no time is free, and are offered a time when one opens up.</summary>
    public bool BookingWaitlistEnabled { get; set; } = true;

    // ---- WhatsApp message templates (approved in Meta) for messages the business starts ----
    // WhatsApp only allows free-form messages within 24 hours of the customer's last message.
    // Reminders, follow-ups, feedback and waitlist offers are sent with these templates when set.

    /// <summary>Template language code exactly as approved in Meta ("en", "en_US", "hi", "fil"...).</summary>
    public string? WhatsAppTemplateLanguage { get; set; }
    public string? WhatsAppTemplateReminder { get; set; }
    public string? WhatsAppTemplateFollowUp { get; set; }
    public string? WhatsAppTemplateFeedback { get; set; }
    public string? WhatsAppTemplateWaitlist { get; set; }

    /// <summary>Email last month's report to StaffAlertEmail on the 1st of each month (8 AM client time).</summary>
    public bool MonthlyReportEnabled { get; set; } = true;

    /// <summary>The month ("2026-10") the monthly report email was last sent for, so it goes out once.</summary>
    public string? MonthlyReportSentFor { get; set; }

    // ---- Plan / subscription (see Domain/Billing/Plans.cs) ----

    /// <summary>New clients start on a 15-day trial. Clients from before plans existed are Demo (no limits).</summary>
    public PlanTier Plan { get; set; } = PlanTier.Trial;

    public BillingCycle? BillingCycle { get; set; }

    /// <summary>When the trial or the paid period ends. A 3-day grace follows, then the assistant pauses.</summary>
    public DateTime? PlanEndsAtUtc { get; set; }

    /// <summary>The price agreed with this client per billing cycle (null = list price for their currency).</summary>
    public decimal? PlanPrice { get; set; }

    /// <summary>A note for MadeeVision only (discount reason, who agreed it...).</summary>
    public string? PlanNote { get; set; }

    /// <summary>The 2-hour reminder runs only when switched on AND the plan includes it.</summary>
    public bool UsesSecondReminder => BookingSecondReminderEnabled && PlanCatalog.For(Plan).SecondReminder;

    public bool UsesFeedback => BookingFeedbackEnabled && PlanCatalog.For(Plan).Feedback;

    public bool UsesWaitlist => BookingWaitlistEnabled && PlanCatalog.For(Plan).Waitlist;

    // ---- Review booster: after the rating, every customer gets a link to review the business ----

    /// <summary>The business's "write a review" link on Google (Google Business Profile -> Ask for reviews).</summary>
    public string? GoogleReviewUrl { get; set; }

    /// <summary>The business's Facebook Page reviews link (optional).</summary>
    public string? FacebookReviewUrl { get; set; }

    /// <summary>Send the review links after the rating question (to every customer who answers it).</summary>
    public bool ReviewRequestsEnabled { get; set; } = true;

    /// <summary>Review links go out when switched on, ratings run (plan + setting) and at least one link is set.</summary>
    public bool UsesReviewRequests =>
        ReviewRequestsEnabled && UsesFeedback && (!string.IsNullOrWhiteSpace(GoogleReviewUrl) || !string.IsNullOrWhiteSpace(FacebookReviewUrl));

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
