using System.Data;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Regional;
using Microsoft.EntityFrameworkCore;

namespace MadeeAutomation.Infrastructure.Persistence.Repositories;

/// <summary>Services, staff and bookings. Every query is limited to one client.</summary>
public class BookingRepository : IBookingRepository
{
    private readonly AutomationDbContext _db;

    public BookingRepository(AutomationDbContext db) => _db = db;

    // =====================================================================
    // Services
    // =====================================================================

    public Task<List<ServiceItem>> GetServicesAsync(Guid clientConfigId, bool activeOnly = false, CancellationToken cancellationToken = default) =>
        _db.ServiceItems.AsNoTracking()
            .Where(s => s.ClientConfigId == clientConfigId && (!activeOnly || s.IsActive))
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

    public Task<ServiceItem?> GetServiceAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.ServiceItems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> ServiceNameExistsAsync(Guid clientConfigId, string name, Guid? exceptId = null, CancellationToken cancellationToken = default) =>
        _db.ServiceItems.AnyAsync(s => s.ClientConfigId == clientConfigId && s.Name == name && (exceptId == null || s.Id != exceptId), cancellationToken);

    public async Task AddServiceAsync(ServiceItem service, IEnumerable<Guid> staffIds, CancellationToken cancellationToken = default)
    {
        _db.ChangeTracker.Clear();
        _db.ServiceItems.Add(service);
        await _db.SaveChangesAsync(cancellationToken);
        await SetServiceStaffAsync(service, staffIds, cancellationToken);
    }

    public async Task UpdateServiceAsync(ServiceItem service, IEnumerable<Guid> staffIds, CancellationToken cancellationToken = default)
    {
        // Load the row fresh and copy the fields across: the object passed in was read without tracking,
        // so attaching it (and anything hanging off it) could clash with rows already in this context.
        _db.ChangeTracker.Clear();
        var row = await _db.ServiceItems.FirstOrDefaultAsync(s => s.Id == service.Id && s.ClientConfigId == service.ClientConfigId, cancellationToken);
        if (row is null) return;
        row.Name = service.Name;
        row.Category = service.Category;
        row.Description = service.Description;
        row.DurationMinutes = service.DurationMinutes;
        row.BufferMinutes = service.BufferMinutes;
        row.Price = service.Price;
        row.HomeServiceAllowed = service.HomeServiceAllowed;
        row.IsActive = service.IsActive;
        row.SortOrder = service.SortOrder;
        await _db.SaveChangesAsync(cancellationToken);
        await SetServiceStaffAsync(row, staffIds, cancellationToken);
    }

    /// <summary>Who can do this service - only staff of the same client are accepted.</summary>
    private async Task SetServiceStaffAsync(ServiceItem service, IEnumerable<Guid> staffIds, CancellationToken cancellationToken)
    {
        var wanted = staffIds.Distinct().ToList();
        var ownStaff = await _db.StaffMembers.Where(s => s.ClientConfigId == service.ClientConfigId && wanted.Contains(s.Id))
            .Select(s => s.Id).ToListAsync(cancellationToken);

        var existing = await _db.StaffServices.Where(x => x.ServiceItemId == service.Id).ToListAsync(cancellationToken);
        _db.StaffServices.RemoveRange(existing.Where(x => !ownStaff.Contains(x.StaffMemberId)));
        foreach (var id in ownStaff.Where(id => existing.All(x => x.StaffMemberId != id)))
            _db.StaffServices.Add(new StaffService { StaffMemberId = id, ServiceItemId = service.Id });
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task DeleteServiceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Old bookings keep the service's name and price; their ServiceItemId just stops pointing anywhere.
        await _db.StaffServices.Where(x => x.ServiceItemId == id).ExecuteDeleteAsync(cancellationToken);
        await _db.ServiceItems.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    // =====================================================================
    // Staff
    // =====================================================================

    public Task<List<StaffMember>> GetStaffAsync(Guid clientConfigId, bool activeOnly = false, CancellationToken cancellationToken = default) =>
        _db.StaffMembers.AsNoTracking().Include(s => s.Services)
            .Where(s => s.ClientConfigId == clientConfigId && (!activeOnly || s.IsActive))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

    public Task<StaffMember?> GetStaffMemberAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.StaffMembers.AsNoTracking().Include(s => s.Services).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task AddStaffAsync(StaffMember staff, IEnumerable<Guid> serviceIds, CancellationToken cancellationToken = default)
    {
        _db.ChangeTracker.Clear();
        staff.Services = new List<StaffService>();
        _db.StaffMembers.Add(staff);
        await _db.SaveChangesAsync(cancellationToken);
        await SetStaffServicesAsync(staff, serviceIds, cancellationToken);
    }

    public async Task UpdateStaffAsync(StaffMember staff, IEnumerable<Guid> serviceIds, CancellationToken cancellationToken = default)
    {
        // Load the row fresh (without its service ticks) and copy the fields across. Attaching the object that
        // was read without tracking, with its old StaffService rows, made EF track the same tick twice and crash.
        _db.ChangeTracker.Clear();
        var row = await _db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staff.Id && s.ClientConfigId == staff.ClientConfigId, cancellationToken);
        if (row is null) return;
        row.Name = staff.Name;
        row.Title = staff.Title;
        row.Gender = staff.Gender;
        row.DaysOffMask = staff.DaysOffMask;
        row.DoesHomeService = staff.DoesHomeService;
        row.IsActive = staff.IsActive;
        row.SortOrder = staff.SortOrder;
        await _db.SaveChangesAsync(cancellationToken);
        await SetStaffServicesAsync(row, serviceIds, cancellationToken);
    }

