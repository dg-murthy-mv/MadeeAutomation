using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>The bookings list filter tabs.</summary>
public enum BookingView
{
    ToConfirm,   // Pending
    Upcoming,    // Pending + Confirmed, from now on
    Day,         // everything on one local day
    Past,        // Completed, NoShow, Cancelled, Declined
    All
}

/// <summary>
/// Services, staff and bookings of beauty / wellness clients. Every query is limited to one client.
/// </summary>
public interface IBookingRepository
{
    // ---------------- services ----------------
    Task<List<ServiceItem>> GetServicesAsync(Guid clientConfigId, bool activeOnly = false, CancellationToken cancellationToken = default);
    Task<ServiceItem?> GetServiceAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ServiceNameExistsAsync(Guid clientConfigId, string name, Guid? exceptId = null, CancellationToken cancellationToken = default);
    Task AddServiceAsync(ServiceItem service, IEnumerable<Guid> staffIds, CancellationToken cancellationToken = default);
    Task UpdateServiceAsync(ServiceItem service, IEnumerable<Guid> staffIds, CancellationToken cancellationToken = default);
    Task DeleteServiceAsync(Guid id, CancellationToken cancellationToken = default);

    // ---------------- staff ----------------
    /// <summary>Staff with the services they do.</summary>
    Task<List<StaffMember>> GetStaffAsync(Guid clientConfigId, bool activeOnly = false, CancellationToken cancellationToken = default);
    Task<StaffMember?> GetStaffMemberAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddStaffAsync(StaffMember staff, IEnumerable<Guid> serviceIds, CancellationToken cancellationToken = default);
    Task UpdateStaffAsync(StaffMember staff, IEnumerable<Guid> serviceIds, CancellationToken cancellationToken = default);
    Task DeleteStaffAsync(Guid id, CancellationToken cancellationToken = default);

