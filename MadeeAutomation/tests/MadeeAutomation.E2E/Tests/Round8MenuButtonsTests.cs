using System.Text.RegularExpressions;
using MadeeAutomation.E2E.Infrastructure;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// Round 008: the menu-buttons chat on LINE. hello -> categories -> items with prices -> cart ->
/// "When do you want it?" -> order. Taps are answered by our code, without the AI.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Round8MenuButtonsTests : PageTest
{
    private static TestDatabase Db => GlobalSetup.Db;

    public override BrowserNewContextOptions ContextOptions() => new() { BaseURL = GlobalSetup.Settings.AdminUrl };

    /// <summary>One customer's chat with one client: send something, get the bot's next message.</summary>
    private sealed class Chat
    {
        private readonly SeededClient _client;
        public string User { get; }
        public LineCall Last { get; private set; } = null!;

        public Chat(SeededClient client, string label)
        {
            _client = client;
            User = Line.NewUser(label);
        }

        private async Task<LineCall> SendAsync(object lineEvent)
        {
            var before = GlobalSetup.Fakes.LineCallsFor(User).Count;
            await Line.SendAsync(_client, lineEvent);
            var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(User)),
                c => c.Count > before, 15000, $"a reply to {User}");
            return Last = calls[^1];
        }

        public Task<LineCall> SayAsync(string text) => SendAsync(Line.TextEvent(User, text));
        public Task<LineCall> FollowAsync() => SendAsync(Line.FollowEvent(User));
        public Task<LineCall> TapDataAsync(string data, string? picked = null) => SendAsync(Line.TapEvent(User, data, picked));

        /// <summary>Taps one of the buttons under the bot's last message.</summary>
        public Task<LineCall> TapAsync(string label, string? picked = null) => TapDataAsync(Last.DataOf(label), picked);

        /// <summary>Taps "+1" / "+2" on an item card of the bot's last message.</summary>
        public Task<LineCall> AddAsync(string item, int quantity)
        {
            var card = Last.Cards.First(c => c.Title == item);
            return TapDataAsync(card.ButtonData.First(d => d.EndsWith("q=" + quantity)));
        }

        public Task<List<Dictionary<string, object?>>> OrdersAsync() =>
            Db.RowsAsync("SELECT * FROM dbo.Orders WHERE CustomerId = @c ORDER BY CreatedAtUtc", ("@c", User));
    }

    private static int AiCalls => GlobalSetup.Fakes.ClaudeCalls.Count;

    [Test]
    public async Task T79_Hello_shows_the_menu_and_the_customer_orders_with_buttons()
    {
        var chat = new Chat(World.ButtonCafe, "t79");

        // hello: the AI greets (one call), our code adds the category buttons
        await chat.SayAsync("hello");
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "Bakery", "Drinks", "Talk to staff" }));
        var aiAfterHello = AiCalls;

        // Drinks: one card per item with its price; the limited item says how many are left
        await chat.TapAsync("Drinks");
        Assert.That(chat.Last.Text, Is.EqualTo("Drinks"));
        var cards = chat.Last.Cards;
        Assert.That(cards.Select(c => c.Title), Is.EqualTo(new[] { "Iced latte", "Thai tea" }));
        Assert.That(cards[0].Price, Is.EqualTo("฿75.00"));
        Assert.That(cards[0].Note, Is.Null);
        Assert.That(cards[0].ButtonData, Has.Count.EqualTo(2), "+1 and +2");

        // add 2 lattes, then a croissant from the other category
        await chat.AddAsync("Iced latte", 2);
        Assert.That(chat.Last.Text, Is.EqualTo("Added 2 x Iced latte.\nCart: 2 item(s), ฿150.00"));
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "Add more", "Cart (2)", "Place order" }));
        await chat.TapAsync("Add more");
        await chat.TapAsync("Other categories");
        Assert.That(chat.Last.Buttons, Does.Contain("Cart (2)"));
        await chat.TapAsync("Bakery");
        await chat.AddAsync("Croissant", 1);

        // the cart, and removing one item
        await chat.TapAsync("Cart (3)");
        Assert.That(chat.Last.Text, Is.EqualTo("Your cart:\n2 x Iced latte - ฿150.00\n1 x Croissant - ฿45.00\nTotal: ฿195.00"));
        await chat.TapAsync("Remove Croissant");
        Assert.That(chat.Last.Text, Does.StartWith("Removed Croissant.\nYour cart:\n2 x Iced latte"));

        // place order: "When do you want it?" with a date-and-time picker
        await chat.TapAsync("Place order");
        Assert.That(chat.Last.Text, Is.EqualTo("Cart: 2 item(s), ฿150.00\nWhen do you want it?"));
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "As soon as possible", "Choose a time", "Back to cart" }));
        Assert.That(chat.Last.HasDateTimePicker, Is.True);
        Assert.That(await chat.OrdersAsync(), Is.Empty, "nothing is ordered before the time is chosen");
        Assert.That(AiCalls, Is.EqualTo(aiAfterHello), "no AI call for any tap");

        // choose tomorrow 10:30 (Bangkok)
        var tomorrow = DateTime.UtcNow.AddHours(7).Date.AddDays(1);
        await chat.TapAsync("Choose a time", tomorrow.ToString("yyyy-MM-dd") + "T10:30");
        var orders = await chat.OrdersAsync();
        Assert.That(orders, Has.Count.EqualTo(1));
        var reference = (string)orders[0]["Reference"]!;
        Assert.That(chat.Last.Text, Does.StartWith($"Thank you! Your order is confirmed. Order number: {reference}\n2 x Iced latte\nFor: "));
        Assert.That(chat.Last.Text, Does.EndWith(", 10:30\nTotal: ฿150.00"));
        Assert.That(orders[0]["Total"], Is.EqualTo(150m));
        Assert.That(orders[0]["RequestedForUtc"], Is.EqualTo(tomorrow.AddHours(10.5 - 7)), "10:30 Bangkok saved as 03:30 UTC");
        Assert.That(orders[0]["CustomerMessage"], Is.EqualTo("(ordered with the menu buttons)"));

        // the shop's staff are told, as for a typed order
        var staff = await Eventually.GetAsync(
            () => Task.FromResult(GlobalSetup.Fakes.LineCalls.Where(c => c.To == World.ButtonCafeStaffLineId && c.Text.Contains(reference)).ToList()),
            m => m.Count > 0, 15000, "staff message for the cart order");
        Assert.That(staff[0].Text, Does.Contain("2 x Iced latte - ฿150.00"));

        // tapping again cannot order twice: the cart is empty
        await chat.TapDataAsync("a=when&t=now");
        Assert.That(chat.Last.Text, Does.StartWith("Your cart is empty."));
        Assert.That(await chat.OrdersAsync(), Has.Count.EqualTo(1));

        // a typed order still works, and gets a Menu button
        await chat.SayAsync("2 croissants");
        Assert.That(chat.Last.Text, Does.EndWith("Total: ฿90.00"));
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "Menu" }));
    }

    [Test]
    public async Task T80_Sold_out_and_daily_limit_are_enforced_in_the_cart()
    {
        var chat = new Chat(World.ButtonCafe, "t80");
        await chat.SayAsync("hello");

        // sold out: the card says so and has no buttons; a forged tap is refused too
        await chat.TapAsync("Bakery");
        var brownie = chat.Last.Cards.First(c => c.Title == "Brownie");
        Assert.That(brownie.Note, Is.EqualTo("Sold out today"));
        Assert.That(brownie.ButtonData, Is.Empty);
        var brownieId = (await Db.RowsAsync("SELECT Id FROM dbo.MenuItems WHERE ClientConfigId = @c AND Name = 'Brownie'", ("@c", World.ButtonCafe.Id)))[0]["Id"];
        await chat.TapDataAsync($"a=add&i={brownieId}&q=1");
        Assert.That(chat.Last.Text, Is.EqualTo("Sorry, Brownie is sold out today."));

        // daily limit: counts today's saved orders AND what is already in the cart
        await chat.TapDataAsync("a=cat&c=Drinks");
        var tea = chat.Last.Cards.First(c => c.Title == "Thai tea");
        var left = int.Parse(Regex.Match(tea.Note!, @"^(\d+) left today$").Groups[1].Value);   // 3 a day
        Assert.That(left, Is.InRange(1, 3));

        var teaId = (await Db.RowsAsync("SELECT Id FROM dbo.MenuItems WHERE ClientConfigId = @c AND Name = 'Thai tea'", ("@c", World.ButtonCafe.Id)))[0]["Id"];
        for (var i = 0; i < left; i++) await chat.TapDataAsync($"a=add&i={teaId}&q=1");
        Assert.That(chat.Last.Text, Does.StartWith("Added 1 x Thai tea."));
        await chat.TapDataAsync($"a=add&i={teaId}&q=1");   // one more than is left
        Assert.That(chat.Last.Text, Is.EqualTo("Sorry, Thai tea is fully booked today."));

        await chat.TapDataAsync("a=cat&c=Drinks");
        tea = chat.Last.Cards.First(c => c.Title == "Thai tea");
        Assert.That(tea.Note, Is.EqualTo("Fully booked today"));
        Assert.That(tea.ButtonData, Is.Empty);

        // things that are not real buttons do no harm
        await chat.TapDataAsync($"a=add&i={Guid.NewGuid()}&q=1");
        Assert.That(chat.Last.Text, Is.EqualTo("Sorry, that item is no longer on the menu."));
        await chat.TapDataAsync("a=something-else");
        Assert.That(chat.Last.Buttons, Does.Contain("Drinks"));

        // a time in the past asks again; then as soon as possible
        await chat.TapDataAsync("a=checkout");
        await chat.TapAsync("Choose a time", "2020-01-01T10:00");
        Assert.That(chat.Last.Text, Does.StartWith("That time has already passed."));
        Assert.That(await chat.OrdersAsync(), Is.Empty);
        await chat.TapAsync("As soon as possible");
        Assert.That(chat.Last.Text, Does.Contain("For: As soon as possible"));
        Assert.That(chat.Last.Text, Does.EndWith($"Total: {Money(45m * left)}"));
        Assert.That((await chat.OrdersAsync())[0]["RequestedForText"], Is.EqualTo("As soon as possible"));
    }

    private static string Money(decimal amount) => "฿" + amount.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    public async Task T81_New_friend_gets_the_menu_and_can_type_the_time()
    {
        var chat = new Chat(World.ButtonCafe, "t81");
        var ai = AiCalls;

        // adding the account as a friend: welcome + categories, no AI. Button words are English for everyone.
        await chat.FollowAsync();
        Assert.That(chat.Last.Text, Does.StartWith("Welcome to E2E Button Cafe!"));
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "Bakery", "Drinks", "Talk to staff" }));
        Assert.That(AiCalls, Is.EqualTo(ai));

        await chat.TapAsync("Bakery");
        await chat.AddAsync("Croissant", 2);
        Assert.That(chat.Last.Text, Is.EqualTo("Added 2 x Croissant.\nCart: 2 item(s), ฿90.00"));
        await chat.TapAsync("Place order");
        Assert.That(chat.Last.Buttons, Is.EqualTo(new[] { "As soon as possible", "Choose a time", "Back to cart" }));

        // a question while we wait is answered normally, and the cart is kept
        await chat.SayAsync("is there parking?");
        Assert.That(await chat.OrdersAsync(), Is.Empty);

        // the customer TYPES the time instead of tapping (one small AI call)
        await chat.SayAsync("at 11pm please");
        var order = (await chat.OrdersAsync()).Single();
        Assert.That(chat.Last.Text, Does.Contain(", 23:00"));
        Assert.That(order["RequestedForText"], Is.EqualTo("at 11pm please"));
        Assert.That(order["Total"], Is.EqualTo(90m));

        // a customer who writes Thai still gets the (English) buttons; the AI's own line is in Thai
        var thai = new Chat(World.ButtonCafe, "t81th");
        await thai.SayAsync("ขอดูเมนูหน่อย");
        Assert.That(thai.Last.Text, Is.EqualTo("ได้เลยค่ะ"));
        Assert.That(thai.Last.Buttons, Is.EqualTo(new[] { "Bakery", "Drinks", "Talk to staff" }));
    }

    [Test]
    public async Task T82_If_LINE_refuses_the_buttons_the_customer_still_gets_the_words()
    {
        // The fake LINE answers 400 to any message with buttons / cards for a customer id starting "Ubadrich".
        var chat = new Chat(World.ButtonCafe, "badrich");
        await chat.SayAsync("hello");
        var calls = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(chat.User)),
            c => c.Any(x => x.StatusReturned == 200), 15000, "the plain-text retry");
        Assert.That(calls.Any(c => c.StatusReturned == 400), Is.True, "LINE refused the buttons first");
        var delivered = calls.Last(c => c.StatusReturned == 200);
        Assert.That(delivered.Buttons, Is.Empty);
        Assert.That(delivered.Text, Is.Not.Empty);

        var log = await Waits.FinishedLogAsync(chat.User);
        Assert.That(log["Status"], Is.EqualTo(3), "the run still succeeded");

        // item cards become a text list
        var before = GlobalSetup.Fakes.LineCallsFor(chat.User).Count;
        await Line.SendAsync(World.ButtonCafe, Line.TapEvent(chat.User, "a=cat&c=Bakery"));
        var list = await Eventually.GetAsync(() => Task.FromResult(GlobalSetup.Fakes.LineCallsFor(chat.User)),
            c => c.Count > before && c[^1].StatusReturned == 200, 15000, "the text list");
        Assert.That(list[^1].Text, Does.Contain("- Croissant: ฿45.00"));
        Assert.That(list[^1].Text, Does.Contain("- Brownie: ฿60.00 (Sold out today)"));
    }

    [Test]
    public async Task T83_Menu_buttons_can_be_switched_off_and_taps_show_in_Inbound_events()
    {
        var chat = new Chat(World.ButtonOff, "t83");
        await chat.SayAsync("hello");
        Assert.That(chat.Last.Buttons, Does.Contain("Drinks"), "on by default");
        await chat.TapAsync("Drinks");

        await AdminSession.SignInAsync(Page, World.AdminEmail, World.AdminPassword);
        await Expect(Page).ToHaveURLAsync(new Regex("/Clients"));

        // the tap is marked as a button on the Inbound events page
        await Page.GotoAsync($"/InboundEvents?clientConfigId={World.ButtonOff.Id}");
        await Expect(Page.Locator("[data-testid=inbound-kind]").First).ToHaveTextAsync("button");

        // switch the menu buttons off
        await Page.GotoAsync($"/Clients/Edit/{World.ButtonOff.Id}");
        await Expect(Page.Locator("[data-testid=menu-buttons]")).ToBeCheckedAsync();
        await Page.UncheckAsync("[data-testid=menu-buttons]");
        await Page.ClickAsync("[data-testid=save-client]");

        await chat.SayAsync("hello");
        Assert.That(chat.Last.Buttons, Is.Empty);

        // a tap on an old button is ignored (no reply), and typed orders still work
        var before = GlobalSetup.Fakes.LineCallsFor(chat.User).Count;
        await Line.SendAsync(World.ButtonOff, Line.TapEvent(chat.User, "a=home"));
        await Waits.QuietPeriodAsync();
        Assert.That(GlobalSetup.Fakes.LineCallsFor(chat.User), Has.Count.EqualTo(before));

        await chat.SayAsync("2 tea");
        Assert.That(chat.Last.Text, Does.EndWith("Total: ฿90.00"));
        Assert.That(chat.Last.Buttons, Is.Empty);
    }
}