    private async Task SetStaffServicesAsync(StaffMember staff, IEnumerable<Guid> serviceIds, CancellationToken cancellationToken)
    {
        var wanted = serviceIds.Distinct().ToList();
        var ownServices = await _db.ServiceItems.Where(s => s.ClientConfigId == staff.ClientConfigId && wanted.Contains(s.Id))
            .Select(s => s.Id).ToListAsync(cancellationToken);

        var existing = await _db.StaffServices.Where(x => x.StaffMemberId == staff.Id).ToListAsync(cancellationToken);
        _db.StaffServices.RemoveRange(existing.Where(x => !ownServices.Contains(x.ServiceItemId)));
        foreach (var id in ownServices.Where(id => existing.All(x => x.ServiceItemId != id)))
            _db.StaffServices.Add(new StaffService { StaffMemberId = staff.Id, ServiceItemId = id });
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task DeleteStaffAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Bookings keep the staff member's name; future ones show "staff member removed" and can be re-assigned.
        await _db.StaffServices.Where(x => x.StaffMemberId == id).ExecuteDeleteAsync(cancellationToken);
        await _db.StaffMembers.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    // =====================================================================
    // Bookings
    // =====================================================================

    public async Task<List<BusyBlock>> GetBusyAsync(Guid clientConfigId, DateTime fromUtc, DateTime untilUtc, Guid? exceptBookingId = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Bookings.AsNoTracking()
            .Where(b => b.ClientConfigId == clientConfigId && b.StaffMemberId != null &&
                        (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) &&
                        b.BusyFromUtc < untilUtc && fromUtc < b.BusyUntilUtc &&
                        (exceptBookingId == null || b.Id != exceptBookingId))
            .Select(b => new { b.StaffMemberId, b.BusyFromUtc, b.BusyUntilUtc })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new BusyBlock(r.StaffMemberId!.Value, r.BusyFromUtc, r.BusyUntilUtc)).ToList();
    }

    private Task<bool> ClashesAsync(Guid staffId, DateTime fromUtc, DateTime untilUtc, Guid exceptId, CancellationToken cancellationToken) =>
        _db.Bookings.AnyAsync(b => b.StaffMemberId == staffId && b.Id != exceptId &&
                                   (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) &&
                                   b.BusyFromUtc < untilUtc && fromUtc < b.BusyUntilUtc, cancellationToken);

