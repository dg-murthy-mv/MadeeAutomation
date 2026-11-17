using MadeeAutomation.Application.Common;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.VerticalPacks.Messaging;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;
using Microsoft.Extensions.Logging;

namespace MadeeAutomation.Application.VerticalPacks.Beauty;

/// <summary>
/// Messages to the customer about their booking, on the channel they booked on:
///   - staff Confirmed / Declined / Cancelled it (sent when staff press the button)
///   - the reminder before the appointment (sent by the reminder worker)
///
/// English customers get a fixed English text; any other language gets the same text translated by
/// the AI (English is sent if that fails). What happened is saved on the booking so staff can see
/// whether the customer was told - and text them on their mobile if not.
///
/// WhatsApp and Messenger only allow free-form messages within 24 hours of the customer's last
/// message. Outside that window Meta refuses the send; that is recorded, not hidden. For WhatsApp,
/// messages the business starts (reminders, follow-ups, feedback, waitlist offers) use the client's
/// approved templates when set (ClientConfig.WhatsAppTemplate*), which work at any time.
/// </summary>
public class BookingCustomerNotifier
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentClientService _clients;
    private readonly IMessagingConnector _messaging;
    private readonly IClaudeService _claude;
    private readonly ILogger<BookingCustomerNotifier> _logger;

    public BookingCustomerNotifier(IBookingRepository bookings, ICurrentClientService clients, IMessagingConnector messaging,
        IClaudeService claude, ILogger<BookingCustomerNotifier> logger)
    {
        _bookings = bookings;
        _clients = clients;
        _messaging = messaging;
        _claude = claude;
        _logger = logger;
    }

    /// <summary>After staff change the status: tells the customer (Confirmed, Declined, Cancelled by staff).</summary>
    public async Task<OrderNoticeResult> NotifyAsync(Guid clientConfigId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await _bookings.GetAsync(clientConfigId, bookingId, cancellationToken);
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (booking is null || client is null) return new OrderNoticeResult { Outcome = "skipped", Detail = "Booking not found." };

        if (booking.Status is not (BookingStatus.Confirmed or BookingStatus.Declined or BookingStatus.Cancelled))
            return new OrderNoticeResult { Outcome = "skipped", Detail = $"Nothing is sent for {booking.Status}." };
        if (!client.NotifyCustomerOnOrderStatus)
            return new OrderNoticeResult { Outcome = "off", Detail = "Customer messages are switched off for this business." };
        if (string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel))
            return await RecordAsync(booking, false, "Added by staff - there is no chat to message. Call or text the customer.", cancellationToken);

        var english = EnglishText(booking, client);
        var text = await InCustomerLanguageAsync(english, booking, cancellationToken);
        var rich = new RichContent();
        rich.QuickReplies.Add(new ChatButton { Label = "My bookings", Data = "a=bm", DisplayText = "My bookings" });
        if (booking.Status != BookingStatus.Confirmed)
            rich.QuickReplies.Add(new ChatButton { Label = "Book again", Data = "a=bh", DisplayText = "Book again" });

        var result = await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = clientConfigId,
            Channel = booking.Channel,
            To = booking.CustomerId,
            Text = text,
            Rich = rich
        }, cancellationToken);

        if (!result.Success)
        {
            _logger.LogWarning("Could not tell customer {CustomerId} that booking {Reference} is {Status}: {Error}",
                booking.CustomerId, booking.Reference, booking.Status, result.Error);
            return await RecordAsync(booking, false, $"Could not be sent ({Short(result.Error)})", cancellationToken);
        }
        return result.Mode == "mock"
            ? await RecordAsync(booking, false, "Not sent - this channel has no access token (mock mode).", cancellationToken)
            : await RecordAsync(booking, true, text, cancellationToken);
    }

    /// <summary>Sends the reminder for one booking (the worker has already claimed it). Returns what happened.</summary>
    public async Task<string> SendReminderAsync(Guid bookingId, Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var note = await ReminderAsync(bookingId, clientConfigId, second: false, cancellationToken);
        await _bookings.SetReminderNoteAsync(bookingId, note, cancellationToken);
        return note;
    }

    /// <summary>The reminder text and buttons. second = the one 2 hours before.</summary>
    private async Task<string> ReminderAsync(Guid bookingId, Guid clientConfigId, bool second, CancellationToken cancellationToken)
    {
        var booking = await _bookings.GetAsync(clientConfigId, bookingId, cancellationToken);
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (booking is null || client is null) return "booking not found";
        if (booking.Status != BookingStatus.Confirmed || booking.StartUtc <= DateTime.UtcNow) return $"not sent - the booking is {booking.Status}";
        if (string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel))
            return "No chat to send it to - call or text the customer.";

        var local = LocalTime.ToLocal(booking.StartUtc, client.TimeZoneId);
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        var day = BookingDesk.Day(DateOnly.FromDateTime(local), today).ToLowerInvariant();
        var terms = BookingTerms.For(client);
        var english = $"Reminder: your {booking.ServiceName} at {client.ClientName} is {day} " +
                      $"({BookingDesk.When(local, client.Country)}{(second ? ", in about 2 hours" : "")}) with {booking.StaffName}. Ref {booking.Reference}." +
                      (booking.AtHome ? $" We'll come to: {booking.HomeAddress}." : "") +
                      " Tap below to let us know.";

        var id = booking.Id.ToString();
        var rich = new RichContent();
        rich.QuickReplies.Add(new ChatButton { Label = "I'll be there", Data = $"a=by&b={id}", DisplayText = "I'll be there" });
        rich.QuickReplies.Add(new ChatButton { Label = "Reschedule", Data = $"a=br&b={id}", DisplayText = "Reschedule" });
        rich.QuickReplies.Add(new ChatButton { Label = "Cancel", Data = $"a=bx&b={id}", DisplayText = "Cancel" });

        // Template "appointment_reminder": {{1}} service, {{2}} business, {{3}} when, {{4}} staff / doctor, {{5}} reference.
        var template = Template(client, booking.Channel, client.WhatsAppTemplateReminder,
            new[] { booking.ServiceName, client.ClientName, BookingDesk.When(local, client.Country), booking.StaffName ?? "-", booking.Reference },
            rich.QuickReplies.Select(b => b.Data));
        var result = await SendAsync(client, booking, english, rich, template, cancellationToken);
        if (!result.Success)
            _logger.LogWarning("{Which} reminder for {Reference} could not be sent: {Error}", second ? "2-hour" : "Day-before", booking.Reference, result.Error);
        return Note(result, booking, terms);
    }

    /// <summary>
    /// Sends one planned message (the worker has already claimed it): the 2-hour reminder, the follow-up
    /// reminder or the feedback question. Returns what happened (saved on the message for staff).
    /// </summary>
    public async Task<string> SendPlannedAsync(BookingMessage message, CancellationToken cancellationToken = default)
    {
        switch (message.Kind)
        {
            case BookingMessage.KindReminder2:
                return await ReminderAsync(message.BookingId, message.ClientConfigId, second: true, cancellationToken);
            case BookingMessage.KindFollowUp:
                return await FollowUpAsync(message, cancellationToken);
            case BookingMessage.KindFeedback:
                return await FeedbackAsync(message, cancellationToken);
            default:
                return $"unknown message kind {message.Kind}";
        }
    }

    private async Task<string> FollowUpAsync(BookingMessage message, CancellationToken cancellationToken)
    {
        var booking = await _bookings.GetAsync(message.ClientConfigId, message.BookingId, cancellationToken);
        var client = await _clients.GetClientAsync(message.ClientConfigId, cancellationToken);
        if (booking is null || client is null) return "booking not found";
        if (booking.FollowUpDate is not { } date) return "not sent - the follow-up was removed";
        if (string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel))
            return $"No chat to send it to - call or text the patient{(string.IsNullOrWhiteSpace(booking.CustomerPhone) ? "" : " on " + booking.CustomerPhone)}.";

        // Already booked again? Then there is nothing to remind them of.
        var next = (await _bookings.GetUpcomingForCustomerAsync(client.Id, booking.Channel, booking.CustomerId, DateTime.UtcNow, cancellationToken))
            .FirstOrDefault(b => b.Id != booking.Id && b.StartUtc > booking.StartUtc);
        if (next is not null) return $"not sent - already booked ({next.Reference})";

        var dateText = date.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
        var who = booking.StaffName ?? client.ClientName;
        var instructions = string.IsNullOrWhiteSpace(booking.FollowUpNote) ? "" : $"\nFrom {who}: {booking.FollowUpNote.Trim()}";
        var english = $"Hello {booking.CustomerName ?? ""}".TrimEnd() + $", {who} would like to see you again around {dateText} for a follow-up " +
                      $"({booking.ServiceName}, after your visit {booking.Reference}).{instructions}\nTap below to book a time.";

        var rich = new RichContent();
        rich.QuickReplies.Add(new ChatButton { Label = "Book follow-up", Data = $"a=bfu&b={booking.Id}", DisplayText = "Book follow-up" });
        rich.QuickReplies.Add(new ChatButton { Label = "Not now", Data = "a=bfn", DisplayText = "Not now" });

        // Template "follow_up_reminder": {{1}} patient name, {{2}} doctor, {{3}} date, {{4}} instructions ("-" when none).
        var template = Template(client, booking.Channel, client.WhatsAppTemplateFollowUp,
            new[] { booking.CustomerName ?? "there", who, dateText, string.IsNullOrWhiteSpace(booking.FollowUpNote) ? "-" : booking.FollowUpNote.Trim() },
            rich.QuickReplies.Select(b => b.Data));
        // The doctor's words are sent as written - never translated by the AI.
        var result = await SendAsync(client, booking, english, rich, template, cancellationToken, translate: string.IsNullOrWhiteSpace(booking.FollowUpNote));
        return Note(result, booking, BookingTerms.For(client));
    }

    private async Task<string> FeedbackAsync(BookingMessage message, CancellationToken cancellationToken)
    {
        var booking = await _bookings.GetAsync(message.ClientConfigId, message.BookingId, cancellationToken);
        var client = await _clients.GetClientAsync(message.ClientConfigId, cancellationToken);
        if (booking is null || client is null) return "booking not found";
        if (booking.Status != BookingStatus.Completed) return $"not sent - the booking is {booking.Status}";
        if (booking.FeedbackRating is not null) return "not sent - feedback already given";
        if (string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel)) return "No chat to send it to.";

        var local = LocalTime.ToLocal(booking.StartUtc, client.TimeZoneId);
        var english = $"Thank you for visiting {client.ClientName}! How was your {booking.ServiceName} with {booking.StaffName} on " +
                      $"{local.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)}? Tap a rating below (5 = excellent).";
        var rich = new RichContent();
        foreach (var (stars, label) in new[] { (5, "5 - Excellent"), (4, "4 - Good"), (3, "3 - OK"), (2, "2 - Poor"), (1, "1 - Bad") })
            rich.QuickReplies.Add(new ChatButton { Label = label, Data = $"a=bfb&b={booking.Id}&r={stars}", DisplayText = label });

        // Template "visit_feedback": {{1}} business, {{2}} service, {{3}} staff / doctor, {{4}} date.
        var template = Template(client, booking.Channel, client.WhatsAppTemplateFeedback,
            new[] { client.ClientName, booking.ServiceName, booking.StaffName ?? "-", local.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture) },
            rich.QuickReplies.Select(b => b.Data));
        var result = await SendAsync(client, booking, english, rich, template, cancellationToken);
        return Note(result, booking, BookingTerms.For(client));
    }

    /// <summary>
    /// Tells a waiting customer that times opened up (up to 3 shown). Returns what happened.
    /// The WhatsApp template has fixed buttons: "Book it" (the first time), "Other times", "Leave waitlist".
    /// </summary>
    public async Task<string> SendWaitlistOfferAsync(WaitlistEntry entry, ClientConfig client, DateOnly day, IReadOnlyList<DateTime> localTimes,
        CancellationToken cancellationToken = default)
    {
        if (localTimes.Count == 0) return "nothing to offer";
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        var dayText = BookingDesk.Day(day, today);
        var ymd = day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var times = localTimes.Take(3).ToList();
        var timesText = string.Join(", ", times.Select(t => BookingDesk.Time(t, client.Country)));
        var with = string.IsNullOrWhiteSpace(entry.StaffName) ? "" : $" with {entry.StaffName}";
        var dayWords = dayText is "Today" or "Tomorrow" ? dayText.ToLowerInvariant() : dayText;
        var english = $"Good news! A time opened up for {entry.ServiceName}{with} {(dayText is "Today" or "Tomorrow" ? "" : "on ")}{dayWords}: " +
                      $"{timesText}. Tap a time to book it - first come, first served.";

        var rich = new RichContent();
        foreach (var t in times)
        {
            var label = BookingDesk.Time(t, client.Country);
            rich.QuickReplies.Add(new ChatButton { Label = label, Data = $"a=bwo&w={entry.Id}&d={ymd}&t={t:HHmm}", DisplayText = label });
        }
        rich.QuickReplies.Add(new ChatButton { Label = "Other times", Data = $"a=bwo&w={entry.Id}&d={ymd}", DisplayText = "Other times" });
        rich.QuickReplies.Add(new ChatButton { Label = "Leave waitlist", Data = $"a=bwx&w={entry.Id}", DisplayText = "Leave waitlist" });

        // Template "waitlist_offer": {{1}} service (+ doctor), {{2}} day, {{3}} the free times.
        var template = Template(client, entry.Channel, client.WhatsAppTemplateWaitlist,
            new[] { entry.ServiceName + with, dayText, timesText },
            new[] { $"a=bwo&w={entry.Id}&d={ymd}&t={times[0]:HHmm}", $"a=bwo&w={entry.Id}&d={ymd}", $"a=bwx&w={entry.Id}" });

        var text = english;
        if (!IsEnglish(entry.Language) && template is null)
            text = await TranslateAsync(english, entry.Language!, client.Id, null, cancellationToken) ?? english;
        var result = await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = client.Id,
            Channel = entry.Channel,
            To = entry.CustomerId,
            Text = text,
            Rich = rich,
            Template = template
        }, cancellationToken);
        return !result.Success ? $"Not sent ({Short(result.Error)})" : result.Mode == "mock" ? "Not sent - mock mode." : $"Offered {dayText} {timesText}";
    }

    /// <summary>A WhatsApp template when the channel is WhatsApp and one is set; otherwise null (a normal message).</summary>
    private static MessageTemplate? Template(ClientConfig client, string channel, string? name, IEnumerable<string> parameters, IEnumerable<string> payloads) =>
        channel == Channels.WhatsApp && !string.IsNullOrWhiteSpace(name)
            ? new MessageTemplate
            {
                Name = name.Trim(),
                Language = string.IsNullOrWhiteSpace(client.WhatsAppTemplateLanguage) ? "en" : client.WhatsAppTemplateLanguage.Trim(),
                BodyParameters = parameters.ToList(),
                QuickReplyPayloads = payloads.ToList()
            }
            : null;

    /// <summary>Sends in the customer's language (translated by the AI unless English or a template is used).</summary>
    private async Task<MessageSendResult> SendAsync(ClientConfig client, Booking booking, string english, RichContent rich, MessageTemplate? template,
        CancellationToken cancellationToken, bool translate = true)
    {
        var text = template is null && translate ? await InCustomerLanguageAsync(english, booking, cancellationToken) : english;
        return await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = client.Id,
            Channel = booking.Channel,
            To = booking.CustomerId!,
            Text = text,
            Rich = rich,
            Template = template
        }, cancellationToken);
    }

    private static string Note(MessageSendResult result, Booking booking, BookingTerms terms) =>
        !result.Success
            ? $"Not sent ({Short(result.Error)}) - text the {terms.Customer.ToLowerInvariant()}{(string.IsNullOrWhiteSpace(booking.CustomerPhone) ? "" : " on " + booking.CustomerPhone)}."
            : result.Mode == "mock" ? "Not sent - this channel has no access token (mock mode)." : result.Mode == "template" ? "sent (WhatsApp template)" : "sent";

    private static string Short(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "error" : error.Length > 160 ? error[..160] + "..." : error;

    private async Task<OrderNoticeResult> RecordAsync(Booking booking, bool sent, string note, CancellationToken cancellationToken)
    {
        await _bookings.SetCustomerNoticeAsync(booking.ClientConfigId, booking.Id, sent, note, CancellationToken.None);
        return new OrderNoticeResult { Outcome = sent ? "sent" : "failed", Detail = note };
    }

    /// <summary>The fixed English message, built by our code so the facts are always right.</summary>
    public static string EnglishText(Booking b, ClientConfig client)
    {
        var local = LocalTime.ToLocal(b.StartUtc, client.TimeZoneId);
        var what = $"{b.ServiceName} on {BookingDesk.When(local, client.Country)}";
        var reason = string.IsNullOrWhiteSpace(b.StatusReason) ? "" : $" Reason: {b.StatusReason.Trim().TrimEnd('.')}.";
        return b.Status switch
        {
            BookingStatus.Confirmed =>
                $"Your booking {b.Reference} is confirmed: {what} with {b.StaffName}" +
                (b.AtHome ? $", home service at {b.HomeAddress}" : $" at {client.ClientName}") +
                (b.Total is { } t ? $". Price: {Money.Format(t, b.Currency)}" : "") + ". See you then!",
            BookingStatus.Declined =>
                $"Sorry, {client.ClientName} can't take your booking {b.Reference} ({what}).{reason} Please choose another time.",
            _ =>
                $"Sorry, your booking {b.Reference} ({what}) was cancelled by {client.ClientName}.{reason} Please message us or book another time."
        };
    }

    private static bool IsEnglish(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private async Task<string> InCustomerLanguageAsync(string english, Booking booking, CancellationToken cancellationToken)
    {
        if (IsEnglish(booking.Language)) return english;
        return await TranslateAsync(english, booking.Language!, booking.ClientConfigId, booking.Reference, cancellationToken) ?? english;
    }

    /// <summary>The AI translation, or null (then English is sent). mustKeep: text the translation must still contain.</summary>
    private async Task<string?> TranslateAsync(string english, string language, Guid clientConfigId, string? mustKeep, CancellationToken cancellationToken)
    {
        try
        {
            var system =
                "You translate one short chat message from a business (spa, salon or clinic) to its customer.\n" +
                $"Translate it into this language: {language}. Keep it short, friendly and natural.\n" +
                "Keep booking references, service, staff and doctor names, dates, times, prices and addresses EXACTLY as written. " +
                "Do not add or remove any information. Answer with the translated message only.";
            string translated;
            using (AiUsageScope.Begin(clientConfigId, "notice-translate"))
                translated = (await _claude.CompleteAsync(system, english, cancellationToken)).Trim();
            if (translated.Length is > 0 and <= 900 && (mustKeep is null || translated.Contains(mustKeep, StringComparison.OrdinalIgnoreCase)))
                return translated;
            _logger.LogWarning("Translation of a customer message was not usable - sending English");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not translate a customer message - sending English");
        }
        return null;
    }
}

/// <summary>
/// Called every minute by the WebApi reminder worker. Sends what is due: day-before reminders, then the
/// planned messages (2-hour reminders, follow-ups, feedback questions), then offers to customers on the
/// waitlist whose time has opened up. Everything is claimed first, so two WebApi copies never send the
/// same message twice.
/// </summary>
public class BookingReminderRunner
{
    private readonly IBookingRepository _bookings;
    private readonly BookingCustomerNotifier _notifier;
    private readonly BookingDesk _desk;
    private readonly ICurrentClientService _clients;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingReminderRunner> _logger;

    public BookingReminderRunner(IBookingRepository bookings, BookingCustomerNotifier notifier, BookingDesk desk, ICurrentClientService clients,
        TimeProvider clock, ILogger<BookingReminderRunner> logger)
    {
        _bookings = bookings;
        _notifier = notifier;
        _desk = desk;
        _clients = clients;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>How many messages went out.</returns>
    public async Task<int> RunDueAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        var sent = await RemindersAsync(take, cancellationToken);
        sent += await PlannedAsync(take, cancellationToken);
        sent += await WaitlistAsync(take, cancellationToken);
        return sent;
    }

    private async Task<int> RemindersAsync(int take, CancellationToken cancellationToken)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var sent = 0;
        foreach (var id in await _bookings.GetDueRemindersAsync(utcNow, take, cancellationToken))
        {
            if (!await _bookings.TryClaimReminderAsync(id, utcNow, cancellationToken)) continue;
            var clientId = await _bookings.GetClientIdAsync(id, cancellationToken);
            if (clientId is null) continue;
            try
            {
                var note = await _notifier.SendReminderAsync(id, clientId.Value, cancellationToken);
                if (note.StartsWith("sent")) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reminder for booking {BookingId} failed", id);
                await _bookings.SetReminderNoteAsync(id, "Not sent (error) - text the customer.", CancellationToken.None);
            }
        }
        return sent;
    }

    private async Task<int> PlannedAsync(int take, CancellationToken cancellationToken)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var sent = 0;
        foreach (var message in await _bookings.GetDueMessagesAsync(utcNow, take, cancellationToken))
        {
            if (!await _bookings.TryClaimMessageAsync(message.Id, utcNow, cancellationToken)) continue;
            try
            {
                var note = await _notifier.SendPlannedAsync(message, cancellationToken);
                await _bookings.SetMessageNoteAsync(message.Id, note, CancellationToken.None);
                if (note.StartsWith("sent")) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Kind} message for booking {BookingId} failed", message.Kind, message.BookingId);
                await _bookings.SetMessageNoteAsync(message.Id, "Not sent (error).", CancellationToken.None);
            }
        }
        return sent;
    }

    private async Task<int> WaitlistAsync(int take, CancellationToken cancellationToken)
    {
        var waiting = await _bookings.GetWaitingAsync(Math.Max(take, 50), cancellationToken);
        if (waiting.Count == 0) return 0;

        var catalogs = new Dictionary<Guid, BookingCatalog?>();
        var offered = 0;
        foreach (var entry in waiting)
        {
            try
            {
                if (!catalogs.TryGetValue(entry.ClientConfigId, out var cat))
                {
                    var client = await _clients.GetClientAsync(entry.ClientConfigId, cancellationToken);
                    cat = client is { BookingWaitlistEnabled: true } ? await _desk.LoadAsync(client, cancellationToken) : null;
                    catalogs[entry.ClientConfigId] = cat;
                }
                if (cat is null) continue;

                if (entry.LocalDate is { } wanted && wanted < cat.Today)
                {
                    await _bookings.SetWaitlistStatusAsync(entry.Id, WaitlistStatus.Closed, "The day has passed", null, WaitlistStatus.Waiting, cancellationToken);
                    continue;
                }
                if (cat.Service(entry.ServiceItemId) is not { } service || !cat.Bookable.Any(x => x.Id == service.Id))
                {
                    await _bookings.SetWaitlistStatusAsync(entry.Id, WaitlistStatus.Closed, "The service is not offered any more", null, WaitlistStatus.Waiting, cancellationToken);
                    continue;
                }

                var choice = new BookingDesk.Choice(service, false, entry.Preference, entry.StaffMemberId);
                DateOnly? day = entry.LocalDate;
                if (day is null)
                {
                    var open = await _desk.OpenDaysAsync(cat, choice, 1, null, cancellationToken);
                    if (open.Count == 0) continue;
                    day = open[0].Date;
                }
                var slots = await _desk.SlotsAsync(cat, choice, day.Value, null, cancellationToken);
                if (slots.Count == 0) continue;

                // Claim first: the offer goes out once, even with two WebApi copies.
                var utcNow = _clock.GetUtcNow().UtcDateTime;
                if (!await _bookings.SetWaitlistStatusAsync(entry.Id, WaitlistStatus.Offered, "Offering...", utcNow, WaitlistStatus.Waiting, cancellationToken))
                    continue;
                var note = await _notifier.SendWaitlistOfferAsync(entry, cat.Client, day.Value, slots.Select(x => x.LocalStart).ToList(), cancellationToken);
                await _bookings.SetWaitlistStatusAsync(entry.Id, WaitlistStatus.Offered, note, null, null, CancellationToken.None);
                if (note.StartsWith("Offered")) offered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Waitlist entry {EntryId} failed", entry.Id);
            }
        }
        return offered;
    }
}
