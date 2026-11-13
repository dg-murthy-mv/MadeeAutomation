using System.Globalization;
using MadeeAutomation.Admin.Web.Models;
using MadeeAutomation.Admin.Web.Security;
using MadeeAutomation.Admin.Web.Services;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.VerticalPacks.Beauty;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using Microsoft.AspNetCore.Mvc;

namespace MadeeAutomation.Admin.Web.Controllers;

/// <summary>
/// A beauty / wellness client's appointments: confirm or decline what customers asked for in the chat,
/// mark completed / no-show, cancel, add walk-ins and phone bookings, and the booking settings.
/// Used by staff (any client) and by the client's portal user (their own bookings only).
/// </summary>
public class BookingsController : ClientScopedController
{
    private static readonly int[] PageSizes = { 10, 20, 50 };
    private static readonly int[] SlotSteps = { 15, 20, 30, 45, 60 };

    private readonly IBookingRepository _bookings;
    private readonly IClientRepository _clients;
    private readonly IApiService _api;
    private readonly BookingDesk _desk;

    public BookingsController(IBookingRepository bookings, IClientRepository clients, IApiService api, BookingDesk desk)
    {
        _bookings = bookings;
        _clients = clients;
        _api = api;
        _desk = desk;
    }

    /// <summary>show = todo (to confirm) | upcoming | day | past | all.</summary>
    public async Task<IActionResult> Index(Guid clientConfigId, string? show = null, string? day = null, string? q = null, Guid? staff = null,
        int page = 1, int ps = 20)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();

