using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Beauty;

/// <summary>Everything the appointment chat needs, in one constructor argument.</summary>
public class AppointmentServices
{
    public AppointmentServices(BookingDesk desk, BookingAssistant assistant, IBookingRepository bookings, IConversationRepository conversations,
        IBusinessProfileRepository profile, IMessagingConnector messaging, TimeProvider clock)
    {
        Desk = desk;
        Assistant = assistant;
        Bookings = bookings;
        Conversations = conversations;
        Profile = profile;
        Messaging = messaging;
        Clock = clock;
    }

    public BookingDesk Desk { get; }
    public BookingAssistant Assistant { get; }
    public IBookingRepository Bookings { get; }
    public IConversationRepository Conversations { get; }
    public IBusinessProfileRepository Profile { get; }
    public IMessagingConnector Messaging { get; }
    public TimeProvider Clock { get; }
}

/// <summary>A booking being made in the chat, remembered between messages (Conversation.FlowJson).</summary>
public sealed class BookingDraft
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    [JsonPropertyName("s")] public Guid? ServiceId { get; set; }
    [JsonPropertyName("h")] public bool? AtHome { get; set; }
    [JsonPropertyName("p")] public string? Preference { get; set; }
    [JsonPropertyName("m")] public Guid? StaffId { get; set; }
    [JsonPropertyName("d")] public string? Date { get; set; }      // yyyyMMdd
    [JsonPropertyName("t")] public string? Time { get; set; }      // HHmm, local
    [JsonPropertyName("w")] public string? WantedTime { get; set; } // HHmm the customer typed
    [JsonPropertyName("n")] public string? Name { get; set; }
    [JsonPropertyName("ph")] public string? Phone { get; set; }
    [JsonPropertyName("ad")] public string? Address { get; set; }
    [JsonPropertyName("rs")] public Guid? RescheduleId { get; set; }
    [JsonPropertyName("u")] public DateTime UpdatedUtc { get; set; }

    public static BookingDraft Read(string? json, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BookingDraft();
        try
        {
            var d = JsonSerializer.Deserialize<BookingDraft>(json) ?? new BookingDraft();
            return utcNow - d.UpdatedUtc > Lifetime ? new BookingDraft() : d;
        }
        catch (JsonException)
        {
            return new BookingDraft();
        }
    }

    public DateOnly? Day => DateOnly.TryParseExact(Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public TimeOnly? At => TimeOnly.TryParseExact(Time, "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    public TimeOnly? Wanted => TimeOnly.TryParseExact(WantedTime, "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

/// <summary>
/// Customer chat for spas, salons, nails and beauty (HandlerKey "booking-message").
///
/// Booking is done with buttons, answered by our code without the AI (instant, free):
///   Book -> category -> service -> spa or home -> who (anyone / female / male / a person)
///   -> day (only days with free times) -> time (only free times) -> name -> mobile [-> address]
///   -> summary -> "Request booking" -> saved as Pending, staff told, staff confirm on the Bookings page.
/// "My bookings" lists what is coming with Reschedule / Cancel. Typed messages go to the AI, which
/// answers questions from the business information and sends booking requests to the right step
/// ("foot spa tomorrow 3pm?" -> the free times for tomorrow, 3pm first if it is free).
///
/// Button data (a=...):
///   bh home        bpr price list     bc&amp;c= category     bs&amp;s= service      bw&amp;h=0|1 where
///   bp&amp;p=any|F|M  bpl choose a person  bp&amp;p=staff&amp;m= person   bdl days list    bd&amp;d= day
///   bpt&amp;d=&amp;pt=am|pm|ev part of day   bt&amp;d=&amp;t= time   bnu use my name   bphu use this number
///   bok request / move   bm my bookings   bx&amp;b= cancel?   bxx&amp;b= cancel!   br&amp;b= reschedule
///   by&amp;b= "I'll be there" (reminder)   bst talk to staff
/// </summary>
public class AppointmentMessageHandler : IHandler
{
    public string HandlerKey => "booking-message";

    public const string StateName = "bk_name";
    public const string StatePhone = "bk_phone";
    public const string StateAddress = "bk_addr";

    private const int MaxButtons = 10;   // WhatsApp lists hold 10, LINE quick replies 13

    private readonly AppointmentServices _s;

    public AppointmentMessageHandler(AppointmentServices services) => _s = services;

    private sealed class Turn
    {
        public TriggerContext Context = null!;
        public ClientConfig Client = null!;
        public BookingCatalog Cat = null!;
        public BusinessStatus Hours = null!;
        public string Channel = "";
        public string? From;
        public string Text = "";
        public bool Buttons;
        public Conversation Conversation = null!;
        public BookingDraft Draft = new();
        public string? Currency => Client.Currency?.ToUpperInvariant();
        public string? Country => Client.Country;
    }

    public async Task<HandlerResult> HandleAsync(TriggerContext context, CancellationToken cancellationToken = default)
    {
        var channel = Channels.Normalize(context.Channel);
        var text = context.MessageText;
        var from = context.CustomerId;
        var kind = context.MessageKind ?? InboundKinds.Text;

        if (channel is null)
            return HandlerResult.Fail("This handler answers customer messages - it needs a LINE/WhatsApp/Messenger message.");
        if (string.IsNullOrWhiteSpace(text))
            return HandlerResult.Fail("Empty inbound message - nothing to do.");
        if (context.Client is not { } client)
            return HandlerResult.Fail("Client not loaded.");

        var cat = await _s.Desk.LoadAsync(client, cancellationToken);
        var utcNow = cat.UtcNow;
        var today = cat.Today;
        var hours = BusinessClock.GetStatus(cat.Hours, await _s.Profile.GetClosedDatesAsync(client.Id, today.AddDays(-1), cancellationToken),
            client.TimeZoneId, utcNow);

        var turn = new Turn
        {
            Context = context,
            Client = client,
            Cat = cat,
            Hours = hours,
            Channel = channel,
            From = from,
            Text = text.Trim(),
            Buttons = !string.IsNullOrWhiteSpace(from) && cat.Bookable.Count > 0
        };
        turn.Conversation = (turn.Buttons ? await _s.Conversations.GetAsync(client.Id, channel, from!, cancellationToken) : null)
                            ?? new Conversation { ClientConfigId = client.Id, Channel = channel, CustomerId = from ?? "" };
        turn.Draft = BookingDraft.Read(turn.Conversation.FlowJson, utcNow);
        if (turn.Draft.ServiceId is null && turn.Conversation.State.StartsWith("bk_")) turn.Conversation.State = "";

        // ---- Taps and new friends: our code, no AI ----
        if (kind != InboundKinds.Text)
        {
            if (!turn.Buttons)
                return HandlerResult.Ok("Button tap ignored - no bookable services (add services and staff first).");
            if (kind == InboundKinds.Follow)
                return HandlerResult.Ok("New friend - welcome with the booking buttons.",
                    Reply(turn, $"Welcome to {client.ClientName}! Book an appointment here any time.", Home(turn)));
            return await TapAsync(turn, ReadData(text), cancellationToken);
        }

        // ---- We asked for their name / mobile / address and they typed it ----
        if (turn.Buttons && turn.Conversation.State is StateName or StatePhone or StateAddress)
            return await AnswerAsync(turn, cancellationToken);

        // ---- Anything else typed: the AI works out what they want ----
        var analysis = await _s.Assistant.AnalyzeAsync(cat, hours, turn.Text, cancellationToken);
        if (analysis is null) return HandlerResult.Fail("The AI reply was not valid JSON (tried twice).");

        if (!string.IsNullOrWhiteSpace(analysis.Language) &&
            !string.Equals(turn.Conversation.Language, analysis.Language.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            turn.Conversation.Language = Cut(analysis.Language.Trim(), 40);
            await SaveAsync(turn, cancellationToken);
        }

        var cannotAnswer = analysis.Intent == "question" && !analysis.CanAnswer;
        if (analysis.NeedsHuman || cannotAnswer)
        {
            var reply = string.IsNullOrWhiteSpace(analysis.Reply) ? client.EffectiveFallbackReply : analysis.Reply;
            return HandlerResult.Ok("Escalated to staff.", Reply(turn, reply, Hint(turn)),
                StaffAlert(turn, cannotAnswer ? "question not covered by the business information" : "needs a person"));
        }

        var line = string.IsNullOrWhiteSpace(analysis.Reply) ? null : analysis.Reply;
        if (!turn.Buttons)
            return line is null
                ? HandlerResult.Fail($"The AI returned no reply text (intent: {analysis.Intent}).")
                : HandlerResult.Ok($"Replied ({analysis.Intent}) - booking buttons not available.", Reply(turn, line));

        switch (analysis.Intent)
        {
            case "book":
            {
                turn.Draft = new BookingDraft();
                if (BookingAssistant.MatchService(cat.Bookable, analysis.Service) is { } service) turn.Draft.ServiceId = service.Id;
                if (analysis.Date is { } d && d >= today) turn.Draft.Date = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (analysis.Time is { } t) turn.Draft.WantedTime = t.ToString("HHmm", CultureInfo.InvariantCulture);
                return await NextStepAsync(turn, line ?? "Sure! Let's find you a time.", cancellationToken);
            }
            case "my_bookings":
                return await MyBookingsAsync(turn, line, cancellationToken);
            case "services":
            case "greeting":
                return HandlerResult.Ok($"Replied ({analysis.Intent}) with the booking buttons.",
                    Reply(turn, line ?? $"Hi! Welcome to {client.ClientName}.", Home(turn)));
            default:
                return line is null
                    ? HandlerResult.Fail($"The AI returned no reply text (intent: {analysis.Intent}).")
                    : HandlerResult.Ok(analysis.Intent == "question" ? "Answered from the business information." : $"Replied ({analysis.Intent}).",
                        Reply(turn, line, Hint(turn)));
        }
    }

    // =====================================================================
    // Taps
    // =====================================================================

    private async Task<HandlerResult> TapAsync(Turn turn, Dictionary<string, string> data, CancellationToken ct)
    {
        var cat = turn.Cat;
        var action = data.GetValueOrDefault("a", "");
        Guid? G(string key) => Guid.TryParse(data.GetValueOrDefault(key), out var g) ? g : null;

        switch (action)
        {
            case "bh":
                turn.Conversation.State = "";
                turn.Draft = new BookingDraft();
                await SaveAsync(turn, ct);
                return HandlerResult.Ok("Booking: home.", Reply(turn, "What would you like to book?", Home(turn)));

            case "bpr":
                return HandlerResult.Ok("Booking: price list.", Reply(turn, PriceList(turn), Home(turn)));

            case "bc":
            {
                var category = data.GetValueOrDefault("c", BookingDesk.NoCategory);
                var services = cat.InCategory(category);
                if (services.Count == 0) return HandlerResult.Ok("Booking: empty category.", Reply(turn, "That category has no services right now.", Home(turn)));
                return HandlerResult.Ok($"Booking: services in {category}.",
                    Reply(turn, category == BookingDesk.NoCategory ? "Our services:" : $"{category}:", ServiceCards(turn, services)));
            }

            case "bs":
            {
                if (G("s") is not { } id || cat.Service(id) is null || !cat.Bookable.Any(s => s.Id == id))
                    return HandlerResult.Ok("Booking: unknown service.", Reply(turn, "Sorry, that service is not available any more.", Home(turn)));
                turn.Draft = new BookingDraft { ServiceId = id };
                return await NextStepAsync(turn, null, ct);
            }

            case "bw":
                turn.Draft.AtHome = data.GetValueOrDefault("h") == "1";
                turn.Draft.Time = null;   // a day the customer typed ("bukas") is kept
                return await NextStepAsync(turn, null, ct);

            case "bp":
            {
                var p = data.GetValueOrDefault("p", "any");
                turn.Draft.Preference = p is "F" or "M" or "staff" ? p : "any";
                turn.Draft.StaffId = p == "staff" ? G("m") : null;
                turn.Draft.Time = null;   // a day the customer typed ("bukas") is kept
                return await NextStepAsync(turn, null, ct);
            }

            case "bpl":
                return ChooseStaff(turn);

            case "bdl":
                turn.Draft.Date = null; turn.Draft.Time = null; turn.Draft.WantedTime = null;
                return await NextStepAsync(turn, null, ct);

            case "bd":
                turn.Draft.Date = data.GetValueOrDefault("d");
                turn.Draft.Time = null;
                return await NextStepAsync(turn, null, ct);

            case "bpt":
                turn.Draft.Date = data.GetValueOrDefault("d");
                return await TimesAsync(turn, null, data.GetValueOrDefault("pt"), ct);

            case "bt":
                turn.Draft.Date = data.GetValueOrDefault("d");
                turn.Draft.Time = data.GetValueOrDefault("t");
                return await NextStepAsync(turn, null, ct);

            case "bnu":
                turn.Draft.Name = await KnownNameAsync(turn, ct);
                return await NextStepAsync(turn, null, ct);

            case "bphu":
                turn.Draft.Phone = turn.Channel == Channels.WhatsApp && turn.From is { } wa ? "+" + wa.TrimStart('+') : null;
                return await NextStepAsync(turn, null, ct);

            case "bok":
                return await RequestAsync(turn, ct);

            case "bm":
                return await MyBookingsAsync(turn, null, ct);

            case "bx":
                return await CancelAskAsync(turn, G("b"), ct);

            case "bxx":
                return await CancelAsync(turn, G("b"), ct);

            case "br":
                return await RescheduleStartAsync(turn, G("b"), ct);

            case "by":
                return await ConfirmFromReminderAsync(turn, G("b"), ct);

            case "bst":
                return HandlerResult.Ok("Customer asked for a person.",
                    Reply(turn, "A member of our team will message you here soon.", Hint(turn)), StaffAlert(turn, "tapped Talk to staff"));

            default:
                return HandlerResult.Ok($"Unknown button ({action}) - showed the booking buttons.", Reply(turn, "What would you like to book?", Home(turn)));
        }
    }

    // =====================================================================
    // The booking steps
    // =====================================================================

    /// <summary>Asks for whatever the draft is still missing, in order. intro goes first in the reply.</summary>
    private async Task<HandlerResult> NextStepAsync(Turn turn, string? intro, CancellationToken ct)
    {
        var cat = turn.Cat;
        var d = turn.Draft;
        string Say(string text) => intro is null ? text : intro + "\n" + text;

        if (d.ServiceId is not { } serviceId || cat.Service(serviceId) is not { } service)
        {
            turn.Draft.ServiceId = null;
            await SaveAsync(turn, ct);
            return HandlerResult.Ok("Booking: asked for the service.", Reply(turn, Say("Which service would you like?"), Home(turn)));
        }

        if (!cat.HoursConfigured)
            return HandlerResult.Ok("Booking: no opening hours set - handed to staff.",
                Reply(turn, Say("A member of our team will message you to arrange a time."), Hint(turn)),
                StaffAlert(turn, $"wants to book {service.Name} - no opening hours are set, so the bot cannot offer times"));

        // Where
        if (d.AtHome is null)
        {
            if (!cat.HomeServiceFor(service)) d.AtHome = false;
            else
            {
                await SaveAsync(turn, ct);
                var fee = turn.Client.HomeServiceFee is { } f and > 0 ? $" (+{Money.Format(f, turn.Currency)})" : "";
                var rich = new RichContent();
                rich.QuickReplies.Add(Button("At the spa", Data("bw", ("h", "0"))));
                rich.QuickReplies.Add(Button("Home service", Data("bw", ("h", "1"))));
                return HandlerResult.Ok("Booking: asked spa or home.",
                    Reply(turn, Say($"{service.Name} - {BookingDesk.ServiceLine(service, turn.Currency)}.\nAt our place, or home service{fee}?"), rich));
            }
        }
        var atHome = d.AtHome == true;

        // Who
        var able = cat.StaffFor(service, atHome);
        if (d.Preference is null)
        {
            if (able.Count <= 1) d.Preference = "any";
            else
            {
                await SaveAsync(turn, ct);
                var rich = new RichContent();
                rich.QuickReplies.Add(Button("Anyone available", Data("bp", ("p", "any"))));
                if (able.Any(m => m.Gender == "F") && able.Any(m => m.Gender != "F")) rich.QuickReplies.Add(Button("Female", Data("bp", ("p", "F"))));
                if (able.Any(m => m.Gender == "M") && able.Any(m => m.Gender != "M")) rich.QuickReplies.Add(Button("Male", Data("bp", ("p", "M"))));
                rich.QuickReplies.Add(Button("Choose a person", Data("bpl")));
                return HandlerResult.Ok("Booking: asked who.",
                    Reply(turn, Say($"{service.Name} - {BookingDesk.ServiceLine(service, turn.Currency)}.\nWho would you like?"), rich));
            }
        }

        var choice = new BookingDesk.Choice(service, atHome, d.Preference, d.StaffId);

        // Day
        if (d.Day is not { } day || day < cat.Today)
        {
            d.Date = null;
            var days = await _s.Desk.OpenDaysAsync(cat, choice, MaxButtons, d.RescheduleId, ct);
            await SaveAsync(turn, ct);
            if (days.Count == 0)
                return HandlerResult.Ok("Booking: no free days.",
                    Reply(turn, Say($"Sorry, there are no free times in the next {turn.Client.BookingDaysAhead} days for this choice."), NoTimes(turn)));
            var rich = new RichContent();
            foreach (var (date, _) in days) rich.QuickReplies.Add(Button(BookingDesk.Day(date, cat.Today), Data("bd", ("d", Ymd(date)))));
            return HandlerResult.Ok("Booking: asked the day.", Reply(turn, Say("Which day?"), rich));
        }

        // Time
        if (d.At is not { } at)
            return await TimesAsync(turn, intro, null, ct);

        // The picked time must still be free (someone may have taken it while they chose).
        var local = day.ToDateTime(at);
        var slots = await _s.Desk.SlotsAsync(cat, choice, day, d.RescheduleId, ct);
        if (slots.All(s => s.LocalStart != local))
        {
            d.Time = null;
            return await TimesAsync(turn, "Sorry, that time was just taken.", null, ct);
        }

        // Moving an existing booking: no need for name / phone again.
        if (d.RescheduleId is { } moving)
        {
            await SaveAsync(turn, ct);
            var rich = new RichContent();
            rich.QuickReplies.Add(Button("Yes, move it", Data("bok")));
            rich.QuickReplies.Add(Button("Other time", Data("bdl")));
            rich.QuickReplies.Add(Button("Keep old time", Data("bm")));
            return HandlerResult.Ok("Booking: asked to confirm the move.",
                Reply(turn, Say($"Move your {service.Name} to {BookingDesk.Day(day, cat.Today)}, {BookingDesk.Time(local, turn.Country)}?"), rich));
        }

        // Name
        if (string.IsNullOrWhiteSpace(d.Name))
        {
            turn.Conversation.State = StateName;
            await SaveAsync(turn, ct);
            var known = await KnownNameAsync(turn, ct);
            var rich = new RichContent();
            if (!string.IsNullOrWhiteSpace(known)) rich.QuickReplies.Add(Button(Cut("Use " + known, 20)!, Data("bnu")));
            rich.QuickReplies.Add(Button("Start over", Data("bh")));
            return HandlerResult.Ok("Booking: asked the name.",
                Reply(turn, Say($"{BookingDesk.Day(day, cat.Today)}, {BookingDesk.Time(local, turn.Country)} - great!\nWhat name should we put the booking under?" +
                                (string.IsNullOrWhiteSpace(known) ? "" : $" (or tap \"Use {known}\")")), rich));
        }

        // Mobile
        if (string.IsNullOrWhiteSpace(d.Phone))
        {
            turn.Conversation.State = StatePhone;
            await SaveAsync(turn, ct);
            var rich = new RichContent();
            if (turn.Channel == Channels.WhatsApp) rich.QuickReplies.Add(Button("Use this number", Data("bphu")));
            rich.QuickReplies.Add(Button("Start over", Data("bh")));
            return HandlerResult.Ok("Booking: asked the mobile number.", Reply(turn, Say("Your mobile number, please? (so we can reach you)"), rich));
        }

        // Address (home service)
        if (atHome && string.IsNullOrWhiteSpace(d.Address))
        {
            turn.Conversation.State = StateAddress;
            await SaveAsync(turn, ct);
            var rich = new RichContent();
            rich.QuickReplies.Add(Button("Start over", Data("bh")));
            return HandlerResult.Ok("Booking: asked the address.",
                Reply(turn, Say("Your full address for the home service? (house no., street, barangay, city - and a landmark helps)"), rich));
        }

        // Summary
        turn.Conversation.State = "";
        await SaveAsync(turn, ct);
        var summary = new RichContent();
        summary.QuickReplies.Add(Button("Request booking", Data("bok")));
        summary.QuickReplies.Add(Button("Change time", Data("bdl")));
        summary.QuickReplies.Add(Button("Start over", Data("bh")));
        return HandlerResult.Ok("Booking: showed the summary.", Reply(turn, Say(Summary(turn, service, local)), summary));
    }

    /// <summary>The free times of the chosen day: all of them, or morning / afternoon / evening first when there are many.</summary>
    private async Task<HandlerResult> TimesAsync(Turn turn, string? intro, string? part, CancellationToken ct)
    {
        var cat = turn.Cat;
        var d = turn.Draft;
        if (d.ServiceId is not { } sid || cat.Service(sid) is not { } service || d.Day is not { } day || d.Preference is null || d.AtHome is null)
            return await NextStepAsync(turn, intro, ct);

        var choice = new BookingDesk.Choice(service, d.AtHome == true, d.Preference, d.StaffId);
        var slots = await _s.Desk.SlotsAsync(cat, choice, day, d.RescheduleId, ct);
        string Say(string text) => intro is null ? text : intro + "\n" + text;
        var dayText = BookingDesk.Day(day, cat.Today);

        // They typed a time ("3pm"): take it straight away when it is free.
        if (d.Wanted is { } wanted)
        {
            d.WantedTime = null;
            var exact = slots.FirstOrDefault(s => TimeOnly.FromDateTime(s.LocalStart) == wanted);
            var wantedText = BookingDesk.Time(day.ToDateTime(wanted), turn.Country);
            if (exact is not null)
            {
                d.Time = exact.LocalStart.ToString("HHmm", CultureInfo.InvariantCulture);
                return await NextStepAsync(turn, Say($"{dayText} at {wantedText} is free!"), ct);
            }
            intro = Say($"{dayText} at {wantedText} is not free.");
        }

        await SaveAsync(turn, ct);
        if (slots.Count == 0)
        {
            var rich = new RichContent();
            rich.QuickReplies.Add(Button("Other day", Data("bdl")));
            rich.QuickReplies.Add(Button("Talk to staff", Data("bst")));
            return HandlerResult.Ok("Booking: no free times that day.", Reply(turn, Say($"Sorry, {dayText} is fully booked."), rich));
        }

        static string PartOf(DateTime t) => t.Hour < 12 ? "am" : t.Hour < 17 ? "pm" : "ev";
        var shown = part is "am" or "pm" or "ev" ? slots.Where(s => PartOf(s.LocalStart) == part).ToList() : slots;

        if (part is null && slots.Count > MaxButtons)
        {
            var rich = new RichContent();
            foreach (var (key, label) in new[] { ("am", "Morning"), ("pm", "Afternoon"), ("ev", "Evening") })
            {
                var n = slots.Count(s => PartOf(s.LocalStart) == key);
                if (n > 0) rich.QuickReplies.Add(Button($"{label} ({n})", Data("bpt", ("d", Ymd(day)), ("pt", key))));
            }
            rich.QuickReplies.Add(Button("Other day", Data("bdl")));
            return HandlerResult.Ok($"Booking: {slots.Count} free times - asked the part of the day.",
                Reply(turn, Say($"{dayText}: {slots.Count} free times. Morning, afternoon or evening?"), rich));
        }

        var times = new RichContent();
        foreach (var s in shown.Take(MaxButtons))
            times.QuickReplies.Add(Button(BookingDesk.Time(s.LocalStart, turn.Country), Data("bt", ("d", Ymd(day)), ("t", s.LocalStart.ToString("HHmm", CultureInfo.InvariantCulture)))));
        times.QuickReplies.Add(Button("Other day", Data("bdl")));
        return HandlerResult.Ok($"Booking: showed {Math.Min(shown.Count, MaxButtons)} free time(s).",
            Reply(turn, Say($"Free times on {dayText}:"), times));
    }

    private HandlerResult ChooseStaff(Turn turn)
    {
        var cat = turn.Cat;
        if (turn.Draft.ServiceId is not { } sid || cat.Service(sid) is not { } service)
            return HandlerResult.Ok("Booking: no service picked.", Reply(turn, "Which service would you like?", Home(turn)));

        var able = cat.StaffFor(service, turn.Draft.AtHome == true);
        var rich = new RichContent();
        foreach (var m in able.Take(MaxButtons))
            rich.QuickReplies.Add(Button(Cut(m.Name, 20)!, Data("bp", ("p", "staff"), ("m", m.Id.ToString()))));
        rich.QuickReplies.Add(Button("Anyone available", Data("bp", ("p", "any"))));
        var list = string.Join("\n", able.Take(MaxButtons).Select(m => $"- {m.Name}" + (string.IsNullOrWhiteSpace(m.Title) ? "" : $" ({m.Title})")));
        return HandlerResult.Ok("Booking: listed staff.", Reply(turn, $"Who would you like?\n{list}", rich));
    }

    /// <summary>A typed answer to "your name / mobile / address?".</summary>
    private async Task<HandlerResult> AnswerAsync(Turn turn, CancellationToken ct)
    {
        var text = turn.Text.Trim();
        switch (turn.Conversation.State)
        {
            case StateName:
                if (text.Length < 2 || text.Length > 100)
                    return HandlerResult.Ok("Booking: name not accepted.", Reply(turn, "Please type the name for the booking (2-100 characters).", StartOver()));
                turn.Draft.Name = text;
                break;
            case StatePhone:
                var digits = new string(text.Where(char.IsDigit).ToArray());
                if (digits.Length is < 7 or > 15 || text.Any(c => char.IsLetter(c)))
                    return HandlerResult.Ok("Booking: phone not accepted.", Reply(turn, "That doesn't look like a mobile number. Please type it again, e.g. 0917 123 4567.", StartOver()));
                turn.Draft.Phone = Cut(text, 30);
                break;
            case StateAddress:
                if (text.Length < 8)
                    return HandlerResult.Ok("Booking: address not accepted.", Reply(turn, "Please type the full address (house no., street, barangay, city).", StartOver()));
                turn.Draft.Address = Cut(text, 500);
                break;
        }
        turn.Conversation.State = "";
        return await NextStepAsync(turn, null, ct);
    }

    /// <summary>"Request booking" (or "Yes, move it"): saves it, tells staff.</summary>
    private async Task<HandlerResult> RequestAsync(Turn turn, CancellationToken ct)
    {
        var cat = turn.Cat;
        var d = turn.Draft;
        if (d.ServiceId is not { } sid || cat.Service(sid) is not { } service || d.Day is not { } day || d.At is not { } at || d.Preference is null)
            return await NextStepAsync(turn, null, ct);

        var choice = new BookingDesk.Choice(service, d.AtHome == true, d.Preference, d.StaffId);
        var local = day.ToDateTime(at);
        var when = $"{BookingDesk.Day(day, cat.Today)}, {BookingDesk.Time(local, turn.Country)}";

        if (turn.Context.IsTest)
            return HandlerResult.Ok("Test run - booking not saved.", Reply(turn, $"(Test) Your booking request for {service.Name} on {when} would be sent to the staff now."));

        // ---- Moving a booking ----
        if (d.RescheduleId is { } moving)
        {
            var existing = await _s.Bookings.GetAsync(turn.Client.Id, moving, ct);
            if (existing is null || !Owns(turn, existing) || !existing.IsHolding)
                return HandlerResult.Ok("Booking: the booking to move is gone.", Reply(turn, "Sorry, I can't find that booking any more.", Home(turn)));

            var moved = await _s.Desk.RescheduleAsync(cat, existing, choice, local, "customer (chat)", ct);
            if (moved is null) return await TimesAsync(turn, "Sorry, that time was just taken.", null, ct);

            turn.Draft = new BookingDraft();
            await SaveAsync(turn, ct);
            var after = await _s.Bookings.GetAsync(turn.Client.Id, moving, ct) ?? existing;
            return HandlerResult.Ok($"Booking {existing.Reference} moved to {when} - waiting for staff.",
                Reply(turn, $"Done - {existing.Reference} is moved to {when} with {moved.Value.Staff.Name}. We'll confirm shortly.", Hint(turn)),
                TellStaff(turn, after, "Booking MOVED by the customer"));
        }

        // ---- New booking ----
        var details = new Booking
        {
            Source = Booking.SourceChat,
            Channel = turn.Channel,
            CustomerId = Cut(turn.From, 200),
            CustomerName = Cut(d.Name, 200),
            CustomerPhone = Cut(d.Phone, 30),
            HomeAddress = Cut(d.Address, 500),
            Language = turn.Conversation.Language,
            Status = BookingStatus.Pending
        };
        var booking = await _s.Desk.CreateAsync(cat, choice, local, details, ct);
        if (booking is null) return await TimesAsync(turn, "Sorry, that time was just taken.", null, ct);

        turn.Draft = new BookingDraft();
        await SaveAsync(turn, ct);

        var price = booking.Total is { } t ? $"\nPrice: {Money.Format(t, booking.Currency)}" : "";
        return HandlerResult.Ok($"Booking {booking.Reference} requested ({service.Name}, {when}, {booking.StaffName}).",
            Reply(turn, $"Thank you, {booking.CustomerName}! Your booking request {booking.Reference} is received:\n" +
                        $"{service.Name} - {when} with {booking.StaffName}{price}\nWe'll confirm it shortly.", Hint(turn)),
            TellStaff(turn, booking, "New booking"));
    }

    // =====================================================================
    // My bookings, cancel, reschedule, reminder answer
    // =====================================================================

    private async Task<HandlerResult> MyBookingsAsync(Turn turn, string? intro, CancellationToken ct)
    {
        var mine = await _s.Bookings.GetUpcomingForCustomerAsync(turn.Client.Id, turn.Channel, turn.From ?? "", turn.Cat.UtcNow, ct);
        if (mine.Count == 0)
        {
            var rich = new RichContent();
            rich.QuickReplies.Add(Button("Book now", Data("bh")));
            return HandlerResult.Ok("Booking: no upcoming bookings.", Reply(turn, (intro is null ? "" : intro + "\n") + "You have no upcoming bookings.", rich));
        }

        var cards = new RichContent();
        foreach (var b in mine.Take(MaxButtons))
        {
            var local = LocalTime.ToLocal(b.StartUtc, turn.Client.TimeZoneId);
            cards.Cards.Add(new ChatCard
            {
                Title = b.ServiceName,
                Subtitle = $"{BookingDesk.When(local, turn.Country)} · {b.StaffName}",
                Note = $"{b.Reference} · {(b.Status == BookingStatus.Confirmed ? "Confirmed" : "Waiting for confirmation")}",
                Buttons =
                {
                    Button("Reschedule", Data("br", ("b", b.Id.ToString()))),
                    Button("Cancel", Data("bx", ("b", b.Id.ToString())))
                }
            });
        }
        cards.QuickReplies.Add(Button("Book another", Data("bh")));
        return HandlerResult.Ok($"Booking: listed {mine.Count} upcoming booking(s).",
            Reply(turn, (intro is null ? "" : intro + "\n") + "Your upcoming bookings:", cards));
    }

    private bool Owns(Turn turn, Booking b) =>
        b.Channel == turn.Channel && !string.IsNullOrWhiteSpace(b.CustomerId) && b.CustomerId == turn.From;

    private async Task<HandlerResult> CancelAskAsync(Turn turn, Guid? id, CancellationToken ct)
    {
        var b = id is { } g ? await _s.Bookings.GetAsync(turn.Client.Id, g, ct) : null;
        if (b is null || !Owns(turn, b) || !b.IsHolding)
            return await MyBookingsAsync(turn, "That booking can't be cancelled any more.", ct);

        var local = LocalTime.ToLocal(b.StartUtc, turn.Client.TimeZoneId);
        var rich = new RichContent();
        rich.QuickReplies.Add(Button("Yes, cancel it", Data("bxx", ("b", b.Id.ToString()))));
        rich.QuickReplies.Add(Button("Keep it", Data("bm")));
        return HandlerResult.Ok($"Booking: asked to cancel {b.Reference}.",
            Reply(turn, $"Cancel {b.Reference} - {b.ServiceName}, {BookingDesk.When(local, turn.Country)}?", rich));
    }

    private async Task<HandlerResult> CancelAsync(Turn turn, Guid? id, CancellationToken ct)
    {
        var b = id is { } g ? await _s.Bookings.GetAsync(turn.Client.Id, g, ct) : null;
        if (b is null || !Owns(turn, b) || !b.IsHolding)
            return await MyBookingsAsync(turn, "That booking can't be cancelled any more.", ct);
        if (turn.Context.IsTest)
            return HandlerResult.Ok("Test run - not cancelled.", Reply(turn, $"(Test) {b.Reference} would be cancelled now."));

        await _s.Bookings.SetStatusAsync(turn.Client.Id, b.Id, BookingStatus.Cancelled, "customer (chat)", "Cancelled by the customer", null, ct);
        b.Status = BookingStatus.Cancelled;
        b.StatusReason = "Cancelled by the customer";
        var rich = new RichContent();
        rich.QuickReplies.Add(Button("Book again", Data("bh")));
        return HandlerResult.Ok($"Booking {b.Reference} cancelled by the customer.",
            Reply(turn, $"{b.Reference} is cancelled. We hope to see you another time!", rich),
            TellStaff(turn, b, "Booking CANCELLED by the customer"));
    }

    private async Task<HandlerResult> RescheduleStartAsync(Turn turn, Guid? id, CancellationToken ct)
    {
        var b = id is { } g ? await _s.Bookings.GetAsync(turn.Client.Id, g, ct) : null;
        if (b is null || !Owns(turn, b) || !b.IsHolding)
            return await MyBookingsAsync(turn, "That booking can't be moved any more.", ct);
        if (b.ServiceItemId is not { } sid || turn.Cat.Service(sid) is null)
            return HandlerResult.Ok($"Booking: {b.Reference} cannot be moved in the chat (service changed).",
                Reply(turn, "A member of our team will message you to move this booking.", Hint(turn)), StaffAlert(turn, $"wants to move {b.Reference}"));

        turn.Draft = new BookingDraft
        {
            ServiceId = sid,
            AtHome = b.AtHome,
            Preference = b.StaffPreference is "F" or "M" or "staff" ? b.StaffPreference : "any",
            StaffId = b.StaffPreference == "staff" ? b.StaffMemberId : null,
            RescheduleId = b.Id
        };
        return await NextStepAsync(turn, $"Moving {b.Reference} ({b.ServiceName}).", ct);
    }

    private async Task<HandlerResult> ConfirmFromReminderAsync(Turn turn, Guid? id, CancellationToken ct)
    {
        var b = id is { } g ? await _s.Bookings.GetAsync(turn.Client.Id, g, ct) : null;
        if (b is null || !Owns(turn, b) || !b.IsHolding)
            return await MyBookingsAsync(turn, null, ct);
        if (!turn.Context.IsTest) await _s.Bookings.SetCustomerConfirmedAsync(turn.Client.Id, b.Id, turn.Cat.UtcNow, ct);
        var local = LocalTime.ToLocal(b.StartUtc, turn.Client.TimeZoneId);
        return HandlerResult.Ok($"Customer confirmed they will come to {b.Reference}.",
            Reply(turn, $"Thank you! See you on {BookingDesk.When(local, turn.Country)}.", Hint(turn)));
    }

    // =====================================================================
    // What we show
    // =====================================================================

    /// <summary>Book (by category), prices, my bookings, talk to staff.</summary>
    private static RichContent Home(Turn turn)
    {
        var rich = new RichContent();
        var categories = turn.Cat.Categories;
        if (categories.Count <= 1)
        {
            return ServiceCards(turn, turn.Cat.Bookable);
        }
        foreach (var c in categories.Take(MaxButtons - 2))
            rich.QuickReplies.Add(Button(c == BookingDesk.NoCategory ? "Other services" : Cut(c, 20)!, Data("bc", ("c", c))));
        rich.QuickReplies.Add(Button("My bookings", Data("bm")));
        rich.QuickReplies.Add(Button("Talk to staff", Data("bst")));
        return rich;
    }

    private static RichContent ServiceCards(Turn turn, List<ServiceItem> services)
    {
        var rich = new RichContent();
        foreach (var s in services.Take(MaxButtons))
        {
            rich.Cards.Add(new ChatCard
            {
                Title = s.Name,
                Subtitle = BookingDesk.ServiceLine(s, turn.Currency),
                Note = string.IsNullOrWhiteSpace(s.Description) ? (turn.Cat.HomeServiceFor(s) ? "Home service available" : null) : s.Description,
                Buttons = { Button("Book", Data("bs", ("s", s.Id.ToString())), "Book " + s.Name) }
            });
        }
        if (turn.Cat.Categories.Count > 1) rich.QuickReplies.Add(Button("All services", Data("bh")));
        rich.QuickReplies.Add(Button("My bookings", Data("bm")));
        return rich;
    }

    /// <summary>The small row under ordinary replies.</summary>
    private static RichContent? Hint(Turn turn)
    {
        if (!turn.Buttons) return null;
        var rich = new RichContent();
        rich.QuickReplies.Add(Button("Book", Data("bh")));
        rich.QuickReplies.Add(Button("My bookings", Data("bm")));
        rich.QuickReplies.Add(Button("Prices", Data("bpr")));
        return rich;
    }

    private static RichContent NoTimes(Turn turn)
    {
        var rich = new RichContent();
        if (turn.Draft.Preference is not null and not "any") rich.QuickReplies.Add(Button("Anyone available", Data("bp", ("p", "any"))));
        rich.QuickReplies.Add(Button("Other service", Data("bh")));
        rich.QuickReplies.Add(Button("Talk to staff", Data("bst")));
        return rich;
    }

    private static RichContent StartOver()
    {
        var rich = new RichContent();
        rich.QuickReplies.Add(Button("Start over", Data("bh")));
        return rich;
    }

    private static string PriceList(Turn turn)
    {
        var sb = new StringBuilder("Our services:");
        foreach (var group in turn.Cat.Bookable.GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "" : s.Category.Trim()))
        {
            sb.Append('\n');
            if (group.Key.Length > 0) sb.Append('\n').Append(group.Key).Append(':');
            foreach (var s in group) sb.Append("\n- ").Append(s.Name).Append(": ").Append(BookingDesk.ServiceLine(s, turn.Currency));
        }
        if (turn.Client.HomeServiceEnabled && turn.Client.HomeServiceFee is { } fee)
            sb.Append("\n\nHome service: +").Append(Money.Format(fee, turn.Currency));
        return sb.ToString();
    }

    private string Summary(Turn turn, ServiceItem service, DateTime local)
    {
        var d = turn.Draft;
        var fee = d.AtHome == true ? turn.Client.HomeServiceFee : null;
        var price = service.Price is { } p ? Money.Format(p + (fee ?? 0), turn.Currency) : null;
        var who = d.Preference switch
        {
            "staff" when d.StaffId is { } id => turn.Cat.StaffMember(id)?.Name ?? "the person you chose",
            "F" => "a female staff member",
            "M" => "a male staff member",
            _ => "any available staff"
        };
        var sb = new StringBuilder("Please check your booking:\n");
        sb.Append($"{service.Name} ({BookingDesk.Duration(service.DurationMinutes)})");
        if (price is not null) sb.Append($" - {price}" + (fee is > 0 ? " incl. home service" : ""));
        sb.Append($"\n{BookingDesk.Day(DateOnly.FromDateTime(local), turn.Cat.Today)}, {BookingDesk.Time(local, turn.Country)} with {who}");
        sb.Append(d.AtHome == true ? $"\nHome service: {d.Address}" : $"\nAt {turn.Client.ClientName}");
        sb.Append($"\nName: {d.Name}, {d.Phone}");
        return sb.ToString();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<string?> KnownNameAsync(Turn turn, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(turn.Context.CustomerName)) return Cut(turn.Context.CustomerName!.Trim(), 100);
        if (string.IsNullOrWhiteSpace(turn.From) || turn.Context.IsTest) return null;
        try
        {
            return Cut((await _s.Messaging.GetCustomerNameAsync(turn.Client.Id, turn.Channel, turn.From, ct))?.Trim(), 100);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SaveAsync(Turn turn, CancellationToken ct)
    {
        if (turn.Context.IsTest || !turn.Buttons) return;
        turn.Draft.UpdatedUtc = turn.Cat.UtcNow;
        turn.Conversation.FlowJson = turn.Draft.ServiceId is null && turn.Draft.RescheduleId is null ? null : JsonSerializer.Serialize(turn.Draft);
        await _s.Conversations.SaveAsync(turn.Conversation, ct);
    }

    private static ActionRequest Reply(Turn turn, string message, RichContent? rich = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["channel"] = turn.Channel,
            ["to"] = turn.From ?? "",
            ["message"] = message,
            ["replyToken"] = turn.Context.ReplyToken ?? ""
        };
        if (rich is { IsEmpty: false }) parameters[SendMessageAction.RichParameter] = SendMessageAction.WriteRich(rich);
        return ActionRequest.For(ActionType.SendMessage, parameters);
    }

    private static ActionRequest TellStaff(Turn turn, Booking booking, string headline) =>
        ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["kind"] = NotifyInternalAction.BookingKind,
            ["subject"] = $"[{turn.Client.ClientName}] {headline} {booking.Reference}",
            ["message"] = BookingDesk.StaffMessage(booking, turn.Client, headline)
        }, required: false);

    private static ActionRequest StaffAlert(Turn turn, string reason) =>
        ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
        {
            ["message"] = $"[{turn.Client.ClientName}] {Channels.DisplayName(turn.Channel)} customer {turn.From} needs a person ({reason}): \"{turn.Text}\""
        }, required: false);

    private static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Data(string action, params (string Key, string? Value)[] values)
    {
        var sb = new StringBuilder("a=").Append(action);
        foreach (var (key, value) in values)
            if (!string.IsNullOrEmpty(value)) sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadData(string data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in data.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) continue;
            try { result[part[..at]] = Uri.UnescapeDataString(part[(at + 1)..]); }
            catch (UriFormatException) { /* ignore a broken value */ }
        }
        return result;
    }

    private static ChatButton Button(string label, string data, string? displayText = null) =>
        new() { Label = label, Data = data, DisplayText = displayText ?? label };

    private static string? Cut(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
