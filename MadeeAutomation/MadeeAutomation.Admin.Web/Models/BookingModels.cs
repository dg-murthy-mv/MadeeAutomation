using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Application.VerticalPacks.Beauty;
using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Admin.Web.Models;

public class ServiceFormModel
{
    public Guid Id { get; set; }
    public Guid ClientConfigId { get; set; }

    [Required(ErrorMessage = "Type the service name.")]
    [StringLength(200)]
    [Display(Name = "Service")]
    public string? Name { get; set; }

    [StringLength(100)]
    [Display(Name = "Category")]
    public string? Category { get; set; }

    [StringLength(500)]
    [Display(Name = "Short description (shown to customers)")]
    public string? Description { get; set; }

    [Range(5, 600, ErrorMessage = "Between 5 and 600 minutes.")]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 60;

    [Range(0, 240, ErrorMessage = "Between 0 and 240 minutes.")]
    [Display(Name = "Clean-up time after (minutes)")]
    public int BufferMinutes { get; set; }

    [Range(0, 10_000_000, ErrorMessage = "The price can't be negative.")]
    [Display(Name = "Price")]
    public decimal? Price { get; set; }

    [Display(Name = "Can be done as home service")]
    public bool HomeServiceAllowed { get; set; }

    [Display(Name = "Offered (untick to hide it from customers)")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Order in the list")]
    public int SortOrder { get; set; }

    /// <summary>Who can do it.</summary>
    public List<Guid> StaffIds { get; set; } = new();
}

public class ServicesPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public List<ServiceItem> Services { get; set; } = new();
    public List<StaffMember> Staff { get; set; } = new();
    public ServiceFormModel NewService { get; set; } = new();
    public bool HoursConfigured { get; set; }

    public List<StaffMember> StaffFor(Guid serviceId) => Staff.Where(m => m.Services.Any(x => x.ServiceItemId == serviceId)).ToList();
}

public class StaffFormModel
{
    public Guid Id { get; set; }
    public Guid ClientConfigId { get; set; }

    [Required(ErrorMessage = "Type the name customers will see.")]
    [StringLength(100)]
    [Display(Name = "Name (as customers see it)")]
    public string? Name { get; set; }

    [StringLength(100)]
    [Display(Name = "Title")]
    public string? Title { get; set; }

    /// <summary>"", "F" or "M".</summary>
    [Display(Name = "Gender")]
    public string? Gender { get; set; }

    /// <summary>The ticked days they work, 0 = Sunday ... 6 = Saturday. Unticked days are their weekly days off.
    /// Starts empty on purpose: if nothing is ticked, the form says so instead of quietly meaning "every day".</summary>
    public List<int> WorkingDays { get; set; } = new();

    [Display(Name = "Does home service")]
    public bool DoesHomeService { get; set; }

    [Display(Name = "Working (untick when on leave or no longer with you)")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Order in the list")]
    public int SortOrder { get; set; }

    public List<Guid> ServiceIds { get; set; } = new();

    // ---- Doctor profile (clinics; optional for spas) ----

    [Range(0, 10_000_000, ErrorMessage = "The fee can't be negative.")]
    [Display(Name = "Consultation fee (instead of the service price)")]
    public decimal? ConsultationFee { get; set; }

    [StringLength(200)]
    [Display(Name = "Languages spoken")]
    public string? Languages { get; set; }

    [StringLength(1000)]
    [Display(Name = "Short profile (shown to patients)")]
    public string? Bio { get; set; }

    /// <summary>
    /// Own weekly sessions: 14 boxes each, index = day * 2 + n (day 0 = Sunday, n = first / second session),
    /// as "HH:mm". Both empty = no session.
    /// </summary>
    public List<string?> SessionStarts { get; set; } = new();
    public List<string?> SessionEnds { get; set; } = new();

    public string? Start(int day, int n) => SessionStarts.ElementAtOrDefault(day * 2 + n);
    public string? End(int day, int n) => SessionEnds.ElementAtOrDefault(day * 2 + n);
}

public class StaffPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public List<StaffMember> Staff { get; set; } = new();
    public List<ServiceItem> Services { get; set; } = new();
}

public class StaffEditModel
{
    public ClientConfig Client { get; set; } = null!;
    public StaffFormModel Form { get; set; } = new();
    public List<ServiceItem> Services { get; set; } = new();
}

public class ServiceEditModel
{
    public ClientConfig Client { get; set; } = null!;
    public ServiceFormModel Form { get; set; } = new();
    public List<StaffMember> Staff { get; set; } = new();
}

public class BookingsPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public PagedResult<Booking> Bookings { get; set; } = new();
    public PagerModel Pager { get; set; } = new();
    public string Show { get; set; } = "todo";
    public DateOnly? Day { get; set; }
    public DateOnly Today { get; set; }
    public string? Search { get; set; }
    public Guid? StaffId { get; set; }
    public List<StaffMember> Staff { get; set; } = new();
    public int PendingCount { get; set; }
    public bool StaffChannelSet { get; set; }

    /// <summary>Messages planned for later per booking (2-hour reminder, follow-up, feedback).</summary>
    public Dictionary<Guid, List<BookingMessage>> Planned { get; set; } = new();
    public List<FollowUpRow> FollowUps { get; set; } = new();
    public List<WaitlistEntry> Waitlist { get; set; } = new();
    public int WaitingCount { get; set; }
    public BookingTerms Terms => BookingTerms.For(Client);
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || StaffId is not null || (Day is not null && Show != "day");
}

