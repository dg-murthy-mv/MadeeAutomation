using System.Globalization;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Clinic (Step A): patients book a doctor in the chat; emergencies, reminders, waitlist, follow-up, feedback, privacy.
/// World.Clinic = "E2E Smile Clinic", Bengaluru (IN, INR), open 09:00-20:00, start times every 15 min.
///   Dr Priya Sharma  - BDS Dentist, ₹500, Mon-Sat 10-13 + 17-20 - Dental check-up, Root canal consultation
///   Dr Arjun Rao     - MBBS GP, ₹400, every day 9-12           - General consultation (15 min)
///   Dr Meera Iyer    - MD Dermatologist, ₹700, Tue/Thu/Sat 14-18 - Skin consultation (20 min)
/// Second reminder, feedback (0 h after "Completed") and waitlist are switched on.
/// Each test uses its own day / time so the tests don't take each other's times.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ClinicTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private static DateOnly IndiaToday => DateOnly.FromDateTime(DateTime.UtcNow.Add(Ist));

    private static string DayLabel(DateOnly d) =>
        d == IndiaToday ? "Today" : d == IndiaToday.AddDays(1) ? "Tomorrow" : d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>The n-th day (0 = first) at least two days ahead on which Dr Meera works (Tue / Thu / Sat).</summary>
    private static DateOnly MeeraDay(int n)
    {
        var d = IndiaToday.AddDays(2);
        while (true)
        {
            if (d.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday or DayOfWeek.Saturday && n-- == 0) return d;
            d = d.AddDays(1);
        }
    }

    private sealed class Chat
    {
        public string User { get; }
        public LineCall Last { get; private set; } = null!;

        public Chat(string label) => User = Line.NewUser(label);

        private async Task<LineCall> SendAsync(object lineEvent)
        {
            var before = GlobalSetup.Fakes.LineCallsFor(User).Count;
            await Line.SendAsync(World.Clinic, lineEvent);
            var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User)),
                c => c.Count > before, 15000, $"a reply to {User}");
            return Last = calls[^1];
        }

        public Task<LineCall> SayAsync(string text) => SendAsync(Line.TextEvent(User, text));
        public Task<LineCall> TapDataAsync(string data) => SendAsync(Line.TapEvent(User, data));
        public Task<LineCall> TapAsync(string label) => TapDataAsync(Last.DataOf(label));
        public Task<LineCall> TapOnAsync(LineCall message, string label) => TapDataAsync(message.DataOf(label));
        public Task<LineCall> BookCardAsync(string title) => TapDataAsync(Last.Cards.First(c => c.Title == title).ButtonData[0]);

        public async Task PartAsync(string part)
        {
            var label = Last.Buttons.FirstOrDefault(b => b.StartsWith(part));
            if (label is not null) await TapAsync(label);
        }

        /// <summary>Waits for a message pushed to this patient later (reminder, offer, follow-up ...).</summary>
        public Task<LineCall> PushedAsync(Func<LineCall, bool> match, string what) =>
            Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User).Where(match).ToList()), l => l.Count > 0, 90000, what)
                .ContinueWith(t => t.Result[0]);

        public Task<List<Dictionary<string, object?>>> BookingsAsync() =>
            Db.RowsAsync("SELECT * FROM dbo.Bookings WHERE CustomerId = @c ORDER BY CreatedAtUtc", ("@c", User));

        /// <summary>From the name question on: name, mobile, age, visit type, reason, then Request appointment.</summary>
        public async Task FinishAsync(string name, string age = "34", string visit = "First visit", string reason = "skin check")
        {
            await SayAsync(name);
            await SayAsync("98450 12345");
            await SayAsync(age);
            await TapAsync(visit);
            await SayAsync(reason);
            Assert.That(Last.Text, Does.StartWith("Please check your appointment"));
            await TapAsync("Request appointment");
            Assert.That(Last.Text, Does.Contain("Your appointment request BK-"));
        }
    }

    private static List<LineCall> StaffMessages() => GlobalSetup.Fakes.LineCallsFor(World.ClinicStaffLineId).ToList();

    private async Task SignInAsync(string email, string password)
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, email, password);
    }

    private static Task MakeMessagesDueAsync(object bookingId) =>
        Db.ExecAsync("UPDATE dbo.BookingMessages SET DueUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE BookingId = @b AND SentAtUtc IS NULL", ("@b", bookingId));

    [Test]
    public async Task T110_Patient_books_a_doctor_inside_their_sessions_with_age_visit_and_reason()
    {
        var chat = new Chat("t110");
        await chat.SayAsync("hello");
        Assert.That(chat.Last.Buttons, Is.SupersetOf(new[] { "Dental", "General", "Skin", "Our doctors", "My appointments", "Talk to staff" }));

        await chat.TapAsync("Our doctors");
        var meera = chat.Last.Cards.First(c => c.Title == "Dr Meera Iyer");
        Assert.That(chat.Last.Body, Does.Contain("MD - Dermatologist"));
        Assert.That(meera.Price, Does.Contain("Tue, Thu, Sat 2:00 PM-6:00 PM").And.Contain("Speaks English, Tamil"));

        await chat.BookCardAsync("Dr Meera Iyer");
        Assert.That(chat.Last.Text, Is.EqualTo("Which day?"));
        var days = chat.Last.Buttons.Where(b => b != "Other day").ToList();
        foreach (var label in days.Where(l => l is not ("Today" or "Tomorrow")))
            Assert.That(label[..3], Is.AnyOf("Tue", "Thu", "Sat"), "only Dr Meera's days");

        var day = MeeraDay(0);
        await chat.TapAsync(DayLabel(day));
        await chat.PartAsync("Afternoon");
        Assert.That(chat.Last.Buttons, Does.Contain("2:00 PM").And.Not.Contain("1:45 PM"));
        await chat.TapAsync("2:00 PM");

        await chat.SayAsync("Anita Desai");
        await chat.SayAsync("98450 12345");
        Assert.That(chat.Last.Text, Does.Contain("patient's age"));
        await chat.SayAsync("abc");
        Assert.That(chat.Last.Text, Does.Contain("as a number"));
        await chat.SayAsync("34 years");
        Assert.That(chat.Last.Buttons.Take(2), Is.EqualTo(new[] { "First visit", "Follow-up visit" }));
        await chat.TapAsync("First visit");
        Assert.That(chat.Last.Text, Does.Contain("medical history"));
        await chat.SayAsync("itchy rash on my arm");
        Assert.That(chat.Last.Text, Does.Contain("₹700.00").And.Contain("Age: 34").And.Contain("Reason: itchy rash on my arm"));
        await chat.TapAsync("Request appointment");
        Assert.That(chat.Last.Text, Does.Contain("Your appointment request BK-").And.Contain("with Dr Meera Iyer").And.Contain("Fee: ₹700.00"));

        var b = (await chat.BookingsAsync()).Single();
        Assert.That(b["Total"], Is.EqualTo(700m));
        Assert.That(b["PatientAge"], Is.EqualTo(34));
        Assert.That(b["VisitType"], Is.EqualTo("first"));
        Assert.That(b["CustomerNote"], Is.EqualTo("itchy rash on my arm"));
        var reference = (string)b["Reference"]!;
        var staff = await Eventually.GetAsync(() => Task.FromResult(StaffMessages().Where(m => m.Text.Contains(reference)).ToList()), m => m.Count > 0, 15000, "staff message");
        Assert.That(staff[0].Text, Does.Contain("Doctor: Dr Meera Iyer").And.Contain("Patient: Anita Desai").And.Contain("Age: 34").And.Contain("Reason: itchy rash"));
    }

    [Test]
    public async Task T111_Emergency_words_get_the_emergency_number_and_medical_questions_go_to_the_team()
    {
        var chat = new Chat("t111");
        var before = StaffMessages().Count;
        await chat.SayAsync("my father has chest pain and is sweating");
        Assert.That(chat.Last.Text, Does.Contain("call 112 (ambulance 108)"));
        await Eventually.GetAsync(() => Task.FromResult(StaffMessages().Skip(before).Where(m => m.Text.Contains("URGENT")).ToList()), m => m.Count > 0, 15000, "urgent staff alert");

        await chat.SayAsync("मेरे पिता बेहोश हो गए");
        Assert.That(chat.Last.Text, Does.Contain("call 112"));
        await chat.SayAsync("my mother collapsed in the bathroom");   // only the AI catches this one
        Assert.That(chat.Last.Text, Does.Contain("call 112"));
        await chat.SayAsync("do you take emergency patients?");
        Assert.That(chat.Last.Text, Does.Not.Contain("call 112"));
        await chat.SayAsync("which medicine is good for fever?");
        Assert.That(chat.Last.Text, Does.Contain("clinic team will get back"));
    }

    [Test]
    public async Task T112_Typed_requests_find_the_doctor_and_the_time()
    {
        var gp = new Chat("t112a");
        await gp.SayAsync("book general consultation tomorrow 10am");
        Assert.That(gp.Last.Text, Does.Contain("Tomorrow at 10:00 AM is free!").And.Contain("What name"));

        var dental = new Chat("t112b");
        await dental.SayAsync("kal dr sharma ka appointment chahiye");
        Assert.That(dental.Last.Text, Does.Contain("visit with Dr Priya Sharma"));
        Assert.That(dental.Last.Cards.Select(c => c.Title), Is.EqualTo(new[] { "Dental check-up", "Root canal consultation" }));
        Assert.That(dental.Last.Cards[0].Price, Does.EndWith("₹500.00"), "the doctor's fee, not the service price");
    }

    [Test]
    public async Task T113_Second_reminder_feedback_and_follow_up()
    {
        var chat = new Chat("t113");
        await chat.SayAsync("hello");
        await chat.TapAsync("Our doctors");
        await chat.BookCardAsync("Dr Meera Iyer");
        await chat.TapAsync(DayLabel(MeeraDay(1)));
        await chat.PartAsync("Afternoon");
        await chat.TapAsync("2:30 PM");
        await chat.FinishAsync("Ravi Kumar", age: "52", reason: "mole check");
        var b = (await chat.BookingsAsync()).Single();
        var reference = (string)b["Reference"]!;

        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}");
        var card = Page.Locator($"[data-booking-ref='{reference}']");
        await Expect(card).ToContainTextAsync("First visit");
        await Expect(card).ToContainTextAsync("Reason: mole check");
        await card.Locator("[data-testid=booking-confirm]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToBeVisibleAsync();

        // 2-hour reminder: planned on confirm, sent when due.
        var planned = await Db.RowsAsync("SELECT * FROM dbo.BookingMessages WHERE BookingId = @b", ("@b", b["Id"]));
        Assert.That(planned.Select(m => m["Kind"]), Does.Contain("reminder2"));
        await MakeMessagesDueAsync(b["Id"]!);
        var second = await chat.PushedAsync(c => c.Text.Contains("in about 2 hours"), "the 2-hour reminder");
        Assert.That(second.Buttons, Is.EqualTo(new[] { "I'll be there", "Reschedule", "Cancel" }));

        // Completed -> feedback question; a low rating alerts the staff and asks what to improve.
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}&show=upcoming");
        await Page.Locator($"[data-booking-ref='{reference}'] [data-testid=booking-complete]").ClickAsync();
        var feedback = await chat.PushedAsync(c => c.Text.StartsWith("Thank you for visiting"), "the feedback question");
        Assert.That(feedback.Buttons, Is.EqualTo(new[] { "5 - Excellent", "4 - Good", "3 - OK", "2 - Poor", "1 - Bad" }));
        var before = StaffMessages().Count;
        await chat.TapOnAsync(feedback, "2 - Poor");
        Assert.That(chat.Last.Text, Does.Contain("what could we do better"));
        await Eventually.GetAsync(() => Task.FromResult(StaffMessages().Skip(before).Where(m => m.Text.Contains("gave 2 star(s)")).ToList()), m => m.Count > 0, 15000, "low rating alert");
        await chat.SayAsync("Long wait at reception");
        var row = (await chat.BookingsAsync()).Single();
        Assert.That(row["FeedbackRating"], Is.EqualTo(2));
        Assert.That(row["FeedbackComment"], Is.EqualTo("Long wait at reception"));

        // Follow-up in 14 days with the doctor's words, sent as written.
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}&show=past");
        card = Page.Locator($"[data-booking-ref='{reference}']");
        await Expect(card).ToContainTextAsync("★★☆☆☆");
        await card.Locator("[data-testid=followup-days]").FillAsync("14");
        await card.Locator("[data-testid=followup-note]").FillAsync("Please bring your old prescriptions.");
        await card.Locator("[data-testid=followup-save]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync($"Follow-up for {reference} set for around");
        await MakeMessagesDueAsync(b["Id"]!);
        var followUp = await chat.PushedAsync(c => c.Text.Contains("would like to see you again"), "the follow-up message");
        Assert.That(followUp.Text, Does.Contain("From Dr Meera Iyer: Please bring your old prescriptions."));
        Assert.That(followUp.Buttons, Is.EqualTo(new[] { "Book follow-up", "Not now" }));

        await chat.TapOnAsync(followUp, "Book follow-up");
        Assert.That(chat.Last.Text, Does.Contain($"Follow-up for {reference}"));
        await chat.TapAsync(DayLabel(MeeraDay(2)));
        await chat.PartAsync("Afternoon");
        await chat.TapAsync(chat.Last.Buttons.First(l => Regex.IsMatch(l, @"^\d{1,2}:\d{2} (AM|PM)$")));
        Assert.That(chat.Last.Text, Does.Contain("Follow-up visit").And.Contain("Age: 52").And.Contain("Ravi Kumar"), "nothing asked again");
        await chat.TapAsync("Request appointment");
        var next = (await chat.BookingsAsync()).Last();
        Assert.That(next["VisitType"], Is.EqualTo("follow"));

        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}&show=followups");
        await Expect(Page.Locator($"[data-testid=followup-row][data-booking-ref='{reference}'] [data-testid=followup-booked]"))
            .ToContainTextAsync((string)next["Reference"]!);

        // Report: rating and comment.
        await Page.GotoAsync($"/Reports?clientConfigId={World.Clinic.Id}&period=custom&from={IndiaToday.AddDays(-1):yyyy-MM-dd}&to={IndiaToday.AddDays(30):yyyy-MM-dd}");
        await Expect(Page.Locator("[data-testid=staff-table] h6")).ToHaveTextAsync("Doctors");
        await Expect(Page.Locator("[data-testid=feedback-comments]")).ToContainTextAsync("Long wait at reception");
    }

    [Test]
    public async Task T114_Waitlist_offer_when_a_time_opens_up()
    {
        var tomorrow = IndiaToday.AddDays(1);
        DateTime Utc(TimeOnly t) => tomorrow.ToDateTime(t) - Ist;
        var arjun = await Db.ScalarAsync<Guid>("SELECT Id FROM dbo.StaffMembers WHERE ClientConfigId = @c AND Name = 'Dr Arjun Rao'", ("@c", World.Clinic.Id));
        var blockId = Guid.NewGuid();
        await Db.ExecAsync(@"
            INSERT INTO dbo.Bookings (Id, ClientConfigId, BookingNumber, Reference, Source, Channel, CustomerId, CustomerName, ServiceName, DurationMinutes,
                Currency, Price, Total, StaffMemberId, StaffName, StaffPreference, StartUtc, EndUtc, BusyFromUtc, BusyUntilUtc, LocalDate, AtHome, Status, CreatedAtUtc)
            VALUES (@id, @c, 9114, 'BLOCK-9114', 'staff', '', NULL, 'Blocked', 'Block', 1439, 'INR', 0, 0, @m, 'Dr Arjun Rao', 'staff', @s, @e, @s, @e, @d, 0, 2, SYSUTCDATETIME());",
            ("@id", blockId), ("@c", World.Clinic.Id), ("@m", arjun), ("@s", Utc(TimeOnly.MinValue)), ("@e", Utc(new TimeOnly(23, 59))), ("@d", tomorrow.ToDateTime(TimeOnly.MinValue)));

        var chat = new Chat("t114");
        await chat.SayAsync("book general consultation tomorrow");
        Assert.That(chat.Last.Text, Does.Contain("Tomorrow is fully booked"));
        await chat.TapAsync("Waitlist Tomorrow");
        Assert.That(chat.Last.Text, Does.Contain("You're on the waitlist for General consultation"));
        var entry = (await Db.RowsAsync("SELECT * FROM dbo.WaitlistEntries WHERE CustomerId = @u", ("@u", chat.User))).Single();
        Assert.That(entry["Status"], Is.EqualTo(1));

        await Db.ExecAsync("UPDATE dbo.Bookings SET Status = 4 WHERE Id = @id", ("@id", blockId));
        var offer = await chat.PushedAsync(c => c.Text.StartsWith("Good news!"), "the waitlist offer");
        Assert.That(offer.Buttons, Does.Contain("9:15 AM").And.Contain("Leave waitlist"));
        await chat.TapOnAsync(offer, "9:15 AM");
        Assert.That(chat.Last.Text, Does.Contain("What name"));
        await chat.FinishAsync("Suresh N", reason: "fever");
        Assert.That((await Db.RowsAsync("SELECT Status FROM dbo.WaitlistEntries WHERE Id = @id", ("@id", entry["Id"])))[0]["Status"], Is.EqualTo(3));

        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}&show=waitlist");
        await Expect(Page.Locator("[data-testid=waitlist-row][data-status=Booked]").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task T115_Doctors_sessions_settings_and_the_clinic_portal()
    {
        await SignInAsync(World.ClinicPortalEmail, World.PortalPassword);
        await Expect(Page.Locator("[data-testid=nav-staff]")).ToHaveTextAsync("Doctors");
        await Page.GotoAsync("/Staff");
        var meera = Page.Locator("[data-staff-name='Dr Meera Iyer']");
        await Expect(meera).ToContainTextAsync("Fee ₹700.00");
        await Expect(meera).ToContainTextAsync("Tue, Thu, Sat 2:00 PM-6:00 PM");
        await Expect(Page.Locator("[data-staff-name='Dr Priya Sharma']")).ToContainTextAsync("Mon-Sat 10:00 AM-1:00 PM & 5:00 PM-8:00 PM");

        // A new doctor with a Sunday session; a half-filled session is refused first.
        await Page.GotoAsync("/Staff/Create");
        await Page.FillAsync("[data-testid=staff-name]", "Dr Kiran Shetty");
        await Page.FillAsync("[data-testid=staff-title]", "BPT - Physiotherapist");
        await Page.FillAsync("[data-testid=staff-fee]", "600");
        await Page.FillAsync("[data-testid=session-start-0-0]", "08:00");
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-testid=staff-sessions-error]")).ToContainTextAsync("Sun: give both a start and an end time.");
        await Page.FillAsync("[data-testid=session-end-0-0]", "11:00");
        await Page.ClickAsync("[data-testid=save-staff]");
        var kiran = Page.Locator("[data-staff-name='Dr Kiran Shetty']");
        await Expect(kiran).ToContainTextAsync("Sun 8:00 AM-11:00 AM");
        await Expect(kiran).ToContainTextAsync("Fee ₹600.00");
        var sessions = await Db.RowsAsync("SELECT s.* FROM dbo.StaffSessions s JOIN dbo.StaffMembers m ON m.Id = s.StaffMemberId WHERE m.Name = 'Dr Kiran Shetty'");
        Assert.That(sessions.Single()["DayOfWeek"], Is.EqualTo(0));

        // WhatsApp template names: Meta's format only.
        await Page.GotoAsync("/Bookings/Settings");
        await Page.FillAsync("[data-testid=set-tpl-reminder]", "Appointment Reminder");
        await Page.ClickAsync("[data-testid=save-settings]");
        await Expect(Page.Locator("body")).ToContainTextAsync("small letters, numbers and _");
        await Page.FillAsync("[data-testid=set-tpl-reminder]", "appointment_reminder");
        await Page.ClickAsync("[data-testid=save-settings]");
        await Page.GotoAsync("/Bookings/Settings");
        await Expect(Page.Locator("[data-testid=set-tpl-reminder]")).ToHaveValueAsync("appointment_reminder");
        await Expect(Page.Locator("[data-testid=set-reminder2]")).ToBeCheckedAsync();
        Assert.That(await Db.ScalarAsync<string>("SELECT WhatsAppTemplateReminder FROM dbo.ClientConfigs WHERE Id = @c", ("@c", World.Clinic.Id)),
            Is.EqualTo("appointment_reminder"));
        await Page.FillAsync("[data-testid=set-tpl-reminder]", "");
        await Page.ClickAsync("[data-testid=save-settings]");

        // Another client's data is never shown.
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Spa.Id}");
        await Expect(Page.Locator("body")).ToContainTextAsync("E2E Smile Clinic");
    }

    [Test]
    public async Task T116_Erase_personal_details()
    {
        var chat = new Chat("t116");
        await chat.SayAsync("hello");
        await chat.TapAsync("Our doctors");
        await chat.BookCardAsync("Dr Meera Iyer");
        await chat.TapAsync(DayLabel(MeeraDay(0)));
        await chat.PartAsync("Afternoon");
        await chat.TapAsync("3:00 PM");
        await chat.FinishAsync("Lakshmi P", age: "61", reason: "hair loss");
        var b = (await chat.BookingsAsync()).Single();

        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}");
        var card = Page.Locator($"[data-booking-ref='{b["Reference"]}']");
        await Expect(card.Locator("[data-testid=booking-erase]")).ToHaveCountAsync(0);   // still open: cancel or decline first
        await card.Locator("[data-testid=booking-reason]").FillAsync("Doctor on leave");
        await card.Locator("[data-testid=booking-decline]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("declined");
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Clinic.Id}&show=all");
        await Page.Locator($"[data-booking-ref='{b["Reference"]}'] [data-testid=booking-erase]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Personal details erased");

        var row = (await Db.RowsAsync("SELECT * FROM dbo.Bookings WHERE Id = @id", ("@id", b["Id"]))).Single();
        Assert.That(row["CustomerName"], Is.EqualTo("(erased)"));
        Assert.That(row["CustomerPhone"], Is.Null);
        Assert.That(row["CustomerId"], Is.Null);
        Assert.That(row["PatientAge"], Is.Null);
        Assert.That(row["CustomerNote"], Is.Null);
        Assert.That(await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Conversations WHERE CustomerId = @u", ("@u", chat.User)), Is.EqualTo(0));
    }
}