    // ---------------- bookings ----------------
    /// <summary>Time already taken by Pending / Confirmed bookings that touch [fromUtc, untilUtc).</summary>
    Task<List<BusyBlock>> GetBusyAsync(Guid clientConfigId, DateTime fromUtc, DateTime untilUtc, Guid? exceptBookingId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new booking with the next BK- number - ONLY if its staff member is still free for its busy
    /// window (someone else may have taken the slot a moment ago). False = taken; nothing saved.
    /// </summary>
    Task<bool> TryAddAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>Moves a booking to a new time / staff member, with the same "still free?" check. Back to Pending.</summary>
    Task<bool> TryRescheduleAsync(Guid clientConfigId, Guid bookingId, DateTime startUtc, DateTime endUtc, DateTime busyFromUtc,
        DateTime busyUntilUtc, DateOnly localDate, Guid staffId, string staffName, string changedBy, CancellationToken cancellationToken = default);

    Task<Booking?> GetAsync(Guid clientConfigId, Guid bookingId, CancellationToken cancellationToken = default);

    Task<PagedResult<Booking>> GetPagedAsync(Guid clientConfigId, BookingView view, DateTime utcNow, int page, int pageSize,
        string? search = null, DateOnly? day = null, Guid? staffId = null, CancellationToken cancellationToken = default);

    Task<int> CountAsync(Guid clientConfigId, BookingStatus status, CancellationToken cancellationToken = default);

    /// <summary>The customer's own Pending / Confirmed bookings that have not started yet, soonest first.</summary>
    Task<List<Booking>> GetUpcomingForCustomerAsync(Guid clientConfigId, string channel, string customerId, DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task<bool> SetStatusAsync(Guid clientConfigId, Guid bookingId, BookingStatus status, string changedBy, string? reason = null,
        DateTime? reminderDueUtc = null, CancellationToken cancellationToken = default);

    Task SetCustomerNoticeAsync(Guid clientConfigId, Guid bookingId, bool sent, string note, CancellationToken cancellationToken = default);

    Task SetCustomerConfirmedAsync(Guid clientConfigId, Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default);

    // ---------------- reminders ----------------
    Task<List<Guid>> GetDueRemindersAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default);

    /// <summary>Marks the reminder as being sent (so two workers never send it twice). False = someone else has it.</summary>
    Task<bool> TryClaimReminderAsync(Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default);

    Task SetReminderNoteAsync(Guid bookingId, string note, CancellationToken cancellationToken = default);

    /// <summary>Which client a booking belongs to (the reminder worker only has the id).</summary>
    Task<Guid?> GetClientIdAsync(Guid bookingId, CancellationToken cancellationToken = default);

    // ---------------- messages sent later (2-hour reminder, follow-up, feedback) ----------------
    /// <summary>Plans a message; an unsent one of the same kind for this booking is replaced.</summary>
    Task ScheduleMessageAsync(Guid clientConfigId, Guid bookingId, string kind, DateTime dueUtc, CancellationToken cancellationToken = default);

    /// <summary>Drops unsent messages for the booking (all kinds, or one).</summary>
    Task CancelMessagesAsync(Guid bookingId, string? kind = null, CancellationToken cancellationToken = default);

    Task<List<BookingMessage>> GetDueMessagesAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default);
    Task<bool> TryClaimMessageAsync(Guid messageId, DateTime utcNow, CancellationToken cancellationToken = default);
    Task SetMessageNoteAsync(Guid messageId, string note, CancellationToken cancellationToken = default);
    Task<List<BookingMessage>> GetMessagesForAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default);

    // ---------------- clinics ----------------
    /// <summary>The follow-up the doctor wants (date null = none) and their instructions, sent to the patient as written.</summary>
    Task SetFollowUpAsync(Guid clientConfigId, Guid bookingId, DateOnly? date, string? note, CancellationToken cancellationToken = default);

    /// <summary>rating set = the stars (1-5); rating null = the comment.</summary>
    Task<bool> SetFeedbackAsync(Guid clientConfigId, Guid bookingId, int? rating, string? comment, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>Visits with a follow-up on or after fromDate, and the patient's next booking if they made one.</summary>
    Task<List<FollowUpRow>> GetFollowUpsAsync(Guid clientConfigId, DateOnly fromDate, int take, CancellationToken cancellationToken = default);

    // ---------------- waitlist ----------------
    Task AddWaitlistAsync(WaitlistEntry entry, CancellationToken cancellationToken = default);
    Task<WaitlistEntry?> GetWaitlistEntryAsync(Guid clientConfigId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>A client's waitlist; openOnly = still waiting or offered.</summary>
    Task<List<WaitlistEntry>> GetWaitlistAsync(Guid clientConfigId, bool openOnly, CancellationToken cancellationToken = default);

    /// <summary>All clients: entries still waiting for a time (the worker checks these).</summary>
    Task<List<WaitlistEntry>> GetWaitingAsync(int take, CancellationToken cancellationToken = default);

    Task<bool> IsWaitingAsync(Guid clientConfigId, string channel, string customerId, Guid serviceId, DateOnly? date, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases one customer's personal details for this client (a privacy request): on all their bookings
    /// (matched by chat id or by mobile), their waitlist entries, their chat state and the text of their
    /// messages. Bookings stay (as "(erased)") so the reports still add up.
    /// </summary>
    Task<EraseResult> EraseCustomerAsync(Guid clientConfigId, string? channel, string? customerId, string? phone, CancellationToken cancellationToken = default);

    /// <summary>onlyIf: change it only when it still has that status (so an offer is sent once).</summary>
    Task<bool> SetWaitlistStatusAsync(Guid id, WaitlistStatus status, string? note, DateTime? offeredAtUtc = null, WaitlistStatus? onlyIf = null,
        CancellationToken cancellationToken = default);
}

/// <summary>What "Erase personal details" changed.</summary>
public sealed record EraseResult(int Bookings, int WaitlistEntries, int Messages);

/// <summary>A visit the doctor wants a follow-up for, and the patient's next booking (null = not booked yet).</summary>
public sealed record FollowUpRow(Booking Visit, Booking? NextBooking);