        var utcNow = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(utcNow, client.TimeZoneId));
        DateOnly? forDay = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        show = show is "upcoming" or "day" or "past" or "all" ? show : "todo";
        if (show == "day") forDay ??= today;
        var view = show switch
        {
            "upcoming" => BookingView.Upcoming,
            "day" => BookingView.Day,
            "past" => BookingView.Past,
            "all" => BookingView.All,
            _ => BookingView.ToConfirm
        };

        if (!PageSizes.Contains(ps)) ps = 20;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];
        var bookings = await _bookings.GetPagedAsync(client.Id, view, utcNow, page, ps, q, forDay, staff);

        return View(new BookingsPageModel
        {
            Client = client,
            Bookings = bookings,
            Show = show,
            Day = forDay,
            Today = today,
            Search = q,
            StaffId = staff,
            Staff = await _bookings.GetStaffAsync(client.Id),
            PendingCount = await _bookings.CountAsync(client.Id, BookingStatus.Pending),
            StaffChannelSet = !string.IsNullOrWhiteSpace(client.StaffAlertEmail) || !string.IsNullOrWhiteSpace(client.StaffAlertLineUserId),
            Pager = new PagerModel
            {
                Page = bookings.Page, PageSize = ps, TotalCount = bookings.TotalCount, Controller = "Bookings", Noun = "booking",
                Route = new()
                {
                    ["clientConfigId"] = client.Id.ToString(), ["show"] = show, ["day"] = forDay?.ToString("yyyy-MM-dd"), ["q"] = q,
                    ["staff"] = staff?.ToString(), ["ps"] = ps == 20 ? null : ps.ToString()
                }
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid clientConfigId, Guid id, BookingStatus status, string? reason = null, string? returnUrl = null)
    {
        clientConfigId = Scope(clientConfigId);
        var booking = await _bookings.GetAsync(clientConfigId, id);
        var client = await _clients.GetWithWorkflowsAsync(clientConfigId);
        if (booking is null || client is null) return NotFound();

        var allowed = (booking.Status, status) switch
        {
            (BookingStatus.Pending, BookingStatus.Confirmed) => true,
            (BookingStatus.Pending, BookingStatus.Declined) => true,
            (BookingStatus.Confirmed, BookingStatus.Completed) => true,
            (BookingStatus.Confirmed, BookingStatus.NoShow) => true,
            (BookingStatus.Confirmed, BookingStatus.Cancelled) => true,
            (BookingStatus.Pending, BookingStatus.Cancelled) => true,
            _ => false
        };
        var back = BackTo(returnUrl, RedirectToAction(nameof(Index), new { clientConfigId }));
        if (!allowed)
        {
            TempData["Error"] = $"{booking.Reference} is {booking.Status} - it can't be changed to {status}.";
            return back;
        }

        var reminderDue = status == BookingStatus.Confirmed ? BookingDesk.ReminderDue(booking, client, DateTime.UtcNow) : null;
        await _bookings.SetStatusAsync(clientConfigId, id, status, SignedInAs, status is BookingStatus.Declined or BookingStatus.Cancelled ? reason : null, reminderDue);

        var done = status switch
        {
            BookingStatus.Confirmed => $"{booking.Reference} confirmed.",
            BookingStatus.Declined => $"{booking.Reference} declined.",
            BookingStatus.Completed => $"{booking.Reference} marked completed.",
            BookingStatus.NoShow => $"{booking.Reference} marked as no-show.",
            _ => $"{booking.Reference} cancelled."
        };

        if (status is BookingStatus.Completed or BookingStatus.NoShow)
        {
            TempData["Success"] = done;
            return back;
        }

        // Confirm / decline / cancel: WebApi tells the customer on the channel they booked on.
        var notice = await _api.NotifyCustomerAboutBookingAsync(clientConfigId, id);
        if (notice.Outcome == "sent")
            TempData["Success"] = $"{done} The customer was told on {Channels.DisplayName(booking.Channel)}.";
        else if (notice.Outcome == "off")
            TempData["Success"] = $"{done} Customer messages are switched off, so please tell the customer yourself.";
        else
        {
            TempData["Success"] = done;
            TempData["Error"] = $"The customer could NOT be told automatically ({notice.Detail})" +
                                (string.IsNullOrWhiteSpace(booking.CustomerPhone) ? "." : $" - please text them on {booking.CustomerPhone}.");
        }
        return back;
    }

    // ---------------------------------------------------------------------
    // Add a booking (walk-in / phone call)
    // ---------------------------------------------------------------------

    public async Task<IActionResult> Add(Guid clientConfigId, string? day = null)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        var date = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : today;
        return View(await AddPageAsync(client, new BookingAddModel { ClientConfigId = client.Id, Date = date }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add([Bind(Prefix = "Form")] BookingAddModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(model.ClientConfigId));
        if (client is null) return NotFound();
        model.ClientConfigId = client.Id;

        var cat = await _desk.LoadAsync(client);
        var service = model.ServiceId is { } sid ? cat.Service(sid) : null;
        if (model.ServiceId is not null && service is null) ModelState.AddModelError("Form.ServiceId", "Choose a service that is offered.");
        if (model.AtHome && string.IsNullOrWhiteSpace(model.HomeAddress)) ModelState.AddModelError("Form.HomeAddress", "Type the address for the home service.");
        if (model.StaffId is { } chosen && service is not null && cat.StaffFor(service, model.AtHome).All(m => m.Id != chosen))
            ModelState.AddModelError("Form.StaffId", model.AtHome ? "This person doesn't do this service as home service." : "This person doesn't do this service.");
        if (!ModelState.IsValid) return View(await AddPageAsync(client, model));

        var local = model.Date!.Value.ToDateTime(TimeOnly.ParseExact(model.Time!.PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture));
        var details = new Booking
        {
            Source = Booking.SourceStaff,
            Channel = "",
            CustomerName = model.CustomerName!.Trim(),
            CustomerPhone = string.IsNullOrWhiteSpace(model.CustomerPhone) ? null : model.CustomerPhone.Trim(),
            CustomerNote = string.IsNullOrWhiteSpace(model.CustomerNote) ? null : model.CustomerNote.Trim(),
            HomeAddress = model.AtHome ? model.HomeAddress!.Trim() : null,
            Status = BookingStatus.Confirmed,
            StatusChangedBy = SignedInAs,
            StatusChangedAtUtc = DateTime.UtcNow
        };
        var choice = new BookingDesk.Choice(service!, model.AtHome, model.StaffId is null ? "any" : "staff", model.StaffId);
        var booking = await _desk.CreateByStaffAsync(cat, choice, local, details, model.StaffId);
        if (booking is null)
        {
            ModelState.AddModelError("Form.Time", model.StaffId is null
                ? "Nobody who does this service is free at that time."
                : "This person already has a booking at that time.");
            return View(await AddPageAsync(client, model));
        }

        TempData["Success"] = $"{booking.Reference} added: {booking.ServiceName}, {BookingDesk.When(local, client.Country)} with {booking.StaffName}.";
        return RedirectToAction(nameof(Index), new { clientConfigId = client.Id, show = "day", day = model.Date.Value.ToString("yyyy-MM-dd") });
    }

    private async Task<BookingAddPageModel> AddPageAsync(ClientConfig client, BookingAddModel form) => new()
    {
        Client = client,
        Form = form,
        Services = await _bookings.GetServicesAsync(client.Id, activeOnly: true),
        Staff = await _bookings.GetStaffAsync(client.Id, activeOnly: true)
    };

    // ---------------------------------------------------------------------
    // Booking settings
    // ---------------------------------------------------------------------

    public async Task<IActionResult> Settings(Guid clientConfigId)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(clientConfigId));
        if (client is null) return NotFound();
        return View(new BookingSettingsPageModel
        {
            Client = client,
            Form = new BookingSettingsModel
            {
                ClientConfigId = client.Id,
                SlotMinutes = client.BookingSlotMinutes,
                MinNoticeMinutes = client.BookingMinNoticeMinutes,
                DaysAhead = client.BookingDaysAhead,
                HomeServiceEnabled = client.HomeServiceEnabled,
                HomeServiceFee = client.HomeServiceFee,
                HomeServiceTravelMinutes = client.HomeServiceTravelMinutes,
                RemindersEnabled = client.BookingRemindersEnabled,
                NotifyCustomer = client.NotifyCustomerOnOrderStatus
            }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings([Bind(Prefix = "Form")] BookingSettingsModel model)
    {
        var client = await _clients.GetWithWorkflowsAsync(Scope(model.ClientConfigId));
        if (client is null) return NotFound();
        model.ClientConfigId = client.Id;
        if (!SlotSteps.Contains(model.SlotMinutes)) ModelState.AddModelError("Form.SlotMinutes", "Choose 15, 20, 30, 45 or 60 minutes.");
        if (!ModelState.IsValid) return View(new BookingSettingsPageModel { Client = client, Form = model });

        client.BookingSlotMinutes = model.SlotMinutes;
        client.BookingMinNoticeMinutes = model.MinNoticeMinutes;
        client.BookingDaysAhead = model.DaysAhead;
        client.HomeServiceEnabled = model.HomeServiceEnabled;
        client.HomeServiceFee = model.HomeServiceFee;
        client.HomeServiceTravelMinutes = model.HomeServiceTravelMinutes;
        client.BookingRemindersEnabled = model.RemindersEnabled;
        client.NotifyCustomerOnOrderStatus = model.NotifyCustomer;
        client.UpdatedAtUtc = DateTime.UtcNow;
        await _clients.UpdateAsync(client);

        TempData["Success"] = "Booking settings saved.";
        return RedirectToAction(nameof(Settings), new { clientConfigId = client.Id });
    }
}
