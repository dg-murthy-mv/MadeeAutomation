using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.VerticalPacks.Messaging;
using MadeeAutomation.Domain.Entities;
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
/// message. Outside that window Meta refuses the send; that is recorded, not hidden.
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
        var booking = await _bookings.GetAsync(clientConfigId, bookingId, cancellationToken);
        var client = await _clients.GetClientAsync(clientConfigId, cancellationToken);
        if (booking is null || client is null) return "booking not found";
        if (string.IsNullOrWhiteSpace(booking.CustomerId) || string.IsNullOrWhiteSpace(booking.Channel))
        {
            await _bookings.SetReminderNoteAsync(booking.Id, "No chat to send it to - call or text the customer.", cancellationToken);
            return "no chat";
        }

        var local = LocalTime.ToLocal(booking.StartUtc, client.TimeZoneId);
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(DateTime.UtcNow, client.TimeZoneId));
        var english = $"Reminder: your {booking.ServiceName} at {client.ClientName} is {BookingDesk.Day(DateOnly.FromDateTime(local), today).ToLowerInvariant()} " +
                      $"({BookingDesk.When(local, client.Country)}) with {booking.StaffName}. Ref {booking.Reference}." +
                      (booking.AtHome ? $" We'll come to: {booking.HomeAddress}." : "") +
                      " Tap below to let us know.";
        var text = await InCustomerLanguageAsync(english, booking, cancellationToken);

        var rich = new RichContent();
        var id = booking.Id.ToString();
        rich.QuickReplies.Add(new ChatButton { Label = "I'll be there", Data = $"a=by&b={id}", DisplayText = "I'll be there" });
        rich.QuickReplies.Add(new ChatButton { Label = "Reschedule", Data = $"a=br&b={id}", DisplayText = "Reschedule" });
        rich.QuickReplies.Add(new ChatButton { Label = "Cancel", Data = $"a=bx&b={id}", DisplayText = "Cancel" });

        var result = await _messaging.SendAsync(new OutboundMessage
        {
            ClientConfigId = clientConfigId,
            Channel = booking.Channel,
            To = booking.CustomerId,
            Text = text,
            Rich = rich
        }, cancellationToken);

        var note = !result.Success
            ? $"Not sent ({Short(result.Error)}) - text the customer{(string.IsNullOrWhiteSpace(booking.CustomerPhone) ? "" : " on " + booking.CustomerPhone)}."
            : result.Mode == "mock" ? "Not sent - this channel has no access token (mock mode)." : "sent";
        await _bookings.SetReminderNoteAsync(booking.Id, note, cancellationToken);
        if (!result.Success)
            _logger.LogWarning("Reminder for booking {Reference} could not be sent: {Error}", booking.Reference, result.Error);
        return note;
    }

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
        try
        {
            var system =
                "You translate one short chat message from a spa / salon to its customer.\n" +
                $"Translate it into this language: {booking.Language}. Keep it short, friendly and natural.\n" +
                $"Keep the booking reference {booking.Reference}, the service and staff names, dates, times, prices and addresses EXACTLY as written. " +
                "Do not add or remove any information. Answer with the translated message only.";
            var translated = (await _claude.CompleteAsync(system, english, cancellationToken)).Trim();
            if (translated.Length is > 0 and <= 900 && translated.Contains(booking.Reference, StringComparison.OrdinalIgnoreCase))
                return translated;
            _logger.LogWarning("Translation of the booking message for {Reference} was not usable - sending English", booking.Reference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not translate the booking message for {Reference} - sending English", booking.Reference);
        }
        return english;
    }
}

/// <summary>
/// Sends due reminders (called every minute by the WebApi reminder worker). Each reminder is claimed
/// first, so two WebApi copies never send the same one twice.
/// </summary>
public class BookingReminderRunner
{
    private readonly IBookingRepository _bookings;
    private readonly BookingCustomerNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<BookingReminderRunner> _logger;

    public BookingReminderRunner(IBookingRepository bookings, BookingCustomerNotifier notifier, TimeProvider clock, ILogger<BookingReminderRunner> logger)
    {
        _bookings = bookings;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunDueAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var sent = 0;
        foreach (var id in await _bookings.GetDueRemindersAsync(utcNow, take, cancellationToken))
        {
            if (!await _bookings.TryClaimReminderAsync(id, utcNow, cancellationToken)) continue;
            var clientId = await ClientOfAsync(id, cancellationToken);
            if (clientId is null) continue;
            try
            {
                var note = await _notifier.SendReminderAsync(id, clientId.Value, cancellationToken);
                if (note == "sent") sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reminder for booking {BookingId} failed", id);
                await _bookings.SetReminderNoteAsync(id, "Not sent (error) - text the customer.", CancellationToken.None);
            }
        }
        return sent;
    }

    private async Task<Guid?> ClientOfAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _bookings.GetClientIdAsync(bookingId, cancellationToken);
}