    public async Task<bool> TryAddAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Serializable: between "is the slot free?" and saving, nobody else can put a booking in that window.
            var strategy = _db.Database.CreateExecutionStrategy();
            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                    if (booking.StaffMemberId is { } staffId &&
                        await ClashesAsync(staffId, booking.BusyFromUtc, booking.BusyUntilUtc, booking.Id, cancellationToken))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return false;
                    }

                    var last = await _db.Bookings.Where(b => b.ClientConfigId == booking.ClientConfigId)
                        .MaxAsync(b => (int?)b.BookingNumber, cancellationToken) ?? 0;
                    booking.BookingNumber = last + 1;
                    booking.Reference = Booking.OwnReference(booking.BookingNumber);

                    _db.Bookings.Add(booking);
                    await _db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    _db.ChangeTracker.Clear();
                    return true;
                });
            }
            catch (Exception ex) when (attempt < 4 && (ex is DbUpdateException || IsDeadlock(ex)))
            {
                // Two bookings saved at the same moment: one is chosen as the deadlock victim or takes the
                // same number - clear and try again (the clash check runs again too).
                _db.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsDeadlock(Exception ex) =>
        ex.GetBaseException().Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> TryRescheduleAsync(Guid clientConfigId, Guid bookingId, DateTime startUtc, DateTime endUtc, DateTime busyFromUtc,
        DateTime busyUntilUtc, DateOnly localDate, Guid staffId, string staffName, string changedBy, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                    if (await ClashesAsync(staffId, busyFromUtc, busyUntilUtc, bookingId, cancellationToken))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return false;
                    }

                    var changed = await _db.Bookings
                        .Where(b => b.Id == bookingId && b.ClientConfigId == clientConfigId &&
                                    (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(b => b.StartUtc, startUtc)
                            .SetProperty(b => b.EndUtc, endUtc)
                            .SetProperty(b => b.BusyFromUtc, busyFromUtc)
                            .SetProperty(b => b.BusyUntilUtc, busyUntilUtc)
                            .SetProperty(b => b.LocalDate, localDate)
                            .SetProperty(b => b.StaffMemberId, staffId)
                            .SetProperty(b => b.StaffName, staffName)
                            .SetProperty(b => b.Status, BookingStatus.Pending)
                            .SetProperty(b => b.StatusChangedBy, changedBy)
                            .SetProperty(b => b.StatusChangedAtUtc, DateTime.UtcNow)
                            .SetProperty(b => b.ReminderDueUtc, (DateTime?)null)
                            .SetProperty(b => b.ReminderSentAtUtc, (DateTime?)null)
                            .SetProperty(b => b.ReminderNote, (string?)null)
                            .SetProperty(b => b.CustomerConfirmedAtUtc, (DateTime?)null), cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return changed == 1;
                });
            }
            catch (Exception ex) when (attempt < 4 && IsDeadlock(ex))
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    public Task<Booking?> GetAsync(Guid clientConfigId, Guid bookingId, CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId && b.ClientConfigId == clientConfigId, cancellationToken);

    public async Task<PagedResult<Booking>> GetPagedAsync(Guid clientConfigId, BookingView view, DateTime utcNow, int page, int pageSize,
        string? search = null, DateOnly? day = null, Guid? staffId = null, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        var query = _db.Bookings.AsNoTracking().Where(b => b.ClientConfigId == clientConfigId);

        query = view switch
        {
            BookingView.ToConfirm => query.Where(b => b.Status == BookingStatus.Pending),
            BookingView.Upcoming => query.Where(b => (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) && b.EndUtc >= utcNow),
            BookingView.Past => query.Where(b => b.Status == BookingStatus.Completed || b.Status == BookingStatus.NoShow ||
                                                 b.Status == BookingStatus.Cancelled || b.Status == BookingStatus.Declined),
            _ => query
        };
        if (day is { } d) query = query.Where(b => b.LocalDate == d);
        if (staffId is { } sid) query = query.Where(b => b.StaffMemberId == sid);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(b =>
                b.Reference.Contains(term) || b.ServiceName.Contains(term) ||
                (b.CustomerName != null && b.CustomerName.Contains(term)) ||
                (b.CustomerPhone != null && b.CustomerPhone.Contains(term)) ||
                (b.StaffName != null && b.StaffName.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        if (page > lastPage) page = lastPage;

        // Things still to come: soonest first. History: newest first.
        var ordered = view is BookingView.Past or BookingView.All
            ? query.OrderByDescending(b => b.StartUtc)
            : query.OrderBy(b => b.StartUtc);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<Booking> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public Task<int> CountAsync(Guid clientConfigId, BookingStatus status, CancellationToken cancellationToken = default) =>
        _db.Bookings.CountAsync(b => b.ClientConfigId == clientConfigId && b.Status == status, cancellationToken);

    public Task<List<Booking>> GetUpcomingForCustomerAsync(Guid clientConfigId, string channel, string customerId, DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking()
            .Where(b => b.ClientConfigId == clientConfigId && b.Channel == channel && b.CustomerId == customerId &&
                        (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) && b.StartUtc > utcNow)
            .OrderBy(b => b.StartUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

    public async Task<bool> SetStatusAsync(Guid clientConfigId, Guid bookingId, BookingStatus status, string changedBy, string? reason = null,
        DateTime? reminderDueUtc = null, CancellationToken cancellationToken = default)
    {
        var cutReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 300)];
        var by = changedBy.Length > 200 ? changedBy[..200] : changedBy;
        var now = DateTime.UtcNow;
        var rows = _db.Bookings.Where(b => b.Id == bookingId && b.ClientConfigId == clientConfigId);
        // Plain SET statements only (no CASE / COALESCE built from C# values), so the SQL is the same on every EF version.
        int changed;
        if (status == BookingStatus.Confirmed)
            changed = cutReason is null
                ? await rows.ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status).SetProperty(b => b.StatusChangedBy, by).SetProperty(b => b.StatusChangedAtUtc, now)
                    .SetProperty(b => b.ReminderDueUtc, reminderDueUtc), cancellationToken)
                : await rows.ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status).SetProperty(b => b.StatusChangedBy, by).SetProperty(b => b.StatusChangedAtUtc, now)
                    .SetProperty(b => b.ReminderDueUtc, reminderDueUtc).SetProperty(b => b.StatusReason, cutReason), cancellationToken);
        else
            changed = cutReason is null
                ? await rows.ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status).SetProperty(b => b.StatusChangedBy, by).SetProperty(b => b.StatusChangedAtUtc, now), cancellationToken)
                : await rows.ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status).SetProperty(b => b.StatusChangedBy, by).SetProperty(b => b.StatusChangedAtUtc, now)
                    .SetProperty(b => b.StatusReason, cutReason), cancellationToken);
        return changed == 1;
    }

    public Task SetCustomerNoticeAsync(Guid clientConfigId, Guid bookingId, bool sent, string note, CancellationToken cancellationToken = default)
    {
        var cut = note.Length > 500 ? note[..500] : note;
        return _db.Bookings.Where(b => b.Id == bookingId && b.ClientConfigId == clientConfigId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.CustomerNoticeSent, sent)
                .SetProperty(b => b.CustomerNoticeNote, cut), cancellationToken);
    }

    public Task SetCustomerConfirmedAsync(Guid clientConfigId, Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default) =>
        _db.Bookings.Where(b => b.Id == bookingId && b.ClientConfigId == clientConfigId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CustomerConfirmedAtUtc, utcNow), cancellationToken);

    // =====================================================================
    // Reminders
    // =====================================================================

    public Task<List<Guid>> GetDueRemindersAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default) =>
        _db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.ReminderDueUtc != null && b.ReminderDueUtc <= utcNow &&
                        b.ReminderSentAtUtc == null && b.StartUtc > utcNow)
            .OrderBy(b => b.ReminderDueUtc)
            .Take(take)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimReminderAsync(Guid bookingId, DateTime utcNow, CancellationToken cancellationToken = default) =>
        await _db.Bookings.Where(b => b.Id == bookingId && b.ReminderSentAtUtc == null && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReminderSentAtUtc, utcNow), cancellationToken) == 1;

    public async Task<Guid?> GetClientIdAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        await _db.Bookings.Where(b => b.Id == bookingId).Select(b => (Guid?)b.ClientConfigId).FirstOrDefaultAsync(cancellationToken);

    public Task SetReminderNoteAsync(Guid bookingId, string note, CancellationToken cancellationToken = default)
    {
        var cut = note.Length > 300 ? note[..300] : note;
        return _db.Bookings.Where(b => b.Id == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReminderNote, cut), cancellationToken);
    }
}
