using System.Globalization;
using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Beauty &amp; wellness (Step A): customers book a service with a person at a free time, in the chat.
/// World.Spa = "E2E Glow Spa", Manila, open 10:00-21:00 every day, home service +₱300 / 30 min travel.
///   Swedish massage 60 min ₱800 (+15 min clean-up, home service) - Joy (F, travels), Mark (M)
///   Foot spa 45 min ₱500 - Joy, Mark      Gel manicure 60 min ₱650 - Ana      Haircut (women) 45 min ₱400 (+15) - Liza
/// Each test uses its own service / day so the tests don't take each other's times.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BeautyBookingTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    private static DateOnly ManilaToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>The button text the bot uses for a day.</summary>
    private static string DayLabel(DateOnly d) =>
        d == ManilaToday ? "Today" : d == ManilaToday.AddDays(1) ? "Tomorrow" : d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private sealed class Chat
    {
        public string User { get; }
        public LineCall Last { get; private set; } = null!;

        public Chat(string label) => User = Line.NewUser(label);

        private async Task<LineCall> SendAsync(object lineEvent)
        {
            var before = GlobalSetup.Fakes.LineCallsFor(User).Count;
            await Line.SendAsync(World.Spa, lineEvent);
            var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User)),
                c => c.Count > before, 15000, $"a reply to {User}");
            return Last = calls[^1];
        }

        public Task<LineCall> SayAsync(string text) => SendAsync(Line.TextEvent(User, text));
        public Task<LineCall> TapDataAsync(string data) => SendAsync(Line.TapEvent(User, data));
        public Task<LineCall> TapAsync(string label) => TapDataAsync(Last.DataOf(label));

        /// <summary>"Book" on a service card.</summary>
        public Task<LineCall> BookAsync(string service) => TapDataAsync(Last.Cards.First(c => c.Title == service).ButtonData[0]);

        /// <summary>When the day has many free times, the bot first asks morning / afternoon / evening.</summary>
        public async Task PartAsync(string part)
        {
            var label = Last.Buttons.FirstOrDefault(b => b.StartsWith(part));
            if (label is not null) await TapAsync(label);
        }

        public Task<List<Dictionary<string, object?>>> BookingsAsync() =>
            Db.RowsAsync("SELECT * FROM dbo.Bookings WHERE CustomerId = @c ORDER BY CreatedAtUtc", ("@c", User));

        /// <summary>Name + mobile + Request booking, from the name question on.</summary>
        public async Task FinishAsync(string name, string phone = "0917 123 4567")
        {
            await SayAsync(name);
            await SayAsync(phone);
            Assert.That(Last.Text, Does.StartWith("Please check your booking"));
            await TapAsync("Request booking");
        }
    }

    private static List<LineCall> StaffMessages(string reference) =>
        GlobalSetup.Fakes.LineCallsFor(World.SpaStaffLineId).Where(c => c.Text.Contains(reference)).ToList();

    [Test]
    public async Task T98_Customer_books_with_buttons_and_the_persons_time_is_then_taken()
    {
        var chat = new Chat("t98");
        await chat.SayAsync("hello");
        Assert.That(chat.Last.Buttons, Is.SupersetOf(new[] { "Hair", "Massage", "Nails", "My bookings" }));

        await chat.TapAsync("Massage");
        // (T104 may already have added "Hot stone massage" - tests run in name order.)
        Assert.That(chat.Last.Cards.Select(c => c.Title), Is.SupersetOf(new[] { "Swedish massage", "Foot spa" }));
        // The raw body is JSON with ₱ written as \u20B1, so read the card's price line instead.
        Assert.That(chat.Last.Cards.First(c => c.Title == "Swedish massage").Price, Is.EqualTo("1 hr · ₱800.00"));

        await chat.BookAsync("Swedish massage");
        Assert.That(chat.Last.Text, Does.Contain("home service (+₱300.00)?"));
        await chat.TapAsync("At the spa");
        Assert.That(chat.Last.Buttons, Is.SupersetOf(new[] { "Anyone available", "Female", "Male", "Choose a person" }));
        await chat.TapAsync("Female");
        Assert.That(chat.Last.Text, Is.EqualTo("Which day?"));
        await chat.TapAsync("Tomorrow");
        await chat.PartAsync("Afternoon");
        Assert.That(chat.Last.Buttons, Does.Contain("2:00 PM"));
        await chat.TapAsync("2:00 PM");
        Assert.That(chat.Last.Text, Does.Contain("What name should we put the booking under?"));

        await chat.SayAsync("Maria Cruz");
        await chat.SayAsync("abc");
        Assert.That(chat.Last.Text, Does.Contain("doesn't look like a mobile number"));
        await chat.SayAsync("0917 123 4567");
        Assert.That(chat.Last.Text, Does.Contain("Swedish massage (1 hr) - ₱800.00").And.Contain("Tomorrow, 2:00 PM with a female staff member"));
        await chat.TapAsync("Request booking");
        Assert.That(chat.Last.Text, Does.Contain("Your booking request BK-").And.Contain("with Joy"));

        var booking = (await chat.BookingsAsync()).Single();
        Assert.That(booking["Status"], Is.EqualTo(1), "Pending - waiting for staff");
        Assert.That(booking["StaffName"], Is.EqualTo("Joy"));
        Assert.That(booking["CustomerPhone"], Is.EqualTo("0917 123 4567"));
        Assert.That(booking["Total"], Is.EqualTo(800m));
        var reference = (string)booking["Reference"]!;
        var staff = await Eventually.GetAsync(() => Task.FromResult(StaffMessages(reference)), m => m.Count > 0, 15000, "staff LINE message");
        Assert.That(staff[0].Text, Does.Contain("New booking").And.Contain("Joy").And.Contain("Maria Cruz, 0917 123 4567"));
        Assert.That(await Mailbox.WaitForAsync("owner@glowspa.test"), Is.Not.Empty);

        // Someone else choosing Joy: her booked hour (+15 min clean-up) is not offered.
        var other = new Chat("t98b");
        await other.SayAsync("hello");
        await other.TapAsync("Massage");
        await other.BookAsync("Swedish massage");
        await other.TapAsync("At the spa");
        await other.TapAsync("Choose a person");
        await other.TapAsync("Joy");
        await other.TapAsync("Tomorrow");
        await other.PartAsync("Afternoon");
        Assert.That(other.Last.Buttons, Does.Not.Contain("1:00 PM").And.Not.Contain("2:00 PM").And.Not.Contain("3:00 PM"));
        Assert.That(other.Last.Buttons, Does.Contain("12:30 PM").And.Contain("3:30 PM"));
    }

    [Test]
    public async Task T99_Typed_Taglish_request_goes_straight_to_the_free_time()
    {
        var chat = new Chat("t99");
        await chat.SayAsync("Pwede po foot spa bukas 3pm?");
        Assert.That(chat.Last.Text, Does.StartWith("Sige po!").And.Contain("Who would you like?"));
        await chat.TapAsync("Anyone available");
        Assert.That(chat.Last.Text, Does.Contain("Tomorrow at 3:00 PM is free!").And.Contain("What name"));
        await chat.FinishAsync("Ana Reyes", "09181234567");
        var booking = (await chat.BookingsAsync()).Single();
        Assert.That(booking["ServiceName"], Is.EqualTo("Foot spa"));
        Assert.That(booking["Language"], Is.EqualTo("tl"));

        // A time we are closed: told, and shown what is free.
        var late = new Chat("t99b");
        await late.SayAsync("book foot spa tomorrow 9pm");
        await late.TapAsync("Anyone available");
        Assert.That(late.Last.Text, Does.Contain("Tomorrow at 9:00 PM is not free."));
        Assert.That(late.Last.Buttons.Any(b => b.StartsWith("Morning")), Is.True);
    }

    [Test]
    public async Task T100_Home_service_asks_the_address_adds_the_fee_and_blocks_travel_time()
    {
        var day = ManilaToday.AddDays(2);
        var chat = new Chat("t100");
        await chat.SayAsync("hello");
        await chat.TapAsync("Massage");
        await chat.BookAsync("Swedish massage");
        await chat.TapAsync("Home service");
        Assert.That(chat.Last.Text, Is.EqualTo("Which day?"), "only Joy travels - no 'who' question");
        await chat.TapAsync(DayLabel(day));
        await chat.PartAsync("Morning");
        await chat.TapAsync("11:00 AM");
        await chat.SayAsync("Lito Santos");
        await chat.SayAsync("0920 111 2222");
        Assert.That(chat.Last.Text, Does.Contain("full address"));
        await chat.SayAsync("Unit 5, 88 Ayala Ave, Brgy San Lorenzo, Makati");
        Assert.That(chat.Last.Text, Does.Contain("₱1,100.00 incl. home service").And.Contain("Home service: Unit 5, 88 Ayala Ave"));
        await chat.TapAsync("Request booking");

        var b = (await chat.BookingsAsync()).Single();
        Assert.That(b["AtHome"], Is.EqualTo(true));
        Assert.That(b["Total"], Is.EqualTo(1100m));
        Assert.That(b["HomeServiceFee"], Is.EqualTo(300m));
        var start = (DateTime)b["StartUtc"]!;
        Assert.That(start - (DateTime)b["BusyFromUtc"]!, Is.EqualTo(TimeSpan.FromMinutes(30)), "travel before");
        Assert.That((DateTime)b["BusyUntilUtc"]! - start, Is.EqualTo(TimeSpan.FromMinutes(60 + 15 + 30)), "service + clean-up + travel back");
    }

    [Test]
    public async Task T101_Two_customers_for_the_same_slot_only_one_gets_it()
    {
        var day = ManilaToday.AddDays(1);
        var a = new Chat("t101a");
        var b = new Chat("t101b");
        foreach (var chat in new[] { a, b })
        {
            await chat.SayAsync("hello");
            await chat.TapAsync("Nails");
            await chat.BookAsync("Gel manicure");   // only Ana does it: no "who" question
            await chat.TapAsync(DayLabel(day));
            await chat.PartAsync("Afternoon");
            await chat.TapAsync("4:00 PM");
            await chat.SayAsync("Racer " + chat.User[^3..]);
            await chat.SayAsync("09170000000");
        }
        await a.TapAsync("Request booking");
        await b.TapAsync("Request booking");
        Assert.That(a.Last.Text, Does.Contain("Your booking request BK-"));
        Assert.That(b.Last.Text, Does.StartWith("Sorry, that time was just taken."));
        Assert.That(b.Last.Buttons, Does.Not.Contain("4:00 PM"));
        Assert.That(await b.BookingsAsync(), Is.Empty);
    }

    [Test]
    public async Task T102_Customer_cancels_and_reschedules_from_My_bookings()
    {
        var day = ManilaToday.AddDays(3);
        var chat = new Chat("t102");
        async Task BookHaircutAsync(string time)
        {
            await chat.SayAsync("hello");
            await chat.TapAsync("Hair");
            await chat.BookAsync("Haircut (women)");
            await chat.TapAsync(DayLabel(day));
            await chat.PartAsync("Morning");
            await chat.TapAsync(time);
            await chat.FinishAsync("Carla");
        }
        await BookHaircutAsync("10:00 AM");
        await BookHaircutAsync("11:00 AM");
        var first = (await chat.BookingsAsync())[0];
        var second = (await chat.BookingsAsync())[1];

        await chat.TapDataAsync("a=bm");
        Assert.That(chat.Last.Cards, Has.Count.EqualTo(2));
        await chat.TapDataAsync(chat.Last.Cards[0].ButtonData.First(d => d.StartsWith("a=bx")));
        Assert.That(chat.Last.Text, Does.StartWith($"Cancel {first["Reference"]}"));
        await chat.TapAsync("Yes, cancel it");
        Assert.That(chat.Last.Text, Does.Contain($"{first["Reference"]} is cancelled"));
        Assert.That((await chat.BookingsAsync())[0]["Status"], Is.EqualTo(4));
        await Eventually.GetAsync(() => Task.FromResult(StaffMessages((string)first["Reference"]!)),
            m => m.Any(x => x.Text.Contains("CANCELLED by the customer")), 15000, "staff told about the cancel");

        // Move the second one to 1:00 PM the same day.
        await chat.TapDataAsync("a=bm");
        await chat.TapDataAsync(chat.Last.Cards[0].ButtonData.First(d => d.StartsWith("a=br")));
        Assert.That(chat.Last.Text, Does.StartWith($"Moving {second["Reference"]}"));
        await chat.TapAsync(DayLabel(day));
        await chat.PartAsync("Afternoon");
        await chat.TapAsync("1:00 PM");
        Assert.That(chat.Last.Text, Does.Contain("Move your Haircut (women) to"));
        await chat.TapAsync("Yes, move it");
        Assert.That(chat.Last.Text, Does.Contain($"{second["Reference"]} is moved to"));
        var moved = (await chat.BookingsAsync())[1];
        Assert.That(moved["Reference"], Is.EqualTo(second["Reference"]));
        Assert.That(moved["Status"], Is.EqualTo(1), "back to Pending for staff to confirm");
        Assert.That(((DateTime)moved["StartUtc"]!).AddHours(8).Hour, Is.EqualTo(13));
    }

    private async Task SignInAsync(string email, string password)
    {
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await AdminSession.SignInAsync(Page, email, password);
    }

    [Test]
    public async Task T103_Staff_confirm_and_decline_the_customer_is_told_and_reminded()
    {
        var day = ManilaToday.AddDays(4);
        async Task<Chat> BookAsync(string label, string time)
        {
            var chat = new Chat(label);
            await chat.SayAsync("hello");
            await chat.TapAsync("Massage");
            await chat.BookAsync("Foot spa");
            await chat.TapAsync("Male");
            await chat.TapAsync(DayLabel(day));
            await chat.PartAsync("Morning");
            await chat.TapAsync(time);
            await chat.FinishAsync("Customer " + label);
            return chat;
        }
        var yes = await BookAsync("t103a", "10:00 AM");
        var no = await BookAsync("t103b", "11:00 AM");
        var yesRef = (string)(await yes.BookingsAsync())[0]["Reference"]!;
        var noRef = (string)(await no.BookingsAsync())[0]["Reference"]!;

        await SignInAsync(World.AdminEmail, World.AdminPassword);
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Spa.Id}");
        var card = Page.Locator($"[data-booking-ref='{yesRef}']");
        await Expect(card).ToContainTextAsync("Mark");
        await card.Locator("[data-testid=booking-confirm]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("The customer was told on LINE");

        var told = GlobalSetup.Fakes.LineCallsFor(yes.User).Last();
        Assert.That(told.Text, Does.StartWith($"Your booking {yesRef} is confirmed").And.Contain("with Mark"));
        var row = (await yes.BookingsAsync())[0];
        Assert.That(row["Status"], Is.EqualTo(2));
        Assert.That(row["ReminderDueUtc"], Is.Not.Null);

        // The reminder: make it due now, the worker sends it with buttons.
        await Db.ExecAsync("UPDATE dbo.Bookings SET ReminderDueUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE Reference = @r AND ClientConfigId = @c",
            ("@r", yesRef), ("@c", World.Spa.Id));
        var reminder = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(yes.User).Where(c => c.Text.StartsWith("Reminder:")).ToList()),
            r => r.Count > 0, 90000, "the reminder");
        Assert.That(reminder[0].Buttons, Is.EqualTo(new[] { "I'll be there", "Reschedule", "Cancel" }));
        Assert.That((await yes.BookingsAsync())[0]["ReminderNote"], Is.EqualTo("sent"));
        await yes.TapDataAsync(reminder[0].DataOf("I'll be there"));
        Assert.That(yes.Last.Text, Does.StartWith("Thank you! See you on"));
        Assert.That((await yes.BookingsAsync())[0]["CustomerConfirmedAtUtc"], Is.Not.Null);

        // Decline with a reason.
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Spa.Id}");
        var noCard = Page.Locator($"[data-booking-ref='{noRef}']");
        await noCard.Locator("[data-testid=booking-reason]").FillAsync("Therapist is sick");
        await noCard.Locator("[data-testid=booking-decline]").ClickAsync();
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("declined");
        Assert.That(GlobalSetup.Fakes.LineCallsFor(no.User).Last().Text, Does.Contain("can't take your booking").And.Contain("Reason: Therapist is sick"));
        Assert.That((await no.BookingsAsync())[0]["Status"], Is.EqualTo(5));
    }

    [Test]
    public async Task T104_Walk_ins_services_staff_and_settings()
    {
        var day = ManilaToday.AddDays(5);
        await SignInAsync(World.AdminEmail, World.AdminPassword);

        // Walk-in for Ana at 11:00: saved Confirmed; a second one at 11:30 is refused; the bot no longer offers it.
        await Page.GotoAsync($"/Bookings/Add?clientConfigId={World.Spa.Id}");
        await Page.SelectOptionAsync("[data-testid=add-service]", new SelectOptionValue { Label = "Gel manicure (1 hr · ₱650.00)" });
        await Page.SelectOptionAsync("[data-testid=add-staff-pick]", new SelectOptionValue { Label = "Ana" });
        await Page.FillAsync("[data-testid=add-date]", day.ToString("yyyy-MM-dd"));
        await Page.FillAsync("[data-testid=add-time]", "11:00");
        await Page.FillAsync("[data-testid=add-customer]", "Walk-in Rosa");
        await Page.ClickAsync("[data-testid=save-booking]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Gel manicure");
        var walkIn = (await Db.RowsAsync("SELECT * FROM dbo.Bookings WHERE CustomerName = 'Walk-in Rosa'")).Single();
        Assert.That(walkIn["Status"], Is.EqualTo(2));
        Assert.That(walkIn["Source"], Is.EqualTo("staff"));

        await Page.GotoAsync($"/Bookings/Add?clientConfigId={World.Spa.Id}");
        await Page.SelectOptionAsync("[data-testid=add-service]", new SelectOptionValue { Label = "Gel manicure (1 hr · ₱650.00)" });
        await Page.SelectOptionAsync("[data-testid=add-staff-pick]", new SelectOptionValue { Label = "Ana" });
        await Page.FillAsync("[data-testid=add-date]", day.ToString("yyyy-MM-dd"));
        await Page.FillAsync("[data-testid=add-time]", "11:30");
        await Page.FillAsync("[data-testid=add-customer]", "Double");
        await Page.ClickAsync("[data-testid=save-booking]");
        await Expect(Page.Locator("[data-testid=add-time-error]")).ToContainTextAsync("already has a booking");

        var nails = new Chat("t104");
        await nails.SayAsync("hello");
        await nails.TapAsync("Nails");
        await nails.BookAsync("Gel manicure");
        await nails.TapAsync(DayLabel(day));
        await nails.PartAsync("Morning");
        Assert.That(nails.Last.Buttons, Does.Contain("10:00 AM").And.Not.Contain("10:30 AM").And.Not.Contain("11:00 AM").And.Not.Contain("11:30 AM"));

        // A new service, done by Joy, bookable at once.
        await Page.GotoAsync($"/Services?clientConfigId={World.Spa.Id}");
        await Page.ClickAsync("[data-testid=add-service]");
        await Expect(Page.Locator("[data-testid=service-name-error]")).Not.ToBeEmptyAsync();
        await Page.FillAsync("[data-testid=service-name]", "Hot stone massage");
        await Page.FillAsync("[data-testid=service-category]", "Massage");
        await Page.FillAsync("[data-testid=service-duration]", "90");
        await Page.FillAsync("[data-testid=service-price]", "1500");
        await Page.CheckAsync("[data-testid='service-staff-Joy']");
        await Page.ClickAsync("[data-testid=add-service]");
        await Expect(Page.Locator("[data-service-name='Hot stone massage']")).ToContainTextAsync("1 hr 30 min");
        var hot = new Chat("t104b");
        await hot.SayAsync("hello");
        await hot.TapAsync("Massage");
        Assert.That(hot.Last.Cards.Select(c => c.Title), Does.Contain("Hot stone massage"));

        // Mark and a new male therapist both off on that day: "Male" doesn't offer it.
        var dow = (int)day.DayOfWeek;
        await Page.GotoAsync($"/Staff?clientConfigId={World.Spa.Id}");
        await Page.Locator("[data-staff-name='Mark'] [data-testid=edit-staff]").ClickAsync();
        // Nothing ticked as a working day is refused (it used to save a person who could never be booked).
        for (var d = 0; d < 7; d++) await Page.UncheckAsync($"[data-testid=staff-work-{d}]");
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-testid=staff-days-error]")).ToContainTextAsync("Tick at least one day");
        for (var d = 0; d < 7; d++) if (d != dow) await Page.CheckAsync($"[data-testid=staff-work-{d}]");
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Mark saved");
        // Saving the same person again with the services still ticked (this crashed on real SQL Server).
        await Page.Locator("[data-staff-name='Mark'] [data-testid=edit-staff]").ClickAsync();
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Mark saved");
        await Page.ClickAsync("[data-testid=add-staff]");
        await Page.FillAsync("[data-testid=staff-name]", "Rico");
        await Page.SelectOptionAsync("[data-testid=staff-gender]", "M");
        await Page.UncheckAsync($"[data-testid=staff-work-{dow}]");
        await Page.ClickAsync("[data-testid=save-staff]");
        await Expect(Page.Locator("[data-staff-name='Rico']")).ToBeVisibleAsync();
        var male = new Chat("t104c");
        await male.SayAsync("hello");
        await male.TapAsync("Massage");
        await male.BookAsync("Swedish massage");
        await male.TapAsync("At the spa");
        await male.TapAsync("Male");
        Assert.That(male.Last.Buttons, Does.Not.Contain(DayLabel(day)));

        // Settings: book up to 3 days ahead.
        await Page.GotoAsync($"/Bookings/Settings?clientConfigId={World.Spa.Id}");
        await Page.FillAsync("[data-testid=set-days]", "3");
        await Page.ClickAsync("[data-testid=save-settings]");
        await Expect(Page.Locator("[data-testid=alert-success]")).ToContainTextAsync("Booking settings saved");
        var hair = new Chat("t104d");
        await hair.SayAsync("hello");
        await hair.TapAsync("Hair");
        await hair.BookAsync("Haircut (women)");
        if (hair.Last.Buttons.Contains("Anyone available")) await hair.TapAsync("Anyone available");
        Assert.That(hair.Last.Buttons.Count, Is.LessThanOrEqualTo(3));
        await Page.FillAsync("[data-testid=set-days]", "14");
        await Page.ClickAsync("[data-testid=save-settings]");
    }

    [Test]
    public async Task T105_Spa_portal_user_sees_bookings_services_and_staff()
    {
        await SignInAsync(World.SpaPortalEmail, World.PortalPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Bookings", RegexOptions.IgnoreCase), new() { Timeout = 15000 });
        await Expect(Page.Locator("[data-testid=nav-bookings]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=nav-services]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=nav-staff]")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=nav-orders]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=nav-menu]")).ToHaveCountAsync(0);

        // Asking for another client's bookings still shows their own.
        await Page.GotoAsync($"/Bookings?clientConfigId={World.Bakery.Id}");
        await Expect(Page.Locator("body")).ToContainTextAsync("E2E Glow Spa");
        await Page.GotoAsync("/Staff");
        await Expect(Page.Locator("[data-staff-name='Joy']")).ToBeVisibleAsync();

        // Café pages lead to the spa's own pages; Opening hours links to Services, not Menu.
        await Page.GotoAsync("/Menu");
        await Expect(Page).ToHaveURLAsync(new Regex("/Services", RegexOptions.IgnoreCase));
        await Page.GotoAsync("/Orders");
        await Expect(Page).ToHaveURLAsync(new Regex("/Bookings", RegexOptions.IgnoreCase));
        await Page.GotoAsync("/Hours");
        await Expect(Page.Locator("[data-testid=hours-open-services]")).ToBeVisibleAsync();
    }
}