public class BookingAddModel
{
    public Guid ClientConfigId { get; set; }

    [Required(ErrorMessage = "Choose the service.")]
    [Display(Name = "Service")]
    public Guid? ServiceId { get; set; }

    [Required(ErrorMessage = "Choose the day.")]
    [Display(Name = "Day")]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "Type the start time.")]
    [RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "Use hours:minutes, e.g. 14:30.")]
    [Display(Name = "Start time")]
    public string? Time { get; set; }

    /// <summary>Null = anyone free.</summary>
    [Display(Name = "Staff")]
    public Guid? StaffId { get; set; }

    [Display(Name = "Home service")]
    public bool AtHome { get; set; }

    [StringLength(500)]
    [Display(Name = "Address (home service)")]
    public string? HomeAddress { get; set; }

    [Required(ErrorMessage = "Type the customer's name.")]
    [StringLength(200)]
    [Display(Name = "Customer name")]
    public string? CustomerName { get; set; }

    [StringLength(30)]
    [Display(Name = "Mobile")]
    public string? CustomerPhone { get; set; }

    [StringLength(1000)]
    [Display(Name = "Note")]
    public string? CustomerNote { get; set; }

    // Clinics
    [Range(0, 120, ErrorMessage = "Between 0 and 120.")]
    [Display(Name = "Age")]
    public int? PatientAge { get; set; }

    [Display(Name = "Visit")]
    public string? VisitType { get; set; }
}

public class BookingAddPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public BookingAddModel Form { get; set; } = new();
    public List<ServiceItem> Services { get; set; } = new();
    public List<StaffMember> Staff { get; set; } = new();
}

public class BookingSettingsModel
{
    public Guid ClientConfigId { get; set; }

    [Display(Name = "Offer start times every")]
    public int SlotMinutes { get; set; } = 30;

    [Range(0, 10080, ErrorMessage = "Between 0 and 10080 minutes (7 days).")]
    [Display(Name = "Earliest booking from now (minutes)")]
    public int MinNoticeMinutes { get; set; } = 60;

    [Range(1, 90, ErrorMessage = "Between 1 and 90 days.")]
    [Display(Name = "Customers can book up to (days ahead)")]
    public int DaysAhead { get; set; } = 14;

    [Display(Name = "We offer home service")]
    public bool HomeServiceEnabled { get; set; }

    [Range(0, 1_000_000, ErrorMessage = "The fee can't be negative.")]
    [Display(Name = "Home-service fee (added to the price)")]
    public decimal? HomeServiceFee { get; set; }

    [Range(0, 240, ErrorMessage = "Between 0 and 240 minutes.")]
    [Display(Name = "Travel time before and after (minutes)")]
    public int HomeServiceTravelMinutes { get; set; } = 30;

    [Display(Name = "Send customers a reminder before their booking")]
    public bool RemindersEnabled { get; set; } = true;

    [Display(Name = "Tell customers when a booking is confirmed, declined or cancelled")]
    public bool NotifyCustomer { get; set; } = true;

    [Display(Name = "Also remind them 2 hours before")]
    public bool SecondReminderEnabled { get; set; }

    [Display(Name = "Ask for a 1-5 star rating after the visit")]
    public bool FeedbackEnabled { get; set; }

    [Range(0, 72, ErrorMessage = "Between 0 and 72 hours.")]
    [Display(Name = "Hours after \"Completed\"")]
    public int FeedbackDelayHours { get; set; } = 2;

    [Display(Name = "Waitlist: customers can wait for a time when none is free")]
    public bool WaitlistEnabled { get; set; } = true;

    [StringLength(20)]
    [RegularExpression(@"^[a-zA-Z]{2,3}(_[a-zA-Z]{2})?$", ErrorMessage = "A language code like en, en_US, hi or fil.")]
    [Display(Name = "Template language")]
    public string? TemplateLanguage { get; set; }

    [StringLength(100)]
    [RegularExpression(@"^[a-z0-9_]+$", ErrorMessage = "Template names use small letters, numbers and _ only.")]
    [Display(Name = "Reminder template")]
    public string? TemplateReminder { get; set; }

    [StringLength(100)]
    [RegularExpression(@"^[a-z0-9_]+$", ErrorMessage = "Template names use small letters, numbers and _ only.")]
    [Display(Name = "Follow-up template")]
    public string? TemplateFollowUp { get; set; }

    [StringLength(100)]
    [RegularExpression(@"^[a-z0-9_]+$", ErrorMessage = "Template names use small letters, numbers and _ only.")]
    [Display(Name = "Feedback template")]
    public string? TemplateFeedback { get; set; }

    [StringLength(100)]
    [RegularExpression(@"^[a-z0-9_]+$", ErrorMessage = "Template names use small letters, numbers and _ only.")]
    [Display(Name = "Waitlist template")]
    public string? TemplateWaitlist { get; set; }
}

public class BookingSettingsPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public BookingSettingsModel Form { get; set; } = new();
}
