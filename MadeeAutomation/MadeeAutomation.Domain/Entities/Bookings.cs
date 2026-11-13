namespace MadeeAutomation.Domain.Entities;

/// <summary>Where a booking is in its life.</summary>
public enum BookingStatus
{
    Pending = 1,     // the customer asked - waiting for staff to confirm
    Confirmed = 2,   // staff said yes
    Completed = 3,   // the customer came and was served
    Cancelled = 4,   // cancelled by the customer or by staff
    Declined = 5,    // staff could not take it
    NoShow = 6       // the customer did not come
}

/// <summary>
/// One service a beauty / wellness business sells by time: "Swedish massage 60 min", "Gel manicure",
/// "Haircut (women)". Like a menu item, but with a duration.
/// </summary>
public class ServiceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }          // Massage, Facial, Nails, Hair...
    public string? Description { get; set; }       // one short line shown to customers

    /// <summary>How long the customer is in the chair / on the bed.</summary>
    public int DurationMinutes { get; set; } = 60;

    /// <summary>Clean-up / reset time after the service, when the staff member can't start the next one.</summary>
    public int BufferMinutes { get; set; }

    public decimal? Price { get; set; }

    /// <summary>Can be done at the customer's home (home service).</summary>
    public bool HomeServiceAllowed { get; set; }

    /// <summary>Unticked = not offered right now (hidden from customers, kept for old bookings).</summary>
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A therapist, nail technician or stylist. Each one serves one customer at a time.</summary>
public class StaffMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    /// <summary>The name customers see ("Joy", "Ate Marie").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Therapist, Nail technician, Stylist...</summary>
    public string? Title { get; set; }

    /// <summary>"F", "M" or null (not given). Customers may ask for a female or male therapist.</summary>
    public string? Gender { get; set; }

    /// <summary>Weekly days off as bits: Sunday = 1, Monday = 2, Tuesday = 4 ... Saturday = 64.</summary>
    public int DaysOffMask { get; set; }

    public bool DoesHomeService { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The services this person can do.</summary>
    public List<StaffService> Services { get; set; } = new();

    public bool IsOffOn(DayOfWeek day) => (DaysOffMask & (1 << (int)day)) != 0;

    public static int Mask(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, d) => mask | (1 << (int)d));
}

/// <summary>Who can do what: one row per staff member + service.</summary>
public class StaffService
{
    public Guid StaffMemberId { get; set; }
    public Guid ServiceItemId { get; set; }
}

/// <summary>
/// One appointment. Made by the customer in the chat (Source "chat") or added by staff for a walk-in or
/// phone call (Source "staff"). Times are stored in UTC; LocalDate is the day in the client's time zone.
///
/// The staff member is BUSY from BusyFromUtc to BusyUntilUtc: for home service that includes travel
/// before and after; it always includes the service's clean-up time. Two bookings for the same staff
/// member never overlap in that window (checked when the booking is saved).
/// </summary>
public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientConfigId { get; set; }

    /// <summary>1, 2, 3 ... per client. Set when the booking is saved.</summary>
    public int BookingNumber { get; set; }

    /// <summary>"BK-0001" - what the customer and staff call it.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Source { get; set; } = SourceChat;
    public const string SourceChat = "chat";
    public const string SourceStaff = "staff";

    /// <summary>The chat channel (line / whatsapp / messenger), or "" for a booking staff added.</summary>
    public string Channel { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }

    public Guid? ServiceItemId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }

    public string? Currency { get; set; }
    public decimal? Price { get; set; }
    public decimal? HomeServiceFee { get; set; }
    /// <summary>Price + home-service fee. Null when the service has no price.</summary>
    public decimal? Total { get; set; }

    public Guid? StaffMemberId { get; set; }
    public string? StaffName { get; set; }

    /// <summary>What the customer asked for: "any", "F", "M" or "staff" (a named person).</summary>
    public string StaffPreference { get; set; } = "any";

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public DateTime BusyFromUtc { get; set; }
    public DateTime BusyUntilUtc { get; set; }
    public DateOnly LocalDate { get; set; }

    public bool AtHome { get; set; }
    public string? HomeAddress { get; set; }
    public string? CustomerNote { get; set; }

    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }

    /// <summary>Why it was declined or cancelled - told to the customer.</summary>
    public string? StatusReason { get; set; }

    /// <summary>The language the customer wrote in ("en", "tl"...). Used for messages later.</summary>
    public string? Language { get; set; }

    /// <summary>Was the customer told about the last Confirm / Decline / Cancel? null = not tried.</summary>
    public bool? CustomerNoticeSent { get; set; }
    public string? CustomerNoticeNote { get; set; }

    /// <summary>When the reminder should go out (null = no reminder: off, or booked too close to the time).</summary>
    public DateTime? ReminderDueUtc { get; set; }
    public DateTime? ReminderSentAtUtc { get; set; }

    /// <summary>"sent" or why it could not be sent (e.g. outside Messenger's 24-hour window).</summary>
    public string? ReminderNote { get; set; }

    /// <summary>The customer tapped "I'll be there" on the reminder.</summary>
    public DateTime? CustomerConfirmedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static string OwnReference(int number) => $"BK-{number:0000}";

    /// <summary>Statuses that hold the staff member's time.</summary>
    public static readonly BookingStatus[] Holding = { BookingStatus.Pending, BookingStatus.Confirmed };

    public bool IsHolding => Status is BookingStatus.Pending or BookingStatus.Confirmed;
}
